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
 * The Technic pack browser. UNLIKE the FTB and ATLauncher browsers, Technic has a real search endpoint,
 * so this is search-driven like the Modrinth one: an empty query loads the trending list, and a query
 * runs a search. Picking a pack and pressing install hands it back through Chosen; the pack's version
 * and whether it is a Solder or single-zip install are resolved from its detail at install time, above
 * this view model.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.ViewModels;

/// <summary>Searches the Technic platform for the browser.</summary>
public interface ITechnicPackSource
{
    Task<IReadOnlyList<TechnicModpack>> SearchAsync(string term, CancellationToken cancellationToken);
}

public sealed partial class TechnicBrowserViewModel : ObservableObject
{
    private readonly ITechnicPackSource? _source;

    public TechnicBrowserViewModel(ITechnicPackSource? source = null) => _source = source;

    /// <summary>The current results — trending, or the last search.</summary>
    public ObservableCollection<TechnicModpack> Packs { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private TechnicModpack? _selectedPack;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Install is allowed once a pack is selected.</summary>
    public bool CanInstall => SelectedPack is not null;

    /// <summary>The pack the user accepted, for the app to resolve and install.</summary>
    public TechnicModpack? Chosen { get; private set; }

    /// <summary>The instance name to use: what was typed, or the pack's own name.</summary>
    public string EffectiveInstanceName
        => InstanceName.Trim().Length != 0 ? InstanceName.Trim() : SelectedPack?.Name ?? string.Empty;

    /// <summary>Loads the trending list. Safe to call on window open.</summary>
    public Task LoadAsync() => SearchAsync();

    /// <summary>Runs the current search (or trending when the box is empty).</summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        if (_source is null || IsLoading)
        {
            return;
        }

        IsLoading = true;
        Status = "Searching Technic…";

        try
        {
            var results = await _source.SearchAsync(SearchText.Trim(), CancellationToken.None).ConfigureAwait(true);

            Packs.Clear();

            foreach (var pack in results)
            {
                Packs.Add(pack);
            }

            Status = Packs.Count == 0 ? "No packs found." : string.Empty;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Status = $"Could not reach Technic: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Records the current selection for the caller. No-op unless a pack is selected.</summary>
    public void Accept() => Chosen = CanInstall ? SelectedPack : null;

    partial void OnSelectedPackChanged(TechnicModpack? value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(EffectiveInstanceName));
    }

    partial void OnInstanceNameChanged(string value) => OnPropertyChanged(nameof(EffectiveInstanceName));
}
