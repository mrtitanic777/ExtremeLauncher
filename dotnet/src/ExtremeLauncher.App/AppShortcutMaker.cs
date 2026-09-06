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
 * Working out what a shortcut for an instance should say, and writing it.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppShortcutMaker(LauncherPaths paths, IconList icons, LauncherLog? log = null) : IShortcutMaker
{
    public async Task<ShortcutResult> CreateAsync(string instanceId, string instanceName, string iconKey)
    {
        var destination = FileSystem.PathCombine(
            Shortcuts.DesktopDirectory,
            Shortcuts.FileNameFor(instanceName));

        /*
         * THE ARGUMENTS ARE THE ONES THIS LAUNCHER ALREADY UNDERSTANDS. --dir matters as much as
         * --launch: a portable install keeps its data beside the executable, and a shortcut that
         * omits it would start a launcher pointing at an empty data folder and find no instance of
         * that name at all.
         */
        var arguments = new[] { "--dir", paths.Root, "--launch", instanceId };

        var iconPath = icons.Resolve(iconKey) is { Source: IconSource.User } entry ? entry.FilePath : string.Empty;

        var error = await Task.Run(() => Shortcuts.Create(
            destination,
            Environment.ProcessPath ?? string.Empty,
            arguments,
            instanceName,
            iconPath)).ConfigureAwait(true);

        if (error.Length != 0)
        {
            log?.Warning($"Could not create a shortcut for {instanceName}: {error}");

            return new ShortcutResult(false, $"Could not create the shortcut: {error}");
        }

        log?.Info($"Created a shortcut for {instanceName} at {destination}");

        return new ShortcutResult(true, $"Created a shortcut at {destination}");
    }
}
