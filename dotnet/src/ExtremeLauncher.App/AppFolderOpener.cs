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
 * Ported in behaviour from upstream's ViewInstanceFolder / ViewLauncherRootFolder family, which is
 * fourteen separate actions over one mechanism.
 *
 * "OPEN THE FOLDER" IS THE ESCAPE HATCH every launcher needs. However complete the UI gets, somebody
 * eventually has to look at a file -- a crash report to attach, a config the launcher does not edit, a
 * jar to drop in by hand -- and a launcher that cannot show them where its files are makes that a
 * search rather than a click.
 *
 * THE FOLDER IS CREATED IF IT IS NOT THERE. Several of these are made lazily -- the icons folder does
 * not exist until an icon is imported -- and opening a file manager on a path that does not exist
 * either fails silently or shows an error box, both of which read as the launcher being broken.
 */

using System.Diagnostics;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppFolderOpener(LauncherPaths paths, LauncherLog? log = null) : IFolderOpener
{
    /// <summary>The launcher's own folders, by the name the UI asks for.</summary>
    private string? PathFor(string kind) => kind switch
    {
        "root" => paths.Root,
        "instances" => paths.Instances,
        "logs" => FileSystem.PathCombine(paths.Root, "logs"),
        "java" => paths.Java,
        "icons" => FileSystem.PathCombine(paths.Root, "icons"),
        "meta" => paths.Meta,
        "assets" => paths.Assets,
        "libraries" => paths.Libraries,
        _ => null,
    };

    public Task OpenKnownAsync(string kind)
    {
        if (PathFor(kind) is { } path)
        {
            return OpenAsync(path);
        }

        log?.Warning($"No folder is known by the name “{kind}”.");

        return Task.CompletedTask;
    }

    public Task OpenAsync(string path)
    {
        if (path.Length == 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            // Made on demand: several of these do not exist until the feature that writes them runs.
            FileSystem.EnsureFolderPathExists(path);

            /*
             * UseShellExecute hands the path to the desktop's own handler, which is what makes one
             * line work on all three platforms -- Explorer, Finder and whatever the user's Linux
             * session uses -- rather than naming a file manager this port would have to guess at.
             */
            using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(path))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception
            or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Logged rather than thrown: a desktop with no file manager association is a limitation,
            // not a reason to take the window down.
            log?.Warning($"Could not open {path}: {e.Message}");
        }

        return Task.CompletedTask;
    }
}
