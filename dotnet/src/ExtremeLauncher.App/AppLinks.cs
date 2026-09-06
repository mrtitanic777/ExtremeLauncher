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
 * Opening a web link, and clearing the metadata cache.
 *
 * TWO SMALL THINGS IN ONE FILE because they are the same kind of thing: upstream toolbar actions with
 * no state, no dialog of their own, and nothing to test beyond "does it do the one thing".
 */

using System.Diagnostics;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppLinks(LauncherLog? log = null) : ILinkOpener
{
    public Task OpenAsync(string url)
    {
        /*
         * THE SCHEME IS CHECKED even though these URLs come from BuildConfig -- a compile-time
         * constant of this build rather than anything from the network or from a user. A fork can put
         * whatever it likes in that config, and "it came from our own settings" is a weaker guarantee
         * than "it is http or https". UseShellExecute hands whatever it is given to the desktop.
         */
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            log?.Warning($"Refusing to open “{url}”: only http and https links are opened.");

            return Task.CompletedTask;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A desktop with no browser association is a limitation, not a reason to take the window
            // down -- and the user can still read the address from the About window.
            log?.Warning($"Could not open a browser for {parsed.AbsoluteUri}: {e.Message}");
        }

        return Task.CompletedTask;
    }
}

public sealed class AppMetadataCache(LauncherPaths paths, LauncherLog? log = null) : IMetadataCache
{
    public async Task<string> ClearAsync()
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(paths.Meta))
            {
                return "There was no metadata cache to clear.";
            }

            long bytes;
            int files;

            try
            {
                var all = Directory.GetFiles(paths.Meta, "*", SearchOption.AllDirectories);

                files = all.Length;
                bytes = all.Sum(f => new FileInfo(f).Length);

                /*
                 * THE FOLDER IS REMOVED AND REMADE rather than emptied file by file: the cache has a
                 * directory per package and leaving the shells behind would have the next fetch write
                 * into a tree that looks populated. It is recreated so nothing downstream has to
                 * handle a missing meta folder.
                 */
                Directory.Delete(paths.Meta, recursive: true);
                FileSystem.EnsureFolderPathExists(paths.Meta);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Warning($"Could not clear the metadata cache: {e.Message}");

                return $"Could not clear the metadata cache: {e.Message}";
            }

            log?.Info($"Cleared the metadata cache: {files} files, {bytes:N0} bytes.");

            return files == 0
                ? "The metadata cache was already empty."
                : $"Cleared {files} cached metadata files ({bytes / 1024.0:0.#} KB). "
                  + "It will be downloaded again when next needed.";
        }).ConfigureAwait(true);
    }
}
