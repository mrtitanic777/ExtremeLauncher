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
 * The app side of the classic FTB browser: shows the window, then stages and installs the chosen pack
 * behind a progress window, mirroring AppPackBrowser.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Tasks;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppLegacyFtbBrowser(
    Func<Window?> owner,
    HttpClient client,
    IUserPrompts prompts,
    LauncherLog? log,
    LauncherPaths? paths) : ILegacyFtbBrowser
{
    public async Task<string> BrowseAndInstallAsync(InstanceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (owner() is not { } parent)
        {
            return string.Empty;
        }

        var model = new LegacyFtbBrowserViewModel(new Source(client));
        var window = new LegacyFtbBrowserWindow(model);

        await window.ShowDialog(parent).ConfigureAwait(true);

        if (window.Chosen is not { } chosen)
        {
            return string.Empty;
        }

        log?.Info($"Installing legacy FTB pack {chosen.Pack.Name} {chosen.Version}");

        var install = new LegacyFtbPackInstallTask(
            chosen.Pack,
            chosen.Version,
            LauncherService.CurrentRuntimeContext(),
            client,
            instanceName: window.InstanceName);

        var staging = new InstanceStagingTask(list, install, install);

        if (!await ProgressWindow.RunAsync(parent, staging, $"Installing {chosen.Pack.Name}").ConfigureAwait(true))
        {
            if (staging.State == TaskState.AbortedByUser)
            {
                log?.Info("FTB pack install cancelled.");

                return string.Empty;
            }

            log?.Error($"FTB pack install failed: {staging.FailReason}");

            await prompts.ConfirmAsync(
                "Could not install the modpack",
                staging.FailReason.Length != 0 ? staging.FailReason : "The modpack could not be installed.",
                "OK").ConfigureAwait(true);

            return string.Empty;
        }

        log?.Info($"Installed '{chosen.Pack.Name}' as '{staging.CommittedId}'");

        return staging.CommittedId;
    }

    /// <summary>Fetches the catalogue from the FTB CDN for the view model.</summary>
    private sealed class Source(HttpClient client) : ILegacyFtbSource
    {
        private readonly LegacyFtbPackSource _source = new(client);

        public Task<LegacyFtbFetchResult> FetchAsync(CancellationToken cancellationToken)
            => _source.FetchAsync(cancellationToken);
    }
}
