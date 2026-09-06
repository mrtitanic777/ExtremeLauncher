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
 * Picks a modpack file and imports it. The app owns this because a file picker is a window's job; the
 * import itself is ModrinthImportTask, which is tested and has been run against a real pack.
 */

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Tasks;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppPackImporter : IPackImporter
{
    private readonly Func<Window?> _owner;

    private readonly HttpClient _client;

    private readonly IUserPrompts _prompts;

    private readonly LauncherLog? _log;

    private readonly LauncherPaths? _paths;

    private readonly string _metaUrl;

    /// <param name="paths">
    /// With a meta url, lets the imported instance resolve the components the pack does not list.
    /// </param>
    public AppPackImporter(
        Func<Window?> owner,
        HttpClient client,
        IUserPrompts prompts,
        LauncherLog? log = null,
        LauncherPaths? paths = null,
        string metaUrl = "")
    {
        _owner = owner;
        _client = client;
        _prompts = prompts;
        _log = log;
        _paths = paths;
        _metaUrl = metaUrl;
    }

    public async Task<string> ImportAsync(InstanceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (_owner() is not { } owner)
        {
            return string.Empty;
        }

        var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a modpack",
            AllowMultiple = false,
            FileTypeFilter =
            [
                /*
                 * .mrpack and .zip both, because a Modrinth pack downloaded through a browser is
                 * routinely saved as .zip -- and refusing to show it would look like the launcher
                 * cannot read the file the user is holding. The format is decided by looking inside.
                 */
                new FilePickerFileType("Modpacks") { Patterns = ["*.mrpack", "*.zip"] },
            ],
        }).ConfigureAwait(true);

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path)
        {
            return string.Empty;
        }

        _log?.Info($"Importing pack from {path}");

        // The paths and meta url let the imported instance resolve what the pack does not list.
        var import = new ModrinthImportTask(
            path,
            _client,
            paths: _paths,
            metaUrl: _metaUrl);
        var staging = new InstanceStagingTask(list, import, import);

        // Behind a progress window: importing downloads the pack's mods, which is tens of megabytes
        // and used to happen with nothing at all on screen.
        if (!await ProgressWindow.RunAsync(owner, staging, "Importing the modpack").ConfigureAwait(true))
        {
            // A cancellation is not an error; see AppPackBrowser.
            if (staging.State == TaskState.AbortedByUser)
            {
                _log?.Info("Pack import cancelled.");

                return string.Empty;
            }

            _log?.Error($"Import failed: {staging.FailReason}");

            /*
             * SHOWN, not swallowed. Import fails for reasons a person can act on -- the wrong kind of
             * pack, a pack with no Minecraft version, a download that would not verify -- and every one
             * of them is a sentence worth reading.
             */
            await _prompts.ConfirmAsync(
                "Could not import the pack",
                staging.FailReason.Length != 0 ? staging.FailReason : "The pack could not be imported.",
                "OK").ConfigureAwait(true);

            return string.Empty;
        }

        _log?.Info($"Imported '{import.PackName}' as '{staging.CommittedId}' ({import.DownloadedCount} files)");

        return staging.CommittedId;
    }
}
