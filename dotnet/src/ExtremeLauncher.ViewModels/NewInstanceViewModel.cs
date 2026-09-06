// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, version 3.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 *
 * Ported from the vanilla page of ui/pages/modplatform/VanillaPage.cpp and the dialog around it.
 *
 * THE LIST IS NOT GARNISH. VanillaCreationTask deliberately does not validate the version it is given
 * -- upstream does not either, because creation must work with no network and resolution happens at
 * first launch. That is only safe if the user PICKED from a list rather than typing; a text box here
 * turns a typo into an instance that fails at launch with a metadata error.
 *
 * SNAPSHOTS AND OLD VERSIONS ARE HIDDEN BY DEFAULT, as upstream hides them. The version list runs to
 * well over a thousand entries, nearly all of them 2011-era alphas and weekly snapshots, and a person
 * looking for "the latest one" should not have to find it among those.
 *
 * MOD LOADERS ARE FILTERED TWO DIFFERENT WAYS, which is upstream's rule and not a tidy one:
 *
 *   - Forge and NeoForge publish a build PER MINECRAFT VERSION, so their versions carry a `requires`
 *     on net.minecraft and are filtered by it exactly.
 *   - Fabric and Quilt do not. Their loader is broadly version-agnostic, the metadata says nothing
 *     about which Minecraft versions it supports, and upstream's own comment on this is "FIXME: dirty
 *     hack because the launcher is unaware of Fabric's dependencies". It shows EVERY loader version
 *     for Minecraft 1.14 and later, and none at all before -- 1.14 being where Fabric began.
 *
 * Reproduced as it stands, hack and all. Filtering Fabric by `requires` would show an empty list for
 * every Minecraft version, which is a worse answer than a slightly too generous one.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.ViewModels;

/// <summary>One row: a Minecraft version that can be installed.</summary>
public sealed partial class VersionViewModel : ObservableObject
{
    public required string Version { get; init; }

    /// <summary>"release", "snapshot", "old_beta" and so on, as the metadata says.</summary>
    public required string Type { get; init; }

    public DateTimeOffset Released { get; init; }

    public bool IsRecommended { get; init; }

    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);

    public string ReleasedString => Released == default
        ? string.Empty
        : Released.LocalDateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture);

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A mod loader that can be installed alongside Minecraft.</summary>
/// <param name="Uid">The metadata component id, which is what ends up in mmc-pack.json.</param>
/// <param name="FiltersByParent">
/// Whether its versions declare which Minecraft version they are for. True for Forge and NeoForge;
/// false for Fabric and Quilt, whose metadata says nothing about it.
/// </param>
public sealed record LoaderOption(string Name, string Uid, bool FiltersByParent)
{
    /// <summary>The "no loader" choice, which is what a vanilla instance is.</summary>
    public static readonly LoaderOption None = new("None", string.Empty, false);

    /// <summary>Every loader on offer, in upstream's order.</summary>
    public static IReadOnlyList<LoaderOption> All { get; } =
    [
        None,
        new("Fabric", "net.fabricmc.fabric-loader", FiltersByParent: false),
        new("Quilt", "org.quiltmc.quilt-loader", FiltersByParent: false),
        new("Forge", "net.minecraftforge", FiltersByParent: true),
        new("NeoForge", "net.neoforged", FiltersByParent: true),
    ];

    public bool IsNone => Uid.Length == 0;

    public override string ToString() => Name;
}

/// <summary>Fetches the Minecraft version list. Implemented by the app, faked by the tests.</summary>
public interface IVersionListSource
{
    Task<IReadOnlyList<MetaVersion>> LoadAsync(string uid, CancellationToken cancellationToken);
}

public sealed partial class NewInstanceViewModel : ObservableObject
{
    private readonly IVersionListSource? _versions;

    private readonly RuntimeContext _runtimeContext;

    private readonly (LauncherPaths Paths, HttpClient Client, string MetaUrl)? _resolution;

    /// <param name="resolution">
    /// What a newly created instance needs to resolve its dependencies. Null leaves the instance with
    /// only the components chosen here -- which for any modern Minecraft version will not start.
    /// </param>
    public NewInstanceViewModel(
        IVersionListSource? versions = null,
        RuntimeContext? runtimeContext = null,
        (LauncherPaths Paths, HttpClient Client, string MetaUrl)? resolution = null)
    {
        _versions = versions;
        _resolution = resolution;

        /*
         * The launch path's own derivation rather than a second one here. My first version hardcoded
         * JavaArchitecture to "64", which is wrong on a 32-bit machine and is exactly the kind of
         * duplicated rule that drifts -- LauncherService.CurrentRuntimeContext maps "x86_64"/"arm64" to
         * "64" and everything else to "32", and it is what the launcher actually launches with.
         */
        _runtimeContext = runtimeContext ?? LauncherService.CurrentRuntimeContext();
    }

    /// <summary>What the instance will be called.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The group to file it under, or empty.</summary>
    [ObservableProperty]
    private string _group = string.Empty;

    /// <summary>Whether snapshots and old versions are shown.</summary>
    [ObservableProperty]
    private bool _showAllVersions;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>What went wrong, or empty.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>The versions on offer, newest first.</summary>
    public ObservableCollection<VersionViewModel> Versions { get; } = [];

    private readonly List<VersionViewModel> _all = [];

    /// <summary>The mod loaders on offer.</summary>
    public IReadOnlyList<LoaderOption> Loaders => LoaderOption.All;

    /// <summary>Which loader is chosen. "None" makes a vanilla instance.</summary>
    [ObservableProperty]
    private LoaderOption _loader = LoaderOption.None;

    /// <summary>The chosen loader's versions, filtered for the chosen Minecraft version.</summary>
    public ObservableCollection<VersionViewModel> LoaderVersions { get; } = [];

    private readonly List<MetaVersion> _allLoaderVersions = [];

    /// <summary>Why the loader list is empty, or empty when it is not.</summary>
    [ObservableProperty]
    private string _loaderNote = string.Empty;

    /// <summary>The version of Fabric and Quilt's first release, before which neither existed.</summary>
    /// <remarks>
    /// Upstream's threshold, in upstream's "dirty hack". It is a real fact about the world rather than
    /// a guess: Fabric began with 1.14.
    /// </remarks>
    public static Core.Version FabricEra { get; } = new("1.14");

    public VersionViewModel? Selected => Versions.FirstOrDefault(v => v.IsSelected);

    [RelayCommand]
    public void Select(string? version)
    {
        foreach (var candidate in Versions)
        {
            candidate.IsSelected = version is not null && candidate.Version == version;
        }

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CanCreate));

        CreateCommand.NotifyCanExecuteChanged();

        // Forge's builds are per Minecraft version, so changing Minecraft changes what Forge offers.
        RebuildLoaderVersions();
    }

    /// <summary>
    /// Whether the instance can be created.
    /// </summary>
    /// <remarks>
    /// Both halves are required, and neither has a sensible default: an unnamed instance is a directory
    /// called nothing, and an unchosen version is the typo this list exists to prevent.
    /// </remarks>
    public bool CanCreate => Name.Trim().Length != 0
                             && Selected is not null
                             && !IsLoading

                             // A loader chosen but no version of it picked would write a component with
                             // an empty version, which resolves to nothing at first launch.
                             && (Loader.IsNone || SelectedLoaderVersion is not null);

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreate));

        CreateCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCreate));

        CreateCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowAllVersionsChanged(bool value) => Rebuild();

    partial void OnLoaderChanged(LoaderOption value)
    {
        _allLoaderVersions.Clear();
        _loaderLoad = null;

        RebuildLoaderVersions();

        OnPropertyChanged(nameof(CanCreate));

        CreateCommand.NotifyCanExecuteChanged();

        if (!value.IsNone)
        {
            _ = LoadLoaderVersionsAsync();
        }
    }

    /// <summary>The fetch already in flight for the current loader, if any.</summary>
    /// <remarks>
    /// SHARED RATHER THAN REPEATED. Setting Loader starts a fetch so the UI does not have to, and a
    /// caller that wants to await the result -- a test, or anything driving this without a combo box --
    /// naturally calls the same method. Both then ran, and both cleared and repopulated LoaderVersions
    /// while the other was walking it: a NullReferenceException out of FirstOrDefault, and only against
    /// the real metadata server, where the fetch is slow enough for the two to overlap.
    ///
    /// Returning the in-flight task to the second caller makes the two indistinguishable.
    /// </remarks>
    private Task? _loaderLoad;

    /// <summary>Fetches the chosen loader's version list.</summary>
    public Task LoadLoaderVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (_versions is null || Loader.IsNone)
        {
            return Task.CompletedTask;
        }

        // One fetch per loader, however many callers ask for it.
        return _loaderLoad ??= LoadLoaderVersionsCoreAsync(Loader, cancellationToken);
    }

    private async Task LoadLoaderVersionsCoreAsync(LoaderOption loader, CancellationToken cancellationToken)
    {

        IsLoading = true;

        try
        {
            var loaded = await _versions!.LoadAsync(loader.Uid, cancellationToken).ConfigureAwait(true);

            // The loader may have been changed while this was in flight; a late answer must not
            // repopulate the list with somebody else's versions.
            if (Loader != loader)
            {
                return;
            }

            _allLoaderVersions.Clear();
            _allLoaderVersions.AddRange(loaded);

            RebuildLoaderVersions();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            Error = $"Could not fetch {loader.Name}'s versions: {e.Message}";
        }
        finally
        {
            IsLoading = false;

            // Cleared only for the loader this fetch was for: a newer one has its own.
            if (Loader == loader)
            {
                _loaderLoad = null;
            }
        }
    }

    /// <summary>
    /// Filters the loader's versions for the chosen Minecraft version.
    /// </summary>
    /// <remarks>
    /// The two rules are upstream's, and they differ per loader -- see the note at the top of this file.
    /// </remarks>
    private void RebuildLoaderVersions()
    {
        var selectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.IsSelected)?.Version;

        LoaderVersions.Clear();
        LoaderNote = string.Empty;

        if (Loader.IsNone)
        {
            LoaderNote = "No mod loader is selected.";

            OnPropertyChanged(nameof(SelectedLoaderVersion));

            return;
        }

        if (Selected is not { } minecraft)
        {
            LoaderNote = "No Minecraft version is selected.";

            OnPropertyChanged(nameof(SelectedLoaderVersion));

            return;
        }

        IEnumerable<MetaVersion> candidates;

        if (Loader.FiltersByParent)
        {
            // Forge and NeoForge publish a build per Minecraft version and say so in `requires`.
            candidates = _allLoaderVersions.Where(
                v => v.Requires.Any(
                    r => string.Equals(r.Uid, "net.minecraft", StringComparison.Ordinal)
                         && string.Equals(r.EqualsVersion, minecraft.Version, StringComparison.Ordinal)));
        }
        else if (new Core.Version(minecraft.Version) >= FabricEra)
        {
            // Upstream's hack: every loader version, because the metadata does not say which apply.
            candidates = _allLoaderVersions;
        }
        else
        {
            candidates = [];

            LoaderNote = $"{Loader.Name} does not support Minecraft {minecraft.Version}.";
        }

        foreach (var version in candidates
                     .Select(v => new VersionViewModel
                     {
                         Version = v.VersionString,
                         Type = v.Type,
                         Released = v.Time,
                         IsRecommended = v.IsRecommended,
                     })
                     .OrderByDescending(v => v.Released))
        {
            LoaderVersions.Add(version);
        }

        if (LoaderVersions.Count == 0 && LoaderNote.Length == 0 && _allLoaderVersions.Count != 0)
        {
            LoaderNote = $"No {Loader.Name} version supports Minecraft {minecraft.Version}.";
        }

        /*
         * The recommended loader version, or the newest. Somebody choosing "Fabric" wants Fabric, not a
         * second decision about which build of it -- and leaving it unselected would make Create stay
         * disabled with no obvious reason why.
         */
        var restored = selectedLoaderVersion is not null
            ? LoaderVersions.FirstOrDefault(v => v.Version == selectedLoaderVersion)
            : null;

        var chosen = restored
                     ?? LoaderVersions.FirstOrDefault(v => v.IsRecommended)
                     ?? LoaderVersions.FirstOrDefault();

        SelectLoaderVersion(chosen?.Version);
    }

    /// <summary>The chosen loader version, or null.</summary>
    public VersionViewModel? SelectedLoaderVersion => LoaderVersions.FirstOrDefault(v => v.IsSelected);

    [RelayCommand]
    public void SelectLoaderVersion(string? version)
    {
        foreach (var candidate in LoaderVersions)
        {
            candidate.IsSelected = version is not null && candidate.Version == version;
        }

        OnPropertyChanged(nameof(SelectedLoaderVersion));
        OnPropertyChanged(nameof(CanCreate));

        CreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Fetches the version list.</summary>
    public async Task LoadVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (_versions is null)
        {
            Error = "This build cannot fetch the version list.";

            return;
        }

        IsLoading = true;
        Error = string.Empty;

        try
        {
            var loaded = await _versions.LoadAsync("net.minecraft", cancellationToken).ConfigureAwait(true);

            _all.Clear();

            foreach (var version in loaded)
            {
                _all.Add(new VersionViewModel
                {
                    Version = version.VersionString,
                    Type = version.Type,
                    Released = version.Time,
                    IsRecommended = version.IsRecommended,
                });
            }

            Rebuild();

            /*
             * The recommended version is selected to begin with -- that is the latest release, and it
             * is what somebody making a new instance wants far more often than anything else. Upstream
             * does the same.
             */
            if (Versions.FirstOrDefault(v => v.IsRecommended) is { } recommended)
            {
                Select(recommended.Version);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            /*
             * A named failure rather than an empty list. "No versions" reads as "Minecraft has no
             * versions", which is never true -- what happened is that the metadata server could not be
             * reached, and that is something a person can act on.
             */
            Error = $"Could not fetch the version list: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Rebuild()
    {
        var selected = Selected?.Version;

        Versions.Clear();

        foreach (var version in _all.Where(v => ShowAllVersions || v.IsRelease)
                     .OrderByDescending(v => v.Released))
        {
            Versions.Add(version);
        }

        if (selected is not null)
        {
            Select(selected);
        }

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool IsEmpty => Versions.Count == 0;

    /// <summary>
    /// Creates the instance.
    /// </summary>
    /// <returns>The new instance's id, or empty when nothing was made.</returns>
    [RelayCommand]
    public async Task<string> CreateAsync(InstanceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!CanCreate || Selected is not { } version)
        {
            return string.Empty;
        }

        IsLoading = true;
        Error = string.Empty;

        try
        {
            var creation = new VanillaCreationTask(
                Name.Trim(),
                version.Version,
                _runtimeContext,
                Loader.IsNone ? string.Empty : Loader.Uid,
                SelectedLoaderVersion?.Version ?? string.Empty,
                Group.Trim(),

                // Passed through so the new instance resolves what it needs -- without this it
                // records only Minecraft and a loader, and cannot start. See ComponentResolution.
                paths: _resolution?.Paths,
                client: _resolution?.Client,
                metaUrl: _resolution?.MetaUrl ?? string.Empty);

            var staging = new InstanceStagingTask(list, creation, creation);

            // Off the UI thread, like a copy: creating writes files and commits a directory.
            if (!await Task.Run(() => staging.RunAsync()).ConfigureAwait(true))
            {
                Error = staging.FailReason.Length != 0
                    ? $"Could not create the instance: {staging.FailReason}"
                    : "Could not create the instance.";

                return string.Empty;
            }

            // The id is the DIRECTORY the commit settled on, which is not the name when something
            // already occupied it.
            return staging.CommittedId;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
