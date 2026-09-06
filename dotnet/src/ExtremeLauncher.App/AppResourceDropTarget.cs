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
 * Running an import for files dropped on an instance window, and saying what became of them.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Minecraft.Mods;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppResourceDropTarget(InstancePaths paths, LauncherLog? log = null) : IResourceDropTarget
{
    public async Task<string> DropAsync(IReadOnlyList<string> files)
    {
        // Off the UI thread: importing copies files, and a dropped world save can be large.
        var results = await Task.Run(() => ResourceImport.Import(paths, files)).ConfigureAwait(true);

        foreach (var result in results)
        {
            if (result.Succeeded)
            {
                log?.Info($"Imported {result.FileName} as a {LocalResourceParse.TypeName(result.Type)}");
            }
            else
            {
                log?.Warning($"Could not import {result.FileName}: {result.Error}");
            }
        }

        return Describe(results);
    }

    /// <summary>
    /// One sentence about what happened.
    /// </summary>
    /// <remarks>
    /// A single file gets NAMED along with what it turned out to be, because that is the interesting
    /// part: somebody who dropped a zip they were unsure about has just been told it was a shader
    /// pack. Several files get a count, since five names do not fit on a status line.
    /// </remarks>
    private static string Describe(IReadOnlyList<ImportedResource> results)
    {
        var installed = results.Where(r => r.Succeeded).ToArray();
        var failed = results.Where(r => !r.Succeeded).ToArray();

        if (installed.Length == 0 && failed.Length == 1)
        {
            return $"Could not import {failed[0].FileName}: {failed[0].Error}.";
        }

        if (installed.Length == 1 && failed.Length == 0)
        {
            var one = installed[0];

            return $"Imported {one.FileName} as a {LocalResourceParse.TypeName(one.Type)}.";
        }

        var text = installed.Length == 1
            ? "Imported 1 file."
            : $"Imported {installed.Length} files.";

        return failed.Length == 0
            ? text
            : $"{text} {failed.Length} could not be imported -- see the launcher log.";
    }
}
