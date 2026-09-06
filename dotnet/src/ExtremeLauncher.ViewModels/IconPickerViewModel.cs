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
 * Ported in behaviour from launcher/ui/dialogs/IconPickerDialog.cpp.
 *
 * CHOOSING AN INSTANCE'S PICTURE. Every instance has carried an `iconKey` in its instance.cfg since
 * the first wave -- the vanilla creator writes one, the Modrinth importer writes one -- and nothing
 * has ever read one back or changed it, so every instance in this launcher looks identical.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

/// <summary>Picking a file to import as an icon. Implemented by the app.</summary>
public interface IIconFilePicker
{
    /// <returns>The chosen path, or empty when cancelled.</returns>
    Task<string> PickAsync();
}

/// <summary>One icon in the grid.</summary>
public sealed partial class IconViewModel(IconEntry entry) : ObservableObject
{
    public IconEntry Entry { get; } = entry;

    public string Key => Entry.Key;

    public string DisplayName => Entry.DisplayName;

    public bool IsUserIcon => Entry.Source == IconSource.User;

    public bool IsRenderable => Entry.IsRenderable;

    /// <summary>Where to load it from: a file path for a user icon, empty for a built-in.</summary>
    public string FilePath => Entry.FilePath;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class IconPickerViewModel : ObservableObject
{
    private readonly IconList _icons;

    private readonly IIconFilePicker? _picker;

    public IconPickerViewModel(IconList icons, string currentKey = "", IIconFilePicker? picker = null)
    {
        ArgumentNullException.ThrowIfNull(icons);

        _icons = icons;
        _picker = picker;

        CurrentKey = currentKey;

        Rebuild(currentKey);
    }

    /// <summary>The key the instance had when the dialog opened.</summary>
    public string CurrentKey { get; }

    public ObservableCollection<IconViewModel> Icons { get; } = [];

    [ObservableProperty]
    private IconViewModel? _selected;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The key the user settled on, or empty when they cancelled.</summary>
    public string ChosenKey { get; private set; } = string.Empty;

    public bool CanImport => _picker is not null;

    /// <summary>
    /// Whether the selected icon can be removed.
    /// </summary>
    /// <remarks>
    /// A built-in has no file to remove and its key would come straight back on the next listing,
    /// which reads as the delete having silently failed.
    /// </remarks>
    public bool CanRemove => Selected is { IsUserIcon: true };

    public bool CanAccept => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    public async Task ImportAsync()
    {
        if (_picker is null)
        {
            return;
        }

        var path = await _picker.PickAsync().ConfigureAwait(true);

        if (path.Length == 0)
        {
            return;
        }

        var key = _icons.Import(path);

        if (key.Length == 0)
        {
            // Named rather than silent: the usual cause is a file that is not an image, and the user
            // picked it on purpose so they deserve to be told why it did not take.
            Status = "That file could not be added as an icon.";

            return;
        }

        // Rebuilt and then selected, so the newly added icon is the one highlighted -- importing an
        // icon and then having to hunt for it in the grid is a small, avoidable annoyance.
        Rebuild(key);

        Status = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    public void Remove()
    {
        if (Selected is not { IsUserIcon: true } selected)
        {
            return;
        }

        if (!_icons.Remove(selected.Key))
        {
            Status = $"Could not remove “{selected.DisplayName}”.";

            return;
        }

        /*
         * FALLS BACK TO THE INSTANCE'S CURRENT ICON, not to nothing. Removing an icon you were not
         * using should leave the dialog exactly as it was; removing the one you WERE using leaves the
         * default selected rather than an empty grid position.
         */
        Rebuild(CurrentKey);

        Status = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanAccept))]
    public void Accept()
    {
        if (Selected is { } selected)
        {
            ChosenKey = selected.Key;
        }
    }

    private void Rebuild(string keyToSelect)
    {
        Icons.Clear();

        foreach (var entry in _icons.All())
        {
            Icons.Add(new IconViewModel(entry));
        }

        Selected = Icons.FirstOrDefault(i => string.Equals(i.Key, keyToSelect, StringComparison.Ordinal))
            ?? Icons.FirstOrDefault(i => string.Equals(i.Key, IconList.DefaultKey, StringComparison.Ordinal))
            ?? Icons.FirstOrDefault();
    }

    partial void OnSelectedChanged(IconViewModel? oldValue, IconViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        // Announced, not merely computed: these are bound to IsEnabled. See AccountsViewModel.
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanAccept));
        RemoveCommand.NotifyCanExecuteChanged();
        AcceptCommand.NotifyCanExecuteChanged();
    }
}
