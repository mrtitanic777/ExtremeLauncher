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
 * Opening the download dialog and running what it returns.
 *
 * The dialog picks; this fetches. ResourceDownloadTask does the actual work and has been tested since
 * wave 8 -- it writes the jar and, for mods, the packwiz metadata beside it that lets the launcher
 * know afterwards where a file came from.
 *
 * DOWNLOADS ARE RUN ONE AT A TIME rather than all at once. Ten mods is ten small files from one CDN,
 * and the difference in wall-clock is seconds; what serial buys is a failure that names the mod it
 * happened on, and a partial install where everything before the failure is complete and usable.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Net;
using ExtremeLauncher.ViewModels;

// Avalonia.Controls has a ResourceProvider of its own; this is the mod-platform one.
using ResourceProvider = ExtremeLauncher.ModPlatform.ResourceProvider;

namespace ExtremeLauncher.App;

public sealed class AppModInstaller(
    Func<Window?> owner,
    HttpClient client,
    IUserPrompts prompts,
    Func<(string Minecraft, string Loader)> instanceInfo,
    LauncherLog? log = null) : IModInstaller
{
    public async Task<int> AddAsync(string gameRoot, ResourceFolderKind kind)
    {
        var (minecraft, loader) = instanceInfo();

        var search = new AppResourceSearch(new ResourceSearchSource(client), minecraft, loader, kind);

        var model = new ModDownloadViewModel(search, minecraft, loader);
        var window = new ModDownloadWindow(model);

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }

        if (window.Chosen.Count == 0)
        {
            return 0;
        }

        return await InstallAsync(window.Chosen, gameRoot, kind).ConfigureAwait(true);
    }

    private async Task<int> InstallAsync(
        IReadOnlyList<(IndexedPack Pack, IndexedVersion Version)> chosen,
        string gameRoot,
        ResourceFolderKind kind)
    {
        var folder = FileSystem.PathCombine(gameRoot, FolderNameFor(kind));

        FileSystem.EnsureFolderPathExists(folder);

        /*
         * ONLY MODS GET A METADATA INDEX. A resource pack is installed as a bare file -- upstream
         * tests the model type before building the task for exactly this reason. Passing an index
         * directory for a resource pack would write packwiz entries for something nothing tracks.
         */
        var isMod = kind == ResourceFolderKind.Mods;
        var indexDirectory = isMod ? FileSystem.PathCombine(gameRoot, ".index") : string.Empty;

        var cache = new HttpMetaCache();
        var installed = 0;
        var failures = new List<string>();

        foreach (var (pack, version) in chosen)
        {
            var task = new ResourceDownloadTask(
                pack,
                version,
                folder,
                indexDirectory,
                client,
                cache,
                isIndexed: isMod);

            try
            {
                await task.RunAsync(CancellationToken.None).ConfigureAwait(true);

                installed++;

                log?.Info($"Installed {pack.Name} ({version.FileName}) into {folder}");
            }
            catch (Exception e) when (e is LauncherException or IOException or HttpRequestException)
            {
                // Named, and the rest still attempted: one mod being unavailable is not a reason to
                // abandon the other nine the user asked for.
                failures.Add($"{pack.Name}: {e.Message}");

                log?.Warning($"Could not install {pack.Name}: {e.Message}");
            }
        }

        if (failures.Count != 0)
        {
            await prompts.ConfirmAsync(
                "Some mods could not be installed",
                string.Join("\n\n", failures),
                "OK").ConfigureAwait(true);
        }

        return installed;
    }

    /// <summary>Where each kind of resource lives inside the instance.</summary>
    private static string FolderNameFor(ResourceFolderKind kind) => kind switch
    {
        ResourceFolderKind.ResourcePacks => "resourcepacks",
        ResourceFolderKind.ShaderPacks => "shaderpacks",
        _ => "mods",
    };

    /// <summary>Adapts the launch layer's search to the view models' interface, with the instance's filters baked in.</summary>
    private sealed class AppResourceSearch(
        ResourceSearchSource source,
        string minecraftVersion,
        string loader,
        ResourceFolderKind kind) : IResourceSearch
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
                new SearchArgs
                {
                    Type = TypeFor(kind),
                    Search = query,

                    /*
                     * A RESOURCE PACK IS NOT FILTERED BY LOADER, and a mod is. Sending Fabric as a
                     * facet on a resource-pack search returns nothing at all on Modrinth -- resource
                     * packs declare no loader, so the filter excludes every one of them.
                     */
                    Loaders = kind == ResourceFolderKind.Mods ? LoaderFor(loader) : null,
                    Versions = minecraftVersion.Length == 0 ? null : [new Core.Version(minecraftVersion)],
                },
                cancellationToken);

        public Task LoadVersionsAsync(IndexedPack pack, CancellationToken cancellationToken)
            => source.LoadVersionsAsync(
                pack,
                new VersionSearchArgs
                {
                    Pack = pack,
                    Loaders = kind == ResourceFolderKind.Mods ? LoaderFor(loader) : null,
                    McVersions = minecraftVersion.Length == 0 ? null : [new Core.Version(minecraftVersion)],
                },
                cancellationToken);

        private static ResourceType TypeFor(ResourceFolderKind kind) => kind switch
        {
            ResourceFolderKind.ResourcePacks => ResourceType.ResourcePack,
            ResourceFolderKind.ShaderPacks => ResourceType.ShaderPack,
            _ => ResourceType.Mod,
        };

        /// <remarks>
        /// Null rather than None when the instance has no loader: None is a filter matching nothing,
        /// while null is no filter at all -- and a vanilla instance searching for mods should see what
        /// exists rather than an empty list.
        /// </remarks>
        private static ModLoaderTypes? LoaderFor(string loader) => loader switch
        {
            "Fabric" => ModLoaderTypes.Fabric,
            "Quilt" => ModLoaderTypes.Quilt,
            "Forge" => ModLoaderTypes.Forge,
            "NeoForge" => ModLoaderTypes.NeoForge,
            _ => null,
        };
    }
}
