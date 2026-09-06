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
 * The window-shaped half of About.
 */

using Avalonia.Controls;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppAbout(Func<Window?> owner, string dataDirectory) : IAboutUi
{
    public async Task OpenAsync()
    {
        // The data directory comes from the running launcher rather than being re-derived: it can be
        // overridden with --dir, and the value support needs is the one actually in use.
        var window = new AboutWindow(new AboutViewModel(dataDirectory: dataDirectory));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }
}
