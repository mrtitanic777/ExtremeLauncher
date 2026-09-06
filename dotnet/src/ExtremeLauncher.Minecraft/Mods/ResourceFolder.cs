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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from launcher/minecraft/mod/tasks/{BasicFolderLoadTask.h,ModFolderLoadTask.cpp}.
 *
 * Reads one of an instance's managed folders and turns it into a list of resources. Every folder the
 * launcher shows -- mods, resource packs, shaders, saves -- comes through here.
 *
 * NOT PORTED: the metadata index (the `.index/` folder of per-mod TOML that records where each mod was
 * downloaded from). It needs MetadataHandler and the mod-platform layer from wave 8; without it every
 * resource simply reports NoMetadata, which is the state a hand-installed mod is in anyway.
 *
 * NOT PORTED EITHER: the thread hopping. Upstream runs these on a worker thread and moves each
 * QObject back with moveToThread, which is machinery for QObject affinity rather than behaviour.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft.Mods;

/// <summary>Whether a resource is tracked by the metadata index.</summary>
public enum ResourceStatus
{
    /// <summary>Present on disk, with no index entry saying where it came from.</summary>
    NoMetadata,

    /// <summary>Present on disk and recorded in the index.</summary>
    Installed,

    /// <summary>Recorded in the index but missing from disk.</summary>
    NotInstalled,
}

/// <summary>One entry in a scanned folder.</summary>
public sealed class FolderEntry
{
    public FolderEntry(Resource resource, ResourceStatus status)
    {
        Resource = resource;
        Status = status;
    }

    public Resource Resource { get; }

    public ResourceStatus Status { get; set; }
}

public static class ResourceFolder
{
    /// <summary>The suffix that marks a resource as disabled.</summary>
    private const string DisabledSuffix = ".disabled";

    /// <summary>
    /// Scans a folder, building one resource per entry.
    /// </summary>
    /// <param name="create">Builds the right kind of resource for a path.</param>
    /// <remarks>
    /// A DISABLED FILE SUPERSEDES ITS ENABLED TWIN. When both <c>foo.jar</c> and
    /// <c>foo.jar.disabled</c> exist, only the disabled one is listed: they are the same mod, and
    /// showing it twice would let a user enable one copy while the other is still there. The disabled
    /// entry is the one kept because it is the one whose state the user last chose.
    ///
    /// Directory order is not relied on: the pairing is resolved after everything has been seen, so a
    /// filesystem that lists ".disabled" first behaves the same as one that does not.
    /// </remarks>
    public static Dictionary<string, FolderEntry> Load(string directory, Func<string, Resource> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        var result = new Dictionary<string, FolderEntry>(StringComparer.Ordinal);

        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal))
        {
            var resource = create(path);

            result[resource.InternalId] = new FolderEntry(resource, ResourceStatus.NoMetadata);
        }

        // Resolved in a second pass so the answer does not depend on the order the directory listed.
        foreach (var id in result.Keys.Where(id => id.EndsWith(DisabledSuffix, StringComparison.Ordinal)).ToList())
        {
            result.Remove(id[..^DisabledSuffix.Length]);
        }

        return result;
    }

    /// <summary>Scans a mod folder, parsing each jar's metadata.</summary>
    /// <remarks>
    /// A jar with no recognisable metadata is still LISTED. It is in the folder, so the game will try
    /// to load it, and a user looking for why their game crashed needs to see it.
    /// </remarks>
    public static Dictionary<string, FolderEntry> LoadMods(string directory)
        => Load(directory, path =>
        {
            var mod = new Mod(path);
            ModUtils.Process(mod);

            return mod;
        });

    /// <summary>Scans a resourcepacks folder, parsing each pack's pack.mcmeta.</summary>
    /// <remarks>
    /// The parse is what gives the row a description and a pack format; a pack whose mcmeta will not
    /// read still appears, named after its file, because a pack the launcher cannot understand is one
    /// the user probably wants to find.
    /// </remarks>
    public static Dictionary<string, FolderEntry> LoadResourcePacks(string directory)
        => Load(directory, path =>
        {
            var pack = new ResourcePack(path);
            ResourcePackUtils.Process(pack);

            return pack;
        });

    /// <summary>Scans a texturepacks folder, parsing each pack's pack.txt.</summary>
    /// <remarks>
    /// The LEGACY format, from before pack.mcmeta existed: a texture pack is a zip or folder with a
    /// pack.txt description and, optionally, a pack.png. Parsed like resource packs, and a pack that
    /// will not read still gets a row named after its file, for the same reason.
    /// </remarks>
    public static Dictionary<string, FolderEntry> LoadTexturePacks(string directory)
        => Load(directory, path =>
        {
            var pack = new TexturePack(path);
            TexturePackUtils.Process(pack);

            return pack;
        });

    /// <summary>The mod folders an instance keeps, in the order the launcher scans them.</summary>
    /// <remarks>
    /// "coremods" is a pre-1.6 Forge arrangement and "nilmods" is NilLoader's; both are empty for
    /// almost every instance, but a launcher that skipped them would silently hide the mods of the
    /// people who still use them.
    /// </remarks>
    public static IReadOnlyList<string> ModFolderNames { get; } = ["mods", "coremods", "nilmods"];

    /// <summary>Scans every mod folder of a game directory.</summary>
    public static Dictionary<string, FolderEntry> LoadAllMods(string gameRoot)
    {
        var all = new Dictionary<string, FolderEntry>(StringComparer.Ordinal);

        foreach (var folder in ModFolderNames)
        {
            foreach (var (id, entry) in LoadMods(FileSystem.PathCombine(gameRoot, folder)))
            {
                // Keyed by filename, so the same name in two folders would collide. First wins, which
                // means "mods" takes precedence -- the folder anything modern lives in.
                all.TryAdd(id, entry);
            }
        }

        return all;
    }
}
