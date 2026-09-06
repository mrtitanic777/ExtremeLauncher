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
 * The window-shaped half of choosing a version, kept out of the view models so those stay testable.
 */

using Avalonia.Controls;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppVersionChooser(Func<Window?> owner, IVersionListSource versions) : IVersionChooser
{
    public async Task<string> ChooseAsync(string uid, string title, string minecraftVersion)
    {
        var window = new VersionSelectWindow(
            new VersionSelectViewModel(uid, title, versions, minecraftVersion));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            // No owner is a headless or test build; showing it modelessly at least does not throw.
            window.Show();
        }

        return window.ChosenVersion;
    }
}
