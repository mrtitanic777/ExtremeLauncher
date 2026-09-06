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
 * Ported in behaviour from launcher/ui/pages/modplatform/modrinth/ModrinthPage.cpp.
 *
 * FINDING A MODPACK WITHOUT LEAVING THE LAUNCHER. Until now the only way to get one in was to find it
 * in a web browser, download the .mrpack by hand, and import the file -- which works, and loses the
 * one thing that matters afterwards: a file carries no project id, so nothing can ever tell you a
 * newer version exists.
 *
 * ONE PACK, ONE VERSION, unlike the mod browser's basket. Installing two modpacks at once is not a
 * thing anybody wants: each is a whole instance, and the choice of WHICH VERSION is the real decision
 * -- Fabulously Optimized alone offers 463 of them.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.ViewModels;

/// <summary>One modpack in the result list.</summary>
public sealed partial class PackResultViewModel(IndexedPack pack) : ObservableObject
{
    public IndexedPack Pack { get; } = pack;

    public string Name => Pack.Name;

    public string Description => Pack.Description;

    public string Authors => Pack.Authors.Count == 0
        ? string.Empty
        : "by " + string.Join(", ", Pack.Authors.Select(a => a.Name));

    /// <summary>Every published version, newest first.</summary>
    public ObservableCollection<PackVersionViewModel> Versions { get; } = [];

    [ObservableProperty]
    private bool _isLoadingVersions;

    public bool VersionsLoaded { get; set; }
}

/// <summary>One release of a pack, as the list shows it.</summary>
public sealed class PackVersionViewModel(IndexedVersion version)
{
    public IndexedVersion Version { get; } = version;

    /// <summary>
    /// What the row says.
    /// </summary>
    /// <remarks>
    /// THE MINECRAFT VERSION IS THE POINT. A pack's own version number ("14.0.0-beta.6") tells nobody
    /// which Minecraft it is for, and that is the only question most people are actually asking. The
    /// live list shows exactly why: every one of Fabulously Optimized's newest releases is a beta for
    /// a snapshot, and somebody wanting to play 1.20.1 has to scroll a long way past them.
    /// </remarks>
    public string Label
    {
        get
        {
            var mc = Version.McVersion.Count != 0 ? $" for Minecraft {string.Join(", ", Version.McVersion)}" : string.Empty;

            var kind = Version.VersionType switch
            {
                VersionType.Beta => "  (beta)",
                VersionType.Alpha => "  (alpha)",
                _ => string.Empty,
            };

            return $"{Version.Version}{mc}{kind}";
        }
    }

    /// <summary>Whether this is a stable release rather than a beta or an alpha.</summary>
    public bool IsRelease => Version.VersionType == VersionType.Release;
}

public sealed partial class PackBrowserViewModel : ObservableObject
{
    private readonly IResourceSearch? _search;

    private int _searchGeneration;

    public PackBrowserViewModel(IResourceSearch? search = null)
    {
        _search = search;

        Selection.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanInstall));
    }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    /// <summary>What to call the new instance. Empty means the pack's own name.</summary>
    [ObservableProperty]
    private string _instanceName = string.Empty;

    /// <summary>
    /// Whether to hide betas and alphas.
    /// </summary>
    /// <remarks>
    /// ON BY DEFAULT, because of what the live data looks like: Fabulously Optimized's four newest
    /// releases are all betas for a Minecraft snapshot, so an unfiltered list opens on versions almost
    /// nobody wants. It is a checkbox rather than a hard rule because pack authors do ship long-lived
    /// betas, and somebody looking for one should not have to think the launcher is hiding it.
    /// </remarks>
    [ObservableProperty]
    private bool _releasesOnly = true;

    public ObservableCollection<PackResultViewModel> Results { get; } = [];

    /// <summary>The rows currently on screen for the highlighted pack, after filtering.</summary>
    public ObservableCollection<PackVersionViewModel> Selection { get; } = [];

    [ObservableProperty]
    private PackResultViewModel? _selectedPack;

    [ObservableProperty]
    private PackVersionViewModel? _selectedVersion;

    public bool CanSearch => _search is not null && !IsSearching;

    public bool CanInstall => SelectedPack is not null && SelectedVersion is not null;

    partial void OnSelectedVersionChanged(PackVersionViewModel? value) => OnPropertyChanged(nameof(CanInstall));

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(CanSearch));

    partial void OnReleasesOnlyChanged(bool value) => RebuildVersionList();

    /// <summary>What the caller installs, once the window has been accepted.</summary>
    public (IndexedPack Pack, IndexedVersion Version)? Chosen { get; private set; }

    /// <summary>Searches Modrinth for modpacks.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    public async Task SearchAsync()
    {
        if (_search is null)
        {
            return;
        }

        var generation = ++_searchGeneration;

        IsSearching = true;
        Status = "Searching…";

        try
        {
            var found = await _search.SearchAsync(ResourceProvider.Modrinth, SearchText, CancellationToken.None)
                .ConfigureAwait(true);

            // Dropped if stale, for the same reason as the mod browser: a shorter query's response is
            // bigger and can land after a longer one's.
            if (generation != _searchGeneration)
            {
                return;
            }

            Results.Clear();
            SelectedPack = null;
            Selection.Clear();
            SelectedVersion = null;

            foreach (var pack in found)
            {
                Results.Add(new PackResultViewModel(pack));
            }

            Status = Results.Count == 0 ? "No modpacks matched." : string.Empty;
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            if (generation == _searchGeneration)
            {
                Status = $"Search failed: {e.Message}";
            }
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>Fetches the highlighted pack's versions, once.</summary>
    public async Task LoadVersionsAsync(PackResultViewModel? row)
    {
        Selection.Clear();
        SelectedVersion = null;

        if (row is null || _search is null)
        {
            return;
        }

        if (row.VersionsLoaded)
        {
            RebuildVersionList();

            return;
        }

        if (row.IsLoadingVersions)
        {
            return;
        }

        row.IsLoadingVersions = true;

        try
        {
            await _search.LoadVersionsAsync(row.Pack, CancellationToken.None).ConfigureAwait(true);

            row.Versions.Clear();

            foreach (var version in row.Pack.Versions)
            {
                row.Versions.Add(new PackVersionViewModel(version));
            }

            row.VersionsLoaded = true;

            RebuildVersionList();

            if (row.Versions.Count == 0)
            {
                Status = $"{row.Name} has no published versions.";
            }
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Could not load versions: {e.Message}";
        }
        finally
        {
            row.IsLoadingVersions = false;
        }
    }

    /// <summary>Applies the release filter and picks a sensible default.</summary>
    private void RebuildVersionList()
    {
        var previous = SelectedVersion?.Version.FileId ?? string.Empty;

        Selection.Clear();

        if (SelectedPack is not { } pack)
        {
            SelectedVersion = null;

            return;
        }

        var shown = ReleasesOnly && pack.Versions.Any(v => v.IsRelease)
            ? pack.Versions.Where(v => v.IsRelease)
            : pack.Versions;

        /*
         * FALLS BACK TO SHOWING EVERYTHING when a pack has no stable release at all -- which is the
         * case for a pack still in development, and an empty list under a ticked box reads as "this
         * pack has no versions" rather than "they are all betas".
         */
        foreach (var version in shown)
        {
            Selection.Add(version);
        }

        // The same version stays picked across a filter change when it survives the filter, and the
        // newest is picked otherwise. Modrinth returns newest first.
        SelectedVersion = Selection.FirstOrDefault(v => v.Version.FileId == previous) ?? Selection.FirstOrDefault();
    }

    partial void OnSelectedPackChanged(PackResultViewModel? value)
    {
        OnPropertyChanged(nameof(CanInstall));

        _ = LoadVersionsAsync(value);
    }

    /// <summary>Records the choice for the caller.</summary>
    public void Accept()
    {
        Chosen = SelectedPack is { } pack && SelectedVersion is { } version
            ? (pack.Pack, version.Version)
            : null;
    }

    /// <summary>What the instance will be called.</summary>
    /// <remarks>
    /// Shown before installing rather than asked afterwards, because the pack's own name is usually
    /// right and the one time it is not -- a second copy of a pack already installed -- is the time
    /// somebody most wants to say so up front.
    /// </remarks>
    public string EffectiveInstanceName
        => InstanceName.Trim().Length != 0 ? InstanceName.Trim() : SelectedPack?.Name ?? string.Empty;
}
