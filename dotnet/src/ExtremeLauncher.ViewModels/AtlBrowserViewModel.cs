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
 * The ATLauncher pack browser, the counterpart to the legacy-FTB one and built the same way: there is
 * no search endpoint, the whole catalogue (packsnew.json) arrives at once, so this fetches it once and
 * filters the rows in memory. Each pack carries its versions; picking a pack and a version and pressing
 * install hands the choice back through Chosen for the app to run AtlInstallTask against.
 *
 * A pack the list marks "system" is ATLauncher's own machinery, not a user-facing modpack, so it is
 * dropped rather than shown — as the upstream browser does.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.ViewModels;

/// <summary>Fetches the ATLauncher pack catalogue.</summary>
public interface IAtlPackSource
{
    Task<IReadOnlyList<AtlIndexedPack>> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>One row of the ATLauncher browser.</summary>
public sealed class AtlPackRow(AtlIndexedPack pack)
{
    public AtlIndexedPack Pack { get; } = pack;

    public string Name => Pack.Name;

    public string Description => Pack.Description;

    public string SourceLabel => Pack.Type == AtlPackType.Private ? "private" : "public";

    /// <summary>A pack with no versions cannot be installed.</summary>
    public bool IsInstallable => Pack.Versions.Count != 0;
}

public sealed partial class AtlBrowserViewModel : ObservableObject
{
    private readonly IAtlPackSource? _source;

    private readonly List<AtlPackRow> _all = [];

    public AtlBrowserViewModel(IAtlPackSource? source = null) => _source = source;

    /// <summary>The catalogue, filtered by <see cref="SearchText"/>.</summary>
    public ObservableCollection<AtlPackRow> Packs { get; } = [];

    /// <summary>The selected pack's versions, newest first (the list gives them newest-first already).</summary>
    public ObservableCollection<string> Versions { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private AtlPackRow? _selectedPack;

    [ObservableProperty]
    private string? _selectedVersion;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Install is allowed once a pack with versions and one of its versions are chosen.</summary>
    public bool CanInstall => SelectedPack is { IsInstallable: true } && SelectedVersion is { Length: > 0 };

    /// <summary>The pack and version the user accepted, for the app to install.</summary>
    public (AtlIndexedPack Pack, string Version)? Chosen { get; private set; }

    /// <summary>The instance name to use: what was typed, or the pack's own name.</summary>
    public string EffectiveInstanceName
        => InstanceName.Trim().Length != 0 ? InstanceName.Trim() : SelectedPack?.Name ?? string.Empty;

    /// <summary>Fetches the catalogue once. Safe to call on window open.</summary>
    public async Task LoadAsync()
    {
        if (_source is null || IsLoading)
        {
            return;
        }

        IsLoading = true;
        Status = "Loading the ATLauncher pack list…";

        try
        {
            var packs = await _source.FetchAsync(CancellationToken.None).ConfigureAwait(true);

            _all.Clear();

            // A "system" pack is ATLauncher's own scaffolding, not something the user installs.
            _all.AddRange(packs.Where(p => !p.System).Select(p => new AtlPackRow(p)));

            ApplyFilter();

            Status = _all.Count == 0 ? "The ATLauncher pack list could not be read." : string.Empty;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Status = $"Could not reach the ATLauncher server: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Records the current selection for the caller. No-op unless it is installable.</summary>
    public void Accept()
        => Chosen = CanInstall ? (SelectedPack!.Pack, SelectedVersion!) : null;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedVersionChanged(string? value) => OnPropertyChanged(nameof(CanInstall));

    partial void OnSelectedPackChanged(AtlPackRow? value)
    {
        Versions.Clear();

        if (value is not null)
        {
            foreach (var version in value.Pack.Versions)
            {
                Versions.Add(version.Version);
            }

            SelectedVersion = Versions.FirstOrDefault();
        }
        else
        {
            SelectedVersion = null;
        }

        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(EffectiveInstanceName));
    }

    partial void OnInstanceNameChanged(string value) => OnPropertyChanged(nameof(EffectiveInstanceName));

    private void ApplyFilter()
    {
        var query = SearchText.Trim();

        Packs.Clear();

        foreach (var row in _all)
        {
            if (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Packs.Add(row);
            }
        }
    }
}
