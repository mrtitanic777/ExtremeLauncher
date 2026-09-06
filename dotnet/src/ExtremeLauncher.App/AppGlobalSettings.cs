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
 * The window-shaped half of the global settings, kept out of the view models so those stay testable.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppGlobalSettings(
    Func<Window?> owner,
    SettingsObject settings,
    LauncherLog? log = null,
    IJavaInstallUi? java = null) : IGlobalSettingsUi
{
    public async Task OpenAsync()
    {
        /*
         * A NEW VIEW MODEL EACH TIME, over the SAME settings object. The object is the one the running
         * launcher reads, so this window edits the live configuration -- which is what makes Cancel
         * reverting rather than merely closing (see GlobalSettingsWindow).
         */
        var window = new GlobalSettingsWindow(new GlobalSettingsViewModel(settings, java));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }

        if (window.Saved)
        {
            log?.Info("Global settings saved.");
        }
    }
}
