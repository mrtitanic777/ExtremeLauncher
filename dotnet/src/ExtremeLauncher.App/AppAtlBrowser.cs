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
 * The app side of the ATLauncher browser: shows the window, then stages and installs the chosen pack
 * behind a progress window, mirroring the classic FTB browser. The URL safe name is the pack name with
 * non-alphanumerics stripped (case preserved) — AtlPackIndex.InstallSafeName, not the logo's SafeName.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Tasks;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppAtlBrowser(
    Func<Window?> owner,
    HttpClient client,
    IUserPrompts prompts,
    LauncherLog? log,
    LauncherPaths? paths) : IAtlBrowser
{
    public async Task<string> BrowseAndInstallAsync(InstanceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (owner() is not { } parent)
        {
            return string.Empty;
        }

        var model = new AtlBrowserViewModel(new Source(client));
        var window = new AtlBrowserWindow(model);

        await window.ShowDialog(parent).ConfigureAwait(true);

        if (window.Chosen is not { } chosen)
        {
            return string.Empty;
        }

        log?.Info($"Installing ATLauncher pack {chosen.Pack.Name} {chosen.Version}");

        var install = new AtlInstallTask(
            chosen.Pack.Name,
            AtlPackIndex.InstallSafeName(chosen.Pack.Name),
            chosen.Version,
            client,
            LauncherService.CurrentRuntimeContext(),
            instanceName: window.InstanceName);

        var staging = new InstanceStagingTask(list, install, install);

        if (!await ProgressWindow.RunAsync(parent, staging, $"Installing {chosen.Pack.Name}").ConfigureAwait(true))
        {
            if (staging.State == TaskState.AbortedByUser)
            {
                log?.Info("ATLauncher pack install cancelled.");

                return string.Empty;
            }

            log?.Error($"ATLauncher pack install failed: {staging.FailReason}");

            await prompts.ConfirmAsync(
                "Could not install the modpack",
                staging.FailReason.Length != 0 ? staging.FailReason : "The modpack could not be installed.",
                "OK").ConfigureAwait(true);

            return string.Empty;
        }

        log?.Info($"Installed '{chosen.Pack.Name}' as '{staging.CommittedId}' ({install.DownloadedCount} files)");

        return staging.CommittedId;
    }

    /// <summary>Fetches the catalogue from the ATLauncher CDN for the view model.</summary>
    private sealed class Source(HttpClient client) : IAtlPackSource
    {
        public Task<IReadOnlyList<AtlIndexedPack>> FetchAsync(CancellationToken cancellationToken)
            => AtlPackSource.FetchAsync(client, BuildConfig.Instance.AtlDownloadServerUrl, cancellationToken);
    }
}
