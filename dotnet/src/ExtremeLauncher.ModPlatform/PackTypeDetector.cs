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
 * Ported from the detection half of launcher/InstanceImportTask.cpp.
 *
 * WHAT KIND OF PACK IS THIS ZIP? The user drops in a file and every format is just a zip with
 * different things inside, so the launcher has to work it out from the contents. Five formats, four
 * marker files, and an ordering that is load-bearing.
 *
 * THE ORDER IS THE DESIGN, and upstream's comment says why: "prioritize modpack platforms that aren't
 * searched for recursively. Especially Flame has a very common filename for its manifest, which may
 * appear inside overrides". A CurseForge pack is identified by `manifest.json` -- a name common enough
 * that some *other* pack's overrides folder may contain one. So the formats with distinctive markers
 * are checked at the root first, and only then does the recursive search begin.
 *
 * AND THE RECURSION SKIPS `overrides/` FOR THE SAME REASON. Everything under it is the pack's payload,
 * not its metadata; a `manifest.json` found there belongs to something the pack ships, not to the pack.
 */

using System.IO.Compression;

namespace ExtremeLauncher.ModPlatform;

/// <summary>The pack formats an archive can turn out to be.</summary>
public enum ModpackType
{
    Unknown,

    /// <summary>A .mrpack: modrinth.index.json at the root.</summary>
    Modrinth,

    /// <summary>A CurseForge export: manifest.json, possibly nested.</summary>
    Flame,

    /// <summary>An exported MultiMC/Prism instance: instance.cfg, possibly nested.</summary>
    MultiMc,

    /// <summary>A Technic pack: bin/modpack.jar or bin/version.json.</summary>
    Technic,
}

/// <summary>What an archive turned out to contain.</summary>
/// <param name="Type">The format, or <see cref="ModpackType.Unknown"/>.</param>
/// <param name="Root">
/// The directory inside the archive the pack actually starts at, with a trailing slash, or empty for
/// the archive root. Packs are routinely zipped with a wrapping folder.
/// </param>
/// <param name="ExtractSubdirectory">
/// A directory to extract INTO, relative to the staging path. Only Technic uses one: its archive is
/// the game directory itself rather than an instance, so it goes under "minecraft".
/// </param>
public readonly record struct PackDetection(ModpackType Type, string Root, string ExtractSubdirectory = "");

public static class PackTypeDetector
{
    public const string ModrinthMarker = "modrinth.index.json";
    public const string FlameMarker = "manifest.json";
    public const string MultiMcMarker = "instance.cfg";

    /// <summary>The folder whose contents are the pack's payload rather than its metadata.</summary>
    public const string OverridesFolder = "overrides/";

    /// <summary>Identifies the pack format in an archive.</summary>
    public static PackDetection Detect(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        // Normalised once: the rest of this works on forward slashes with no leading one.
        var entries = archive.Entries
            .Select(e => e.FullName.Replace('\\', '/').TrimStart('/'))
            .ToList();

        /*
         * ROOT-ONLY MARKERS FIRST. These names are distinctive enough that finding one anywhere but
         * the root would be a coincidence, and checking them before the recursive search is what stops
         * a Modrinth pack that happens to ship a manifest.json in its overrides from being taken for a
         * CurseForge one.
         */
        if (entries.Contains(ModrinthMarker, StringComparer.Ordinal))
        {
            return new PackDetection(ModpackType.Modrinth, string.Empty);
        }

        if (entries.Contains("bin/modpack.jar", StringComparer.Ordinal)
            || entries.Contains("bin/version.json", StringComparer.Ordinal))
        {
            /*
             * A Technic archive IS the game directory -- bin/, config/, mods/ -- rather than an
             * instance containing one, so it is extracted a level down. Upstream creates and enters
             * "minecraft" before extracting for exactly this.
             */
            return new PackDetection(ModpackType.Technic, string.Empty, "minecraft");
        }

        return SearchForNestedMarker(entries);
    }

    /// <summary>Identifies the pack format in an archive on disk.</summary>
    public static PackDetection Detect(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        return Detect(archive);
    }

    /// <summary>
    /// Walks the archive looking for a marker, skipping <c>overrides/</c>.
    /// </summary>
    /// <remarks>
    /// Upstream recurses directory by directory, checking every FILE in a directory before descending
    /// into any of its subdirectories — so a marker nearer the root always wins over a deeper one, and
    /// within one directory <c>instance.cfg</c> is found before <c>manifest.json</c> only because the
    /// entry list happens to be ordered. Made explicit here: shallowest first, and MultiMC before
    /// Flame at equal depth, because an exported instance that also carries a CurseForge manifest is
    /// still an instance.
    /// </remarks>
    private static PackDetection SearchForNestedMarker(List<string> entries)
    {
        PackDetection? best = null;
        var bestDepth = int.MaxValue;

        foreach (var entry in entries)
        {
            var slash = entry.LastIndexOf('/');
            var directory = slash < 0 ? string.Empty : entry[..(slash + 1)];
            var fileName = entry[(slash + 1)..];

            var type = fileName switch
            {
                MultiMcMarker => ModpackType.MultiMc,
                FlameMarker => ModpackType.Flame,
                _ => ModpackType.Unknown,
            };

            if (type == ModpackType.Unknown)
            {
                continue;
            }

            // Anything under overrides/ is the pack's payload, not its metadata.
            if (IsUnderOverrides(directory))
            {
                continue;
            }

            var depth = directory.Length == 0 ? 0 : directory.Count(c => c == '/');

            // Shallower wins; at equal depth an instance beats a manifest.
            if (depth > bestDepth || (depth == bestDepth && type != ModpackType.MultiMc))
            {
                continue;
            }

            best = new PackDetection(type, directory);
            bestDepth = depth;
        }

        return best ?? new PackDetection(ModpackType.Unknown, string.Empty);
    }

    private static bool IsUnderOverrides(string directory)
        => directory.StartsWith(OverridesFolder, StringComparison.Ordinal)
            || directory.Contains("/" + OverridesFolder, StringComparison.Ordinal);
}
