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
 * The app's half of the modpack browser: show the window, then run the install.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Tasks;
using ExtremeLauncher.ViewModels;

// Avalonia.Controls has a ResourceProvider of its own; this is the mod-platform one.
using ResourceProvider = ExtremeLauncher.ModPlatform.ResourceProvider;

namespace ExtremeLauncher.App;

public sealed class AppPackBrowser(
    Func<Window?> owner,
    HttpClient client,
    IUserPrompts prompts,
    LauncherLog? log,
    LauncherPaths? paths,
    string metaUrl) : IPackBrowser
{
    public async Task<string> BrowseAndInstallAsync(InstanceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (owner() is not { } parent)
        {
            return string.Empty;
        }

        var model = new PackBrowserViewModel(new PackSearch(new ResourceSearchSource(client)));
        var window = new PackBrowserWindow(model);

        await window.ShowDialog(parent).ConfigureAwait(true);

        if (window.Chosen is not { } chosen)
        {
            return string.Empty;
        }

        log?.Info($"Installing modpack {chosen.Pack.Name} {chosen.Version.Version} ({chosen.Pack.AddonId})");

        var install = new ModpackInstallTask(
            client,
            chosen.Pack,
            chosen.Version,
            window.InstanceName,
            paths,
            metaUrl);

        var staging = new InstanceStagingTask(list, install, install);

        /*
         * BEHIND A PROGRESS WINDOW. A modpack is a download of tens or hundreds of megabytes followed
         * by all of its mods, and without something on screen the launcher is indistinguishable from
         * one that has hung -- which people respond to by killing it, half way through writing files.
         */
        if (!await ProgressWindow.RunAsync(parent, staging, $"Installing {chosen.Pack.Name}").ConfigureAwait(true))
        {
            /*
             * A CANCELLATION IS NOT AN ERROR. Somebody who pressed Cancel knows what happened and
             * does not need a dialog telling them it failed -- and the staging folder is already gone.
             */
            if (staging.State == TaskState.AbortedByUser)
            {
                log?.Info("Modpack install cancelled.");

                return string.Empty;
            }

            log?.Error($"Modpack install failed: {staging.FailReason}");

            await prompts.ConfirmAsync(
                "Could not install the modpack",
                staging.FailReason.Length != 0 ? staging.FailReason : "The modpack could not be installed.",
                "OK").ConfigureAwait(true);

            return string.Empty;
        }

        log?.Info(
            $"Installed '{install.PackName}' as '{staging.CommittedId}' ({install.DownloadedCount} files)");

        return staging.CommittedId;
    }

    /// <summary>The search adapter, with none of the mod browser's filters.</summary>
    /// <remarks>
    /// A MODPACK IS NOT FILTERED BY THE INSTANCE, because there is no instance yet -- the pack is
    /// what creates one. The mod browser's adapter narrows every search by the instance's Minecraft
    /// version and loader, which here would be filtering by an answer nobody has given.
    /// </remarks>
    private sealed class PackSearch(ResourceSearchSource source) : IResourceSearch
    {
        public bool IsAvailable(ResourceProvider provider) => ResourceSearchSource.IsAvailable(provider);

        public string UnavailableReason(ResourceProvider provider)
            => ResourceSearchSource.UnavailableReason(provider);

        public Task<IReadOnlyList<IndexedPack>> SearchAsync(
            ResourceProvider provider,
            string query,
            CancellationToken cancellationToken)
            => source.SearchAsync(
                provider,
                new SearchArgs { Type = ResourceType.Modpack, Search = query },
                cancellationToken);

        public Task LoadVersionsAsync(IndexedPack pack, CancellationToken cancellationToken)
            => source.LoadVersionsAsync(pack, new VersionSearchArgs { Pack = pack }, cancellationToken);
    }
}
