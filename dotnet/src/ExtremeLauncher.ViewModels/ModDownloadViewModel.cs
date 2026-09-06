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
 * Ported in behaviour from launcher/ui/pages/modplatform/ResourceDownloadDialog.cpp, ModPage.cpp and
 * ResourceModel.cpp.
 *
 * ADDING MODS, which is the thing a modded-Minecraft launcher is FOR and the largest single feature
 * this port has been missing. ModPlatform has had Modrinth and CurseForge search, project metadata,
 * version listing and a download task -- all ported, all tested -- since wave 8, and nothing in the UI
 * could reach any of it. The only way to add a mod was to drop a jar into the folder by hand.
 *
 * A BASKET, NOT A PICKER. Upstream's dialog lets you search, add several mods, keep searching, and
 * install the lot on OK -- because nobody adds exactly one mod. That shape is why `Selection` is a
 * separate collection from `Results` rather than a highlighted row: a mod added under one search term
 * must survive searching for the next one.
 *
 * THE SEARCH IS SERIALISED, not cancelled-and-restarted. Typing produces a request per keystroke if
 * you let it, and out-of-order responses put the results of "sod" underneath the query "sodium". Each
 * search records its own sequence number and a stale one is dropped on arrival.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.ViewModels;

/// <summary>Searching a mod provider. Implemented in the launch layer, which owns the HTTP.</summary>
public interface IResourceSearch
{
    bool IsAvailable(ResourceProvider provider);

    /// <summary>Why a provider cannot be used, or empty when it can.</summary>
    string UnavailableReason(ResourceProvider provider);

    Task<IReadOnlyList<IndexedPack>> SearchAsync(
        ResourceProvider provider,
        string query,
        CancellationToken cancellationToken);

    /// <summary>Fills in the pack's version list, filtered to the instance.</summary>
    Task LoadVersionsAsync(IndexedPack pack, CancellationToken cancellationToken);
}

/// <summary>One search result.</summary>
public sealed partial class ResourceResultViewModel(IndexedPack pack) : ObservableObject
{
    public IndexedPack Pack { get; } = pack;

    public string Name => Pack.Name;

    public string Description => Pack.Description;

    public string Authors => Pack.Authors.Count == 0
        ? string.Empty
        : string.Join(", ", Pack.Authors.Select(a => a.Name));

    public string ProviderLabel => Pack.Provider == ResourceProvider.Modrinth ? "Modrinth" : "CurseForge";

    /// <summary>The versions of this mod that suit the instance, once looked up.</summary>
    public ObservableCollection<IndexedVersion> Versions { get; } = [];

    [ObservableProperty]
    private IndexedVersion? _selectedVersion;

    [ObservableProperty]
    private bool _isLoadingVersions;

    /// <summary>Whether this mod is in the basket to be installed.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Set once its versions have been fetched, so they are not fetched twice.</summary>
    public bool VersionsLoaded { get; set; }
}

public sealed partial class ModDownloadViewModel : ObservableObject
{
    private readonly IResourceSearch? _search;

    /// <summary>Guards against an older search's results arriving after a newer one's.</summary>
    private int _searchGeneration;

    public ModDownloadViewModel(IResourceSearch? search = null, string minecraftVersion = "", string loader = "")
    {
        _search = search;

        MinecraftVersion = minecraftVersion;
        Loader = loader;
    }

    /// <summary>What the instance runs, shown so it is obvious what the results are filtered to.</summary>
    public string MinecraftVersion { get; }

    /// <summary>The instance's loader, for the same reason.</summary>
    public string Loader { get; }

    public string FilterSummary => MinecraftVersion.Length == 0
        ? "Showing everything"
        : Loader.Length == 0
            ? $"Showing mods for Minecraft {MinecraftVersion}"
            : $"Showing {Loader} mods for Minecraft {MinecraftVersion}";

    public ObservableCollection<ResourceResultViewModel> Results { get; } = [];

    /// <summary>The mods added so far. Survives searching for something else.</summary>
    public ObservableCollection<ResourceResultViewModel> Selection { get; } = [];

    [ObservableProperty]
    private ResourceProvider _provider = ResourceProvider.Modrinth;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ResourceResultViewModel? _highlighted;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _status = string.Empty;

    public bool CanSearch => _search is not null && !IsSearching;

    /// <summary>Whether there is anything to install.</summary>
    public bool CanInstall => Selection.Count > 0;

    public string SelectionSummary => Selection.Count switch
    {
        0 => "Nothing selected",
        1 => "1 mod selected",
        var n => $"{n} mods selected",
    };

    /// <summary>What the caller should download, once the dialog is accepted.</summary>
    public IReadOnlyList<(IndexedPack Pack, IndexedVersion Version)> Chosen { get; private set; } = [];

    [RelayCommand(CanExecute = nameof(CanSearch))]
    public async Task SearchAsync()
    {
        if (_search is null)
        {
            return;
        }

        if (!_search.IsAvailable(Provider))
        {
            // Said up front rather than after a 403, which would read as "the search is broken".
            Status = _search.UnavailableReason(Provider);
            Results.Clear();

            return;
        }

        var generation = ++_searchGeneration;

        IsSearching = true;
        Status = "Searching…";

        try
        {
            var found = await _search.SearchAsync(Provider, SearchText, CancellationToken.None)
                .ConfigureAwait(true);

            /*
             * DROPPED IF STALE. Without this, typing "sodium" one letter at a time can leave the
             * results for "sod" on screen under the query "sodium", because the shorter query's
             * response is bigger and can land later.
             */
            if (generation != _searchGeneration)
            {
                return;
            }

            Results.Clear();

            foreach (var pack in found)
            {
                var row = new ResourceResultViewModel(pack);

                /*
                 * A mod already in the basket comes back ticked. The two collections hold DIFFERENT
                 * objects for the same mod -- a new search builds new rows -- so this is matched on
                 * the addon id rather than by reference.
                 */
                row.IsSelected = Selection.Any(s => IsSameMod(s.Pack, pack));

                Results.Add(row);
            }

            Status = Results.Count == 0 ? "No mods matched." : string.Empty;
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

    /// <summary>Fetches the highlighted mod's versions, once.</summary>
    public async Task LoadVersionsAsync(ResourceResultViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_search is null || row.VersionsLoaded || row.IsLoadingVersions)
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
                row.Versions.Add(version);
            }

            // The newest suitable build, which is what the list is already ordered by.
            row.SelectedVersion = row.Versions.FirstOrDefault();

            row.VersionsLoaded = true;

            if (row.Versions.Count == 0)
            {
                /*
                 * A real and common case worth naming: the mod exists but has no build for this
                 * instance's Minecraft version or loader. "No versions" alone reads as a broken
                 * listing rather than an incompatible mod.
                 */
                Status = $"{row.Name} has no build for {FilterSummary.ToLowerInvariant()}.";
            }
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Could not load versions for {row.Name}: {e.Message}";
        }
        finally
        {
            row.IsLoadingVersions = false;
        }
    }

    /// <summary>Adds or removes the highlighted mod from the basket.</summary>
    [RelayCommand]
    public async Task ToggleSelectedAsync()
    {
        if (Highlighted is not { } row)
        {
            return;
        }

        if (Selection.FirstOrDefault(s => IsSameMod(s.Pack, row.Pack)) is { } already)
        {
            Selection.Remove(already);

            row.IsSelected = false;
            already.IsSelected = false;
        }
        else
        {
            // Versions are needed before it can go in the basket: the basket holds a FILE to
            // download, not just a project.
            await LoadVersionsAsync(row).ConfigureAwait(true);

            if (row.SelectedVersion is null)
            {
                return;
            }

            row.IsSelected = true;

            Selection.Add(row);
        }

        RaiseSelectionDependent();
    }

    /// <summary>Turns the basket into the list of files to fetch.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    public void Accept()
        => Chosen = Selection
            .Where(s => s.SelectedVersion is not null)
            .Select(s => (s.Pack, s.SelectedVersion!))
            .ToArray();

    private static bool IsSameMod(IndexedPack a, IndexedPack b)
        => a.Provider == b.Provider && string.Equals(a.AddonId, b.AddonId, StringComparison.Ordinal);

    private void RaiseSelectionDependent()
    {
        // Announced, not just computed: these are bound to IsEnabled and to a label. See the note in
        // AccountsViewModel -- a value-based test cannot see a missing notification.
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(SelectionSummary));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSearch));
        SearchCommand.NotifyCanExecuteChanged();
    }

    partial void OnProviderChanged(ResourceProvider value)
    {
        // The results on screen came from the other provider, so they are no longer an answer to
        // anything. Cleared rather than left to look like results for the new one.
        Results.Clear();

        Status = _search is not null && !_search.IsAvailable(value)
            ? _search.UnavailableReason(value)
            : string.Empty;
    }
}
