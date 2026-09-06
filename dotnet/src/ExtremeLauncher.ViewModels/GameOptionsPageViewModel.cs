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
 * Ported in behaviour from launcher/ui/pages/instance/GameOptionsPage.cpp.
 *
 * THE GAME'S OWN SETTINGS, options.txt. The parser and writer have been ported and tested since an
 * early wave and nothing has ever shown one. It is a modest page and it earns its place for one
 * reason: an instance whose graphics settings crash the machine on startup can be fixed here without
 * launching it, which is otherwise a chicken-and-egg problem.
 *
 * A FLAT KEY-VALUE TABLE, which is upstream's own treatment. options.txt holds two hundred keys of
 * wildly different kinds -- booleans, enums, floats, JSON blobs for key bindings -- and pretending to
 * understand them is how a launcher corrupts somebody's settings. Showing them as they are, and
 * writing back exactly what was typed, is both honest and safe.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.ViewModels;

/// <summary>One line of options.txt.</summary>
public sealed partial class GameOptionViewModel(string key, string value) : ObservableObject
{
    private string _saved = value;

    public string Key { get; } = key;

    [ObservableProperty]
    private string _value = value;

    public bool IsDirty => !string.Equals(Value, _saved, StringComparison.Ordinal);

    public void Saved() => _saved = Value;

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(IsDirty));
}

public sealed partial class GameOptionsPageViewModel : ObservableObject, IInstancePage
{
    private GameOptions? _options;

    public string Title => "Game options";

    public ObservableCollection<GameOptionViewModel> Options { get; } = [];

    /// <summary>Everything, before the search box narrows it.</summary>
    private readonly List<GameOptionViewModel> _all = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The options.txt format version the game wrote, for the page to show.</summary>
    public int FormatVersion => _options?.Version ?? 0;

    public bool HasOptions => _all.Count != 0;

    /// <summary>
    /// Whether there is anything to show.
    /// </summary>
    /// <remarks>
    /// An instance that has never been launched has no options.txt at all, which is the ordinary case
    /// for a new one -- so this is a state to explain rather than an error.
    /// </remarks>
    public bool IsMissing => _options is null || !_options.IsLoaded;

    public bool HasUnsavedChanges => _all.Any(o => o.IsDirty);

    /// <summary>Reads an instance's options.txt.</summary>
    public void Load(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);

        _options = GameOptions.Load(FileSystem.PathCombine(gameRoot, "options.txt"));

        _all.Clear();

        foreach (var item in _options.Contents)
        {
            var option = new GameOptionViewModel(item.Key, item.Value);

            option.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HasUnsavedChanges));

            _all.Add(option);
        }

        Rebuild();

        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(FormatVersion));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    public bool Save()
    {
        if (_options is null)
        {
            return !HasUnsavedChanges;
        }

        /*
         * ONLY WHAT CHANGED is written back, through GameOptions.Set. It preserves the order and the
         * keys it does not know about -- and options.txt is full of keys this launcher has never heard
         * of, because every mod that stores a setting puts it here.
         */
        foreach (var option in _all.Where(o => o.IsDirty))
        {
            _options.Set(option.Key, option.Value);
        }

        if (!_options.Save())
        {
            Status = "Could not write options.txt. The game may still be running.";

            return false;
        }

        foreach (var option in _all)
        {
            option.Saved();
        }

        Status = "Saved.";

        OnPropertyChanged(nameof(HasUnsavedChanges));

        return true;
    }

    [RelayCommand]
    public void Reload()
    {
        if (_options is null)
        {
            return;
        }

        Load(Path.GetDirectoryName(_options.Path) ?? string.Empty);

        Status = "Reloaded from disk.";
    }

    private void Rebuild()
    {
        Options.Clear();

        foreach (var option in _all)
        {
            if (SearchText.Length != 0
                && option.Key.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            Options.Add(option);
        }
    }

    /// <remarks>
    /// Filtering the VIEW, not the list: an option edited and then filtered away keeps its change,
    /// because the same object goes back into the collection when the filter is cleared.
    /// </remarks>
    partial void OnSearchTextChanged(string value) => Rebuild();
}
