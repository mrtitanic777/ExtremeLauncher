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
 * The app side of the Technic browser: shows the window, then resolves the chosen pack's detail (which
 * says whether it is a single-zip or a Solder pack, and its version) and installs it behind a progress
 * window. The detail is a second request — the search results carry only name/slug/icon.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Tasks;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppTechnicBrowser(
    Func<Window?> owner,
    HttpClient client,
    IUserPrompts prompts,
    LauncherLog? log,
    LauncherPaths? paths) : ITechnicBrowser
{
    public async Task<string> BrowseAndInstallAsync(InstanceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (owner() is not { } parent)
        {
            return string.Empty;
        }

        var model = new TechnicBrowserViewModel(new Source(client));
        var window = new TechnicBrowserWindow(model);

        await window.ShowDialog(parent).ConfigureAwait(true);

        if (window.Chosen is not { } chosen)
        {
            return string.Empty;
        }

        var apiBase = BuildConfig.Instance.TechnicApiBaseUrl;
        var apiBuild = BuildConfig.Instance.TechnicApiBuild;

        TechnicPackDetail? detail;

        try
        {
            var (url, _) = TechnicSearch.SearchUrl(apiBase, apiBuild, "#" + chosen.Slug);
            var data = await client.GetByteArrayAsync(url).ConfigureAwait(true);
            detail = TechnicDetail.Parse(Json.RequireObject(Json.RequireDocument(data, "technic detail")));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            log?.Error($"Could not read Technic pack '{chosen.Slug}': {e.Message}");
            detail = null;
        }

        if (detail is null)
        {
            await prompts.ConfirmAsync(
                "Could not install the modpack",
                $"Could not read the details of '{chosen.Name}'.",
                "OK").ConfigureAwait(true);

            return string.Empty;
        }

        log?.Info($"Installing Technic pack {chosen.Name} ({(detail.IsSolder ? "Solder" : "single-zip")})");

        var runtimeContext = LauncherService.CurrentRuntimeContext();

        IInstanceTask install = detail.IsSolder
            ? new TechnicSolderInstallTask(
                detail.Url, chosen.Slug, detail.CurrentVersion, detail.MinecraftVersion,
                runtimeContext, client, instanceName: window.InstanceName)
            : new TechnicSingleZipInstallTask(
                detail.Url, detail.MinecraftVersion, runtimeContext, client, instanceName: window.InstanceName);

        var staging = new InstanceStagingTask(list, (LauncherTask)install, install);

        if (!await ProgressWindow.RunAsync(parent, staging, $"Installing {chosen.Name}").ConfigureAwait(true))
        {
            if (staging.State == TaskState.AbortedByUser)
            {
                log?.Info("Technic pack install cancelled.");

                return string.Empty;
            }

            log?.Error($"Technic pack install failed: {staging.FailReason}");

            await prompts.ConfirmAsync(
                "Could not install the modpack",
                staging.FailReason.Length != 0 ? staging.FailReason : "The modpack could not be installed.",
                "OK").ConfigureAwait(true);

            return string.Empty;
        }

        log?.Info($"Installed '{chosen.Name}' as '{staging.CommittedId}'");

        return staging.CommittedId;
    }

    /// <summary>Searches the Technic platform for the view model.</summary>
    private sealed class Source(HttpClient client) : ITechnicPackSource
    {
        public Task<IReadOnlyList<TechnicModpack>> SearchAsync(string term, CancellationToken cancellationToken)
            => TechnicPackSource.SearchAsync(
                client,
                BuildConfig.Instance.TechnicApiBaseUrl,
                BuildConfig.Instance.TechnicApiBuild,
                term,
                cancellationToken);
    }
}
