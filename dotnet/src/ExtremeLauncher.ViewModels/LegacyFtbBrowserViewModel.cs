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
 * The classic (legacy) FTB pack browser, the counterpart to the Modrinth PackBrowserViewModel.
 *
 * UNLIKE MODRINTH there is no search endpoint: the whole catalogue arrives in two XML lists, so this
 * fetches once and filters the rows in memory. Each pack already carries its versions (a "broken" pack
 * carries none and cannot be installed). Picking a pack and a version and pressing install hands the
 * choice back through Chosen, exactly as the Modrinth browser does.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.ViewModels;

/// <summary>Fetches the legacy FTB catalogue. Implemented by the app over the CDN; stubbed in tests.</summary>
public interface ILegacyFtbSource
{
    Task<LegacyFtbFetchResult> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>One row in the FTB catalogue list.</summary>
public sealed class LegacyFtbPackRow(LegacyFtbModpack pack)
{
    public LegacyFtbModpack Pack { get; } = pack;

    public string Name => Pack.Name;

    public string Author => Pack.Author;

    public string Description => Pack.Description;

    /// <summary>Which list it came from, shown as a small tag.</summary>
    public string SourceLabel => Pack.Type == LegacyFtbPackType.ThirdParty ? "third-party" : "public";

    /// <summary>A pack with no usable version cannot be installed; the row says so.</summary>
    public bool IsBroken => Pack.Broken;
}

public sealed partial class LegacyFtbBrowserViewModel : ObservableObject
{
    private readonly ILegacyFtbSource? _source;

    private readonly List<LegacyFtbPackRow> _all = [];

    public LegacyFtbBrowserViewModel(ILegacyFtbSource? source = null) => _source = source;

    /// <summary>The catalogue, filtered by <see cref="SearchText"/>.</summary>
    public ObservableCollection<LegacyFtbPackRow> Packs { get; } = [];

    /// <summary>The selected pack's versions, newest first.</summary>
    public ObservableCollection<string> Versions { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LegacyFtbPackRow? _selectedPack;

    [ObservableProperty]
    private string? _selectedVersion;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Install is allowed once a non-broken pack and one of its versions are chosen.</summary>
    public bool CanInstall => SelectedPack is { IsBroken: false } && SelectedVersion is { Length: > 0 };

    /// <summary>The pack and version the user accepted, for the app to install.</summary>
    public (LegacyFtbModpack Pack, string Version)? Chosen { get; private set; }

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
        Status = "Loading the FTB pack list…";

        try
        {
            var result = await _source.FetchAsync(CancellationToken.None).ConfigureAwait(true);

            _all.Clear();
            _all.AddRange(result.Public.Select(p => new LegacyFtbPackRow(p)));
            _all.AddRange(result.ThirdParty.Select(p => new LegacyFtbPackRow(p)));

            ApplyFilter();

            Status = _all.Count == 0
                ? "The FTB pack list could not be read."
                : string.Empty;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Status = $"Could not reach the FTB server: {e.Message}";
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

    partial void OnSelectedPackChanged(LegacyFtbPackRow? value)
    {
        Versions.Clear();

        if (value is not null)
        {
            // Newest first: the XML lists oldVersions oldest-first.
            foreach (var version in Enumerable.Reverse(value.Pack.OldVersions))
            {
                Versions.Add(version);
            }

            SelectedVersion = value.Pack.CurrentVersion.Length != 0 && Versions.Contains(value.Pack.CurrentVersion)
                ? value.Pack.CurrentVersion
                : Versions.FirstOrDefault();
        }
        else
        {
            SelectedVersion = null;
        }

        InstanceName = string.Empty;
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(EffectiveInstanceName));
    }

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
