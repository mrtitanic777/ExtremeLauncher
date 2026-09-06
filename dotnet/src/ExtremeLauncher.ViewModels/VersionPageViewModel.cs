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
 * Ported from the decision-making half of ui/pages/instance/VersionPage.cpp.
 *
 * THIS IS THE PAGE THAT BREAKS INSTANCES. Every other page edits files the game reads; this one edits
 * what the game *is* -- remove the wrong row and a working modpack stops launching. Upstream guards
 * that with `updateButtons()`, forty-odd lines re-deriving eight buttons' enabled states from the
 * current selection, called from six places. Missing one call is a button that acts on a stale row.
 *
 * Here each button is a PROPERTY of the selection, worked out once. The rules are upstream's, read off
 * VersionPage::updateButtons() and Component.cpp rather than from memory -- I wrote two of them from
 * memory first and both were wrong:
 *
 *   - REMOVE is `!important`. Minecraft itself cannot go; an instance without it is not an instance.
 *     A dependency-only component IS removable, because removing whatever pulled it in is how a user
 *     gets rid of it.
 *   - CHANGE VERSION is "the version list has entries" -- NOT "is not custom", which is what I assumed.
 *     Minecraft cannot be removed and is the single most likely thing to be re-versioned.
 *   - MOVE is `isMoveable()`, which upstream hardcodes to `true` under its own "HACK, FIXME: this was
 *     too dumb and wouldn't follow dependency constraints anyway". See CanMoveUp for what this port
 *     does instead, and why.
 *
 * The list is rebuilt from the profile after every edit rather than patched in place. A component's
 * problems change when its neighbours change -- removing Fabric makes every Fabric mod's requirement
 * unsatisfied -- so anything short of a re-read shows a stale verdict.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.ViewModels;

/// <summary>One row: a component of the instance.</summary>
public sealed partial class ComponentViewModel : ObservableObject
{
    public required string Uid { get; init; }

    public required string Name { get; init; }

    /// <summary>What the row shows in the version column.</summary>
    /// <remarks>
    /// A component with no version is one whose metadata has not loaded. Blank would read as "no
    /// version", which is a different and more alarming thing than "not known yet".
    /// </remarks>
    public required string Version { get; init; }

    /// <summary>Whether the launcher will not let this be removed — Minecraft itself, chiefly.</summary>
    public bool IsImportant { get; init; }

    /// <summary>Whether this is here only because something else asked for it.</summary>
    public bool IsDependencyOnly { get; init; }

    /// <summary>Whether the user has edited this component's JSON by hand.</summary>
    public bool IsCustom { get; init; }

    /// <summary>Whether a version list exists to choose a different version from.</summary>
    /// <remarks>
    /// Upstream's `isVersionChangeable(false)`: the list exists and is not empty. It is NOT "is not
    /// custom" -- that was my own guess, and it is a different question with a different answer.
    /// </remarks>
    public bool IsVersionChangeable { get; init; }

    public bool IsDisabled { get; init; }

    /// <summary>The worst thing wrong with this component.</summary>
    public ProblemSeverity Severity { get; init; }

    /// <summary>Everything wrong with it, one per line, or empty.</summary>
    public string Problems { get; init; } = string.Empty;

    public bool HasProblems => Problems.Length != 0;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Asks the user to pick a version of a component. Implemented by the app.</summary>
public interface IVersionChooser
{
    /// <param name="minecraftVersion">Filters loader builds; empty lists everything.</param>
    /// <returns>The chosen version, or empty when cancelled.</returns>
    Task<string> ChooseAsync(string uid, string title, string minecraftVersion);
}

/// <summary>The uid and name for a new empty component.</summary>
public sealed record NewComponentChoice(string Uid, string Name);

/// <summary>Asks for a new component's uid and name. Implemented by the app's dialog.</summary>
public interface INewComponentPrompt
{
    /// <param name="existingUids">Uids already in the profile, which the new one may not clash with.</param>
    /// <returns>The choice, or null when cancelled.</returns>
    Task<NewComponentChoice?> AskAsync(IReadOnlyList<string> existingUids);
}

public sealed partial class VersionPageViewModel : ObservableObject, IInstancePage
{
    private PackProfile? _profile;

    private string _path = string.Empty;

    private readonly IVersionChooser? _chooser;

    private readonly IPackUpdateChecker? _updates;

    private readonly IPackUpdater? _updater;

    private InstanceSettings? _settings;

    /// <param name="chooser">
    /// Asks the user to pick a version. Null in a build with no dialog to show, where Change version
    /// and Add loader stay disabled rather than becoming buttons that do nothing.
    /// </param>
    private readonly IFolderOpener? _folders;

    private readonly INewComponentPrompt? _newComponent;

    public VersionPageViewModel(
        IVersionChooser? chooser = null,
        IPackUpdateChecker? updates = null,
        IPackUpdater? updater = null,
        IFolderOpener? folders = null,
        INewComponentPrompt? newComponent = null)
    {
        _chooser = chooser;
        _updates = updates;
        _updater = updater;
        _folders = folders;
        _newComponent = newComponent;
    }

    /// <summary>The instance's patches folder, beside its mmc-pack.json.</summary>
    private string PatchesDirectory =>
        _path.Length == 0 ? string.Empty : Core.FileSystem.PathCombine(System.IO.Path.GetDirectoryName(_path)!, "patches");

    private string _gameRoot = string.Empty;

    private string _libraryPath = string.Empty;

    /// <summary>What the page list calls this page.</summary>
    public string Title => "Version";

    /// <summary>The components, in the order they are applied.</summary>
    public ObservableCollection<ComponentViewModel> Components { get; } = [];

    /// <summary>Set when the profile has been changed and not yet written out.</summary>
    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>Where this instance came from, or empty when nobody imported it.</summary>
    /// <remarks>
    /// SHOWN ON THIS PAGE because this is where somebody comes to ask "what IS this instance" -- and
    /// "Minecraft 1.20.1, Fabric 0.15.7" answers a different question from "Fabulously Optimized
    /// 5.9.2", which is what they actually installed.
    /// </remarks>
    [ObservableProperty]
    private string _packProvenance = string.Empty;

    /// <summary>Why the pack cannot be checked for updates, or empty when it can be.</summary>
    [ObservableProperty]
    private string _packUpdateNote = string.Empty;

    public bool HasPackProvenance => PackProvenance.Length != 0;

    partial void OnPackProvenanceChanged(string value) => OnPropertyChanged(nameof(HasPackProvenance));

    /// <summary>What the last update check found, or empty.</summary>
    [ObservableProperty]
    private string _packUpdateStatus = string.Empty;

    [ObservableProperty]
    private bool _isCheckingForPackUpdate;

    /// <summary>Whether the Check for updates button does anything.</summary>
    public bool CanCheckForPackUpdate
        => _updates is not null && _settings is { } settings && settings.CanCheckForPackUpdates && !IsCheckingForPackUpdate;

    partial void OnIsCheckingForPackUpdateChanged(bool value) => OnPropertyChanged(nameof(CanCheckForPackUpdate));

    /// <summary>Asks the platform whether a newer version of this pack exists.</summary>
    /// <remarks>
    /// ON DEMAND, NOT ON OPENING THE WINDOW. Upstream's managed-pack page fetches the version list as
    /// soon as you look at it; here it is a button, because opening the Version page is something
    /// people do to read the component list and it should not cost a network request every time.
    /// </remarks>
    [RelayCommand]
    public async Task CheckForPackUpdateAsync()
    {
        if (!CanCheckForPackUpdate || _settings is not { } settings || _updates is null)
        {
            return;
        }

        IsCheckingForPackUpdate = true;
        PackUpdateStatus = "Checking…";

        try
        {
            var result = await _updates
                .CheckAsync(settings.ManagedPackId, settings.ManagedPackVersionId, settings.ManagedPackVersionName)
                .ConfigureAwait(true);

            PackUpdateStatus = result.Message;

            // Held so the Update button knows what it would be installing. Cleared when there is
            // nothing to offer, so the button cannot outlive the answer that produced it.
            Offered = result.Available ? result.Newest : null;
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            // A failed check is not a failed instance. Said plainly and dropped.
            PackUpdateStatus = $"Could not check: {e.Message}";
        }
        finally
        {
            IsCheckingForPackUpdate = false;
        }
    }

    /// <summary>The version the last check offered, or null.</summary>
    [ObservableProperty]
    private IndexedVersion? _offered;

    partial void OnOfferedChanged(IndexedVersion? value) => OnPropertyChanged(nameof(CanApplyPackUpdate));

    [ObservableProperty]
    private bool _isApplyingPackUpdate;

    partial void OnIsApplyingPackUpdateChanged(bool value) => OnPropertyChanged(nameof(CanApplyPackUpdate));

    /// <summary>Whether there is an offered version and something able to install it.</summary>
    public bool CanApplyPackUpdate => Offered is not null && _updater is not null && !IsApplyingPackUpdate;

    /// <summary>What the Update button says, so it names the version rather than being generic.</summary>
    public string ApplyPackUpdateLabel
        => Offered is { } offered ? $"Update to {offered.Version}" : "Update";

    partial void OnOfferedChanged(IndexedVersion? oldValue, IndexedVersion? newValue)
        => OnPropertyChanged(nameof(ApplyPackUpdateLabel));

    /// <summary>Installs the version the last check offered.</summary>
    /// <remarks>
    /// THE PLAN GOES IN FRONT OF SOMEBODY FIRST. PackUpdateTask is deliberately split into working
    /// out what it would do and doing it, and this is the join: the summary and a sample of what
    /// would be deleted are shown, and nothing happens until somebody says yes.
    /// </remarks>
    [RelayCommand]
    public async Task ApplyPackUpdateAsync()
    {
        if (!CanApplyPackUpdate || _settings is not { } settings || Offered is not { } version)
        {
            return;
        }

        IsApplyingPackUpdate = true;
        PackUpdateStatus = "Working out what would change…";

        try
        {
            var outcome = await _updater!.UpdateAsync(version).ConfigureAwait(true);

            PackUpdateStatus = outcome.Message;

            if (outcome.Updated)
            {
                // The offer is spent. Leaving it would let somebody press Update twice and diff
                // against a version that is now the installed one.
                Offered = null;

                /*
                 * AND THE COMPONENT LIST IS NOW STALE. A pack that bumped its Fabric version has
                 * rewritten mmc-pack.json underneath this page, so without re-reading it the window
                 * shows the old loader version until somebody closes and reopens it -- an instance
                 * that is correct on disk and wrong on screen, which is the confusing way round.
                 */
                ReloadFromDisk();

                LoadProvenance(settings);

                PackUpdateStatus = outcome.Message;
            }
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            PackUpdateStatus = $"Could not update: {e.Message}";
        }
        finally
        {
            IsApplyingPackUpdate = false;
        }
    }

    /// <summary>Reads where this instance came from, if anywhere.</summary>
    public void LoadProvenance(InstanceSettings? settings)
    {
        _settings = settings;

        PackUpdateStatus = string.Empty;
        Offered = null;

        OnPropertyChanged(nameof(CanCheckForPackUpdate));

        if (settings is null || !settings.IsManagedPack)
        {
            PackProvenance = string.Empty;
            PackUpdateNote = string.Empty;

            return;
        }

        var platform = settings.ManagedPackType switch
        {
            "modrinth" => "Modrinth",
            "flame" => "CurseForge",
            "atlauncher" => "ATLauncher",
            "" => "a modpack",
            var other => other,
        };

        var name = settings.ManagedPackName.Length != 0 ? settings.ManagedPackName : "an unnamed pack";

        PackProvenance = settings.ManagedPackVersionName.Length != 0
            ? $"Imported from the {platform} pack \"{name}\", version {settings.ManagedPackVersionName}."
            : $"Imported from the {platform} pack \"{name}\".";

        /*
         * SAID OUT LOUD, because the alternative is somebody waiting for an update button that is
         * never going to appear. A .mrpack on disk does not carry its own project id, so there is
         * nothing to check against -- and that is a property of how it was imported, not a fault.
         */
        PackUpdateNote = settings.CanCheckForPackUpdates
            ? string.Empty
            : "This pack was imported from a file, so it carries no link back to the platform and "
              + "cannot be checked for newer versions.";
    }

    /// <summary>Reads a profile into the page.</summary>
    /// <param name="path">Where to write it back — the instance's mmc-pack.json.</param>
    public void Load(PackProfile profile, string path = "", string gameRoot = "", string libraryPath = "")
    {
        ArgumentNullException.ThrowIfNull(profile);

        _profile = profile;
        _path = path;
        _gameRoot = gameRoot;
        _libraryPath = libraryPath;

        Rebuild();

        // The folder buttons depend on the paths, empty until now.
        OnPropertyChanged(nameof(CanOpenMinecraftFolder));
        OnPropertyChanged(nameof(CanOpenLibrariesFolder));
        OnPropertyChanged(nameof(CanAddEmpty));
        OpenMinecraftFolderCommand.NotifyCanExecuteChanged();
        OpenLibrariesFolderCommand.NotifyCanExecuteChanged();
        AddEmptyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-reads the profile from disk on the user's command (upstream's Reload).</summary>
    [RelayCommand]
    public void Reload() => ReloadFromDisk();

    /// <summary>Whether the instance's .minecraft folder can be opened.</summary>
    public bool CanOpenMinecraftFolder => _folders is not null && _gameRoot.Length != 0;

    /// <summary>Opens the instance's .minecraft folder (upstream's Minecraft folder action).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenMinecraftFolder))]
    public async Task OpenMinecraftFolderAsync()
    {
        if (_folders is null || _gameRoot.Length == 0)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(_gameRoot);

        await _folders.OpenAsync(_gameRoot).ConfigureAwait(true);
    }

    /// <summary>Whether the instance's local libraries folder can be opened.</summary>
    public bool CanOpenLibrariesFolder => _folders is not null && _libraryPath.Length != 0;

    /// <summary>Opens the instance's local libraries folder (upstream's Libraries folder action).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenLibrariesFolder))]
    public async Task OpenLibrariesFolderAsync()
    {
        if (_folders is null || _libraryPath.Length == 0)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(_libraryPath);

        await _folders.OpenAsync(_libraryPath).ConfigureAwait(true);
    }

    /// <summary>Whether a new empty component can be added.</summary>
    public bool CanAddEmpty => _newComponent is not null && _profile is not null && _path.Length != 0;

    /// <summary>Adds a new empty custom component -- upstream's Add Empty.</summary>
    /// <remarks>
    /// The uid and name come from the dialog, which is told the uids already present so it can refuse a
    /// clash. The change is written like the others here: the patch is created now, the component-list
    /// save deferred to close, so a reordering plus this commit together.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanAddEmpty))]
    public async Task AddEmptyAsync()
    {
        if (_newComponent is null || _profile is null || _path.Length == 0)
        {
            return;
        }

        var existing = _profile.Components.Select(c => c.Uid).ToArray();

        var choice = await _newComponent.AskAsync(existing).ConfigureAwait(true);

        if (choice is null)
        {
            return;
        }

        if (_profile.InstallEmpty(choice.Uid.Trim(), choice.Name.Trim(), PatchesDirectory))
        {
            HasUnsavedChanges = true;

            Rebuild();
        }
    }

    /// <summary>Re-reads the profile from disk, after something else has rewritten it.</summary>
    /// <returns>False when there is no path to read, or the file will not parse.</returns>
    public bool ReloadFromDisk()
    {
        if (_path.Length == 0 || _profile is null)
        {
            return false;
        }

        var reloaded = new PackProfile(_profile.RuntimeContext);

        if (!reloaded.Load(_path))
        {
            /*
             * Left showing what it was showing. A page that empties itself because a re-read failed
             * is worse than one that is briefly out of date -- and the instance on disk is fine.
             */
            return false;
        }

        Load(reloaded, _path, _gameRoot, _libraryPath);

        return true;
    }

    public ComponentViewModel? Selected => Components.FirstOrDefault(c => c.IsSelected);

    /// <summary>Selects a component by uid, or clears the selection.</summary>
    [RelayCommand]
    public void Select(string? uid)
    {
        foreach (var component in Components)
        {
            component.IsSelected = uid is not null && component.Uid == uid;
        }

        RaiseSelectionDependent();
    }

    // ================================================================== what the buttons may do

    public bool HasSelection => Selected is not null;

    /// <summary>
    /// Whether the selected component can be removed.
    /// </summary>
    /// <remarks>
    /// Upstream's rule, which is narrower than it looks: `important` components cannot go. That is
    /// Minecraft itself -- an instance without it is not an instance. A DEPENDENCY-ONLY component is
    /// removable, because removing whatever pulled it in is how a user gets rid of it.
    /// </remarks>
    public bool CanRemove => Selected is { IsImportant: false };

    /// <summary>Whether the selected component's version can be changed.</summary>
    /// <remarks>
    /// Upstream's rule is that a version list exists with entries in it. True for Minecraft, which
    /// cannot be removed and is the single most likely thing to be re-versioned.
    /// </remarks>
    public bool CanChangeVersion => Selected is { IsVersionChangeable: true };

    /// <summary>
    /// Whether the selected component can move up.
    /// </summary>
    /// <remarks>
    /// DIVERGES FROM UPSTREAM, deliberately. `Component::isMoveable()` returns a hardcoded `true`,
    /// under its own comment: "HACK, FIXME: this was too dumb and wouldn't follow dependency
    /// constraints anyway." So upstream enables Move Up on the top row -- a button that cannot do what
    /// it says, which is the thing this port has been removing everywhere else.
    ///
    /// Guarding on position is also what keeps upstream bug #19 out of reach; see PORTING.md.
    /// </remarks>
    public bool CanMoveUp => Selected is not null && Components.IndexOf(Selected) > 0;

    /// <inheritdoc cref="CanMoveUp"/>
    public bool CanMoveDown => Selected is not null && Components.IndexOf(Selected) < Components.Count - 1;

    /// <summary>Whether anything at all is wrong with the instance as it stands.</summary>
    public bool HasProblems => Components.Any(c => c.HasProblems);

    /// <summary>The worst severity across every component, for the page's own warning strip.</summary>
    public ProblemSeverity WorstSeverity
        => Components.Count == 0 ? ProblemSeverity.None : Components.Max(c => c.Severity);

    // ================================================================== editing

    /// <summary>Removes the selected component.</summary>
    /// <remarks>
    /// Guarded by the same property the button binds to. Removing the wrong component leaves an
    /// instance that will not start, and the user's next move is usually to try launching it.
    /// </remarks>
    [RelayCommand]
    public void RemoveSelected()
    {
        if (!CanRemove || Selected is not { } selected || _profile is null)
        {
            return;
        }

        if (_profile.Remove(selected.Uid))
        {
            HasUnsavedChanges = true;

            Rebuild();
        }
    }

    /// <summary>
    /// Whether a version can actually be chosen for the selection right now.
    /// </summary>
    /// <remarks>
    /// DIVERGES FROM UPSTREAM, deliberately, and NOT gated on <see cref="CanChangeVersion"/>.
    ///
    /// Upstream's `isVersionChangeable` is `VersionList is { Versions.Count: > 0 }` -- the list must
    /// already be in memory, because its dialog is handed one rather than fetching it. This port's
    /// dialog loads the list itself through IVersionListSource, so that gate would disable a button
    /// that would work perfectly: an instance whose metadata index has not been walked yet has no
    /// version list attached to any component, and Change version would be permanently dead.
    ///
    /// A CUSTOM component is still refused. It has a local patch file that IS its definition, so
    /// "which published version is this" has no answer -- upstream reaches the same result because a
    /// custom component carries no version list either.
    /// </remarks>
    public bool CanPickVersion => _chooser is not null && Selected is { IsCustom: false };

    /// <summary>Whether a loader can be added to this instance.</summary>
    public bool CanAddLoader => _chooser is not null && _profile is not null && MinecraftVersion.Length != 0;

    /// <summary>The instance's Minecraft version, which every loader list is filtered against.</summary>
    public string MinecraftVersion => Components
        .FirstOrDefault(c => string.Equals(c.Uid, "net.minecraft", StringComparison.Ordinal))
        ?.Version ?? string.Empty;

    /// <summary>Changes the selected component's version.</summary>
    [RelayCommand]
    public async Task ChangeVersionAsync()
    {
        if (!CanPickVersion || Selected is not { } selected || _profile is null)
        {
            return;
        }

        /*
         * NOT filtered by Minecraft version when the component IS Minecraft -- it requires nothing,
         * and filtering it against itself would leave a list holding only the version already set.
         */
        var isMinecraft = string.Equals(selected.Uid, "net.minecraft", StringComparison.Ordinal);

        var chosen = await _chooser!.ChooseAsync(
            selected.Uid,
            $"Change {selected.Name} version",
            isMinecraft ? string.Empty : MinecraftVersion).ConfigureAwait(true);

        if (chosen.Length == 0)
        {
            return;
        }

        Apply(selected.Uid, chosen, important: isMinecraft);
    }

    /// <summary>Adds a mod loader to the instance.</summary>
    [RelayCommand]
    public async Task AddLoaderAsync(string? uid)
    {
        if (!CanAddLoader || uid is not { Length: > 0 } loaderUid || _profile is null)
        {
            return;
        }

        var chosen = await _chooser!.ChooseAsync(
            loaderUid,
            "Choose a loader version",
            MinecraftVersion).ConfigureAwait(true);

        if (chosen.Length == 0)
        {
            return;
        }

        /*
         * SetComponentVersion both adds and replaces, so installing a loader over an existing one is
         * the same call -- which is what upstream's InstallLoaderDialog does, and why it does not need
         * to remove the old one first.
         */
        Apply(loaderUid, chosen, important: false);
    }

    private void Apply(string uid, string version, bool important)
    {
        _profile!.SetComponentVersion(uid, version, important);

        HasUnsavedChanges = true;

        Rebuild();
    }

    [RelayCommand]
    public void MoveSelectedUp() => Move(PackProfile.MoveDirection.Up, CanMoveUp);

    [RelayCommand]
    public void MoveSelectedDown() => Move(PackProfile.MoveDirection.Down, CanMoveDown);

    private void Move(PackProfile.MoveDirection direction, bool allowed)
    {
        if (!allowed || Selected is not { } selected || _profile is null)
        {
            return;
        }

        var index = _profile.IndexOf(selected.Uid);

        if (index < 0)
        {
            return;
        }

        _profile.Move(index, direction);

        HasUnsavedChanges = true;

        // Re-read, then put the selection back on the component that moved rather than on whatever
        // row now sits where it used to be.
        Rebuild();
        Select(selected.Uid);
    }

    /// <summary>Writes the profile back to disk.</summary>
    /// <returns>False when the file could not be written, which the window must not ignore.</returns>
    public bool Save()
    {
        if (_profile is null || _path.Length == 0)
        {
            // Nothing loaded, or loaded without a path to write to. Not an error, and not a save.
            return !HasUnsavedChanges;
        }

        if (!_profile.Save(_path))
        {
            return false;
        }

        HasUnsavedChanges = false;

        return true;
    }

    /// <summary>Re-reads the rows from the profile.</summary>
    /// <remarks>
    /// Rebuilt wholesale rather than patched. A component's problems depend on its neighbours --
    /// removing Fabric makes every Fabric mod's requirement unsatisfied -- so touching one row and
    /// leaving the rest would leave stale verdicts on screen.
    /// </remarks>
    private void Rebuild()
    {
        var selected = Selected?.Uid;

        Components.Clear();

        if (_profile is null)
        {
            RaiseSelectionDependent();

            return;
        }

        foreach (var component in _profile.Components)
        {
            var problems = component.GetProblems();

            Components.Add(new ComponentViewModel
            {
                Uid = component.Uid,
                Name = component.Name,

                // "not loaded" rather than blank, which would read as "no version".
                Version = component.Version.Length != 0 ? component.Version : "(not loaded)",
                IsImportant = component.IsImportant,
                IsDependencyOnly = component.IsDependencyOnly,
                IsCustom = component.IsCustom,
                IsVersionChangeable = component.IsVersionChangeable,
                IsDisabled = component.IsDisabled,
                Severity = component.GetProblemSeverity(),
                Problems = string.Join(Environment.NewLine, problems.Select(p => p.Description)),
            });
        }

        // Kept across the rebuild where it still exists; a removed component's selection simply goes.
        if (selected is not null)
        {
            Select(selected);
        }

        RaiseSelectionDependent();
    }

    private void RaiseSelectionDependent()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanChangeVersion));

        /*
         * ANNOUNCED, not merely computed. These are bound to IsEnabled, and a computed property that
         * nothing raises leaves a button stuck in whatever state it was first evaluated in -- the
         * exact bug the accounts window shipped with for an hour, where every value assertion passed
         * because reading the property recomputes it.
         */
        OnPropertyChanged(nameof(CanPickVersion));
        OnPropertyChanged(nameof(CanAddLoader));
        OnPropertyChanged(nameof(MinecraftVersion));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(WorstSeverity));

        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedUpCommand.NotifyCanExecuteChanged();
        MoveSelectedDownCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>What came of trying to update.</summary>
public sealed record PackUpdateOutcome(bool Updated, string Message);

/// <summary>
/// Installs a newer version of an instance's modpack, asking first. Implemented by the app.
/// </summary>
/// <remarks>
/// SEPARATE FROM IPackUpdateChecker because the two are different promises. Checking is a read and
/// costs a request; updating deletes files in a folder somebody has been playing in, and a build
/// with no way to ask a question must not be able to do it at all.
/// </remarks>
public interface IPackUpdater
{
    Task<PackUpdateOutcome> UpdateAsync(IndexedVersion version);
}

/// <summary>Asks a platform whether a modpack has a newer version. Implemented by the app.</summary>
public interface IPackUpdateChecker
{
    /// <param name="packId">The platform's project id.</param>
    /// <param name="versionId">The platform's id for the installed version.</param>
    /// <param name="versionName">What the installed version calls itself, for the message.</param>
    Task<PackUpdateResult> CheckAsync(string packId, string versionId, string versionName);
}
