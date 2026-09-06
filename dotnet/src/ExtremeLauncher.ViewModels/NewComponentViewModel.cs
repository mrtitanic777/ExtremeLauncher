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
 * Ported from ui/dialogs/NewComponentDialog: the uid and name for a hand-made component, with the
 * uid checked against the ones already in the instance so two cannot share it.
 */

using CommunityToolkit.Mvvm.ComponentModel;

namespace ExtremeLauncher.ViewModels;

public sealed partial class NewComponentViewModel : ObservableObject
{
    private readonly HashSet<string> _existingUids;

    public NewComponentViewModel(IReadOnlyList<string>? existingUids = null)
        => _existingUids = new HashSet<string>(existingUids ?? [], StringComparer.Ordinal);

    /// <summary>The reverse-DNS id, like "com.example.tweaks". Unique within the instance.</summary>
    [ObservableProperty]
    private string _uid = string.Empty;

    /// <summary>The display name shown on the version page.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    partial void OnUidChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(UidClashes));
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanCreate));

    /// <summary>Whether the entered uid is already taken -- shown so the refusal is not a mystery.</summary>
    public bool UidClashes => Uid.Trim().Length != 0 && _existingUids.Contains(Uid.Trim());

    /// <summary>A component needs both fields, and a uid nothing else already uses.</summary>
    public bool CanCreate => Uid.Trim().Length != 0 && Name.Trim().Length != 0 && !UidClashes;

    public NewComponentChoice ToChoice() => new(Uid.Trim(), Name.Trim());
}
