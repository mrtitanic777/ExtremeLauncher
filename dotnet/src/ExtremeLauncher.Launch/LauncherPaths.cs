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
 * The launcher's own data directory, and everything under it.
 *
 * PORTABLE BY DEFAULT, as upstream is: everything lives beside the executable unless a data directory
 * is given. That is what lets a whole install be moved to another machine, or run from a USB stick,
 * which is a real thing people do with this launcher lineage.
 *
 * THESE PATHS ARE A COMPATIBILITY SURFACE. An existing Prism or MultiMC install has exactly this
 * layout, so pointing this launcher at one has to find the same instances, the same cached libraries
 * and the same accounts.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Launch;

public sealed class LauncherPaths
{
    public LauncherPaths(string? dataDirectory = null)
        => Root = FileSystem.CleanPath(Path.GetFullPath(dataDirectory ?? AppContext.BaseDirectory));

    /// <summary>The launcher's data directory.</summary>
    public string Root { get; }

    /// <summary>One directory per instance, each holding an instance.cfg.</summary>
    public string Instances => FileSystem.PathCombine(Root, "instances");

    /// <summary>Shared between instances: the same jar is never downloaded twice.</summary>
    public string Libraries => FileSystem.PathCombine(Root, "libraries");

    /// <summary>Shared, and content-addressed, so identical assets across versions cost nothing.</summary>
    public string Assets => FileSystem.PathCombine(Root, "assets");

    /// <summary>The cached copy of the metadata tree.</summary>
    public string Meta => FileSystem.PathCombine(Root, "meta");

    /// <summary>Downloaded Java runtimes, one directory per runtime name.</summary>
    public string Java => FileSystem.PathCombine(Root, "java");

    /// <summary>Where the HTTP cache keeps its bookkeeping.</summary>
    public string CacheIndex => FileSystem.PathCombine(Root, "metacache");

    public string LauncherConfig => FileSystem.PathCombine(Root, BuildConfig.Instance.LauncherConfigFile);

    public string Accounts => FileSystem.PathCombine(Root, "accounts.json");

    /// <summary>Creates the directories the launcher writes into.</summary>
    public void EnsureExists()
    {
        foreach (var directory in new[] { Root, Instances, Libraries, Assets, Meta, Java })
        {
            FileSystem.EnsureFolderPathExists(directory);
        }
    }

    /// <summary>
    /// Builds the HTTP cache with the bases the download code names.
    /// </summary>
    /// <remarks>
    /// The base names are not decorative: NetRequest resolves a cache entry by base plus relative
    /// path, so a missing base means those downloads land somewhere unexpected or fail outright.
    /// </remarks>
    public Net.HttpMetaCache CreateCache()
    {
        var cache = new Net.HttpMetaCache(FileSystem.PathCombine(CacheIndex, "index.json"));

        cache.AddBase("meta", Meta);
        cache.AddBase("libraries", Libraries);
        cache.AddBase("assets", Assets);
        cache.AddBase("asset_indexes", FileSystem.PathCombine(Assets, "indexes"));
        cache.AddBase("fmllibs", FileSystem.PathCombine(Root, "cache", "fmllibs"));
        cache.AddBase("java", FileSystem.PathCombine(Root, "cache", "java"));

        return cache;
    }
}
