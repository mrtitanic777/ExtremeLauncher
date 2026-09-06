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
 * The desktop half of the copy dialog: shows CopyInstanceWindow and hands back what was chosen.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppInstanceCopyPrompt(Func<Window?> owner, string instancesPath) : IInstanceCopyPrompt
{
    public async Task<InstanceCopyChoice?> AskAsync(string sourceName)
    {
        // Offer Clone only where the instances volume actually supports copy-on-write; the copy task
        // is documented to expect that gate here rather than falling back to a slow copy in silence.
        var cloneAvailable = FileSystem.CanClone(instancesPath, instancesPath);

        var window = new CopyInstanceWindow(new CopyInstanceViewModel(sourceName, cloneAvailable));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }

        return window.Result;
    }
}
