// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * Ported from launcher/modplatform/packwiz/Packwiz.{h,cpp}.
 *
 * THE METADATA INDEX. Every mod the launcher downloaded has a `.pw.toml` beside it in the instance's
 * `.index/` folder recording where it came from -- which provider, which project, which file. That is
 * what makes "update this mod" possible at all: a jar on disk says what it is, but not where to look
 * for a newer one.
 *
 * The format is packwiz's, deliberately, so an instance's index is readable by the packwiz CLI and by
 * other launchers that speak it. The `x-extremelauncher-*` keys are this launcher's own additions;
 * packwiz ignores unknown keys, so they travel harmlessly.
 *
 * NOT PORTED: the CurseForge/Modrinth API clients that fill these files in. This is the on-disk format
 * only, which is the half that has an inherited test.
 */

using System.Globalization;
using System.Text;
using ExtremeLauncher.Core;
using Tomlyn.Model;

namespace ExtremeLauncher.ModPlatform;

/// <summary>Which side of a client/server split a mod belongs on.</summary>
public enum PackwizSide
{
    ClientSide,
    ServerSide,
    UniversalSide,
}

/// <summary>One mod's entry in the metadata index.</summary>
public sealed class PackwizMod
{
    /// <summary>The index filename without its extension. Identifies the entry.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The jar's name on disk, which is how the entry is matched to a file.</summary>
    public string Filename { get; set; } = string.Empty;

    /// <remarks>Defaults to universal: a mod that does not say is assumed to work on both.</remarks>
    public PackwizSide Side { get; set; } = PackwizSide.UniversalSide;

    public string Mode { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string HashFormat { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public ResourceProvider Provider { get; set; } = ResourceProvider.Modrinth;

    // CurseForge identifies a download by two integers...
    public int FileId { get; set; }

    public int ProjectId { get; set; }

    // ...and Modrinth by two opaque strings. Only one pair is ever populated.
    public string ModId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>Launcher-specific: the loaders this file is for.</summary>
    public List<string> Loaders { get; } = [];

    /// <summary>Launcher-specific: the Minecraft versions this file is for.</summary>
    public List<string> McVersions { get; } = [];

    public string ReleaseType { get; set; } = string.Empty;

    /// <summary>
    /// Whether the entry names a file that can actually be fetched.
    /// </summary>
    /// <remarks>
    /// A slug and a provider identifier are the minimum. Without them the entry cannot be matched to a
    /// file on disk or resolved against an API, so it is worse than absent -- it looks like tracking
    /// that is not there.
    /// </remarks>
    public bool IsValid
        => Slug.Length != 0
           && (Provider == ResourceProvider.Flame
               ? FileId != 0 && ProjectId != 0
               : ModId.Length != 0 && Version.Length != 0);

    public override string ToString() => $"{Slug} ({Provider})";
}

public static class Packwiz
{
    private const string IndexExtension = ".pw.toml";

    /// <summary>The provider names as they appear in the file. "curseforge", not "flame".</summary>
    // The provider names its own spelling; packwiz uses exactly those strings.
    private static string ProviderName(ResourceProvider provider) => ProviderCapabilities.Name(provider);

    public static string SideToString(PackwizSide side) => side switch
    {
        PackwizSide.ClientSide => "client",
        PackwizSide.ServerSide => "server",
        PackwizSide.UniversalSide => "both",
        _ => string.Empty,
    };

    /// <remarks>Anything unrecognised, including an empty string, is universal.</remarks>
    public static PackwizSide StringToSide(string side) => side switch
    {
        "client" => PackwizSide.ClientSide,
        "server" => PackwizSide.ServerSide,
        _ => PackwizSide.UniversalSide,
    };

    /// <summary>The index filename for a slug, adding the extension if it is not already there.</summary>
    public static string IndexFileName(string modSlug)
        => modSlug.EndsWith(IndexExtension, StringComparison.Ordinal) ? modSlug : modSlug + IndexExtension;

    /// <summary>
    /// Finds the index file for a slug, tolerating a difference in case.
    /// </summary>
    /// <remarks>
    /// Index files are named after a provider's slug, and providers are not consistent about case.
    /// A case-insensitive match means an entry written on Windows is still found on Linux, where the
    /// filesystem would otherwise treat "JEI.pw.toml" and "jei.pw.toml" as different files.
    /// </remarks>
    /// <param name="mustExist">
    /// When true, a missing file yields an empty string rather than the name it would have.
    /// </param>
    public static string GetRealIndexName(string indexDirectory, string normalizedName, bool mustExist = false)
    {
        if (File.Exists(FileSystem.PathCombine(indexDirectory, normalizedName)))
        {
            return normalizedName;
        }

        if (Directory.Exists(indexDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(indexDirectory))
            {
                var name = Path.GetFileName(path);

                if (string.Equals(name, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return mustExist ? string.Empty : normalizedName;
    }

    // ================================================================== reading

    /// <summary>Reads one mod's index entry.</summary>
    /// <returns>An entry with an empty slug when the file is missing or unusable.</returns>
    public static PackwizMod GetIndexForMod(string indexDirectory, string slug)
    {
        var normalized = IndexFileName(slug);
        var real = GetRealIndexName(indexDirectory, normalized, mustExist: true);

        if (real.Length == 0)
        {
            return new PackwizMod();
        }

        TomlTable table;

        try
        {
            var text = File.ReadAllText(FileSystem.PathCombine(indexDirectory, real));

            if (!Tomlyn.Toml.TryToModel<TomlTable>(text, out var parsed, out _) || parsed is null)
            {
                return new PackwizMod();
            }

            table = parsed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PackwizMod();
        }

        // The slug is the FILENAME, not anything inside the file: the entry has to be findable from
        // the name alone, and nothing in the document repeats it.
        var mod = new PackwizMod
        {
            Slug = slug.EndsWith(IndexExtension, StringComparison.Ordinal)
                ? slug[..^IndexExtension.Length]
                : slug,

            Name = StringEntry(table, "name"),
            Filename = StringEntry(table, "filename"),
            Side = StringToSide(StringEntry(table, "side")),
            ReleaseType = StringEntry(table, "x-extremelauncher-release-type"),
        };

        foreach (var loader in StringArray(table, "x-extremelauncher-loaders"))
        {
            mod.Loaders.Add(loader);
        }

        foreach (var version in StringArray(table, "x-extremelauncher-mc-versions"))
        {
            mod.McVersions.Add(version);
        }

        // Sorted, so a written file is stable regardless of the order the API returned them.
        mod.McVersions.Sort(StringComparer.Ordinal);

        // Both sections are REQUIRED. Without [download] there is nothing to fetch; without [update]
        // there is no way to look for a newer version, which is the only reason the index exists.
        if (SubTable(table, "download") is not { } download)
        {
            return new PackwizMod();
        }

        mod.Mode = StringEntry(download, "mode");
        mod.Url = StringEntry(download, "url");
        mod.HashFormat = StringEntry(download, "hash-format");
        mod.Hash = StringEntry(download, "hash");

        if (SubTable(table, "update") is not { } update)
        {
            return new PackwizMod();
        }

        // CurseForge is checked first, matching upstream. A file naming both would be malformed.
        if (SubTable(update, ProviderName(ResourceProvider.Flame)) is { } flame)
        {
            mod.Provider = ResourceProvider.Flame;
            mod.FileId = IntEntry(flame, "file-id");
            mod.ProjectId = IntEntry(flame, "project-id");
        }
        else if (SubTable(update, ProviderName(ResourceProvider.Modrinth)) is { } modrinth)
        {
            mod.Provider = ResourceProvider.Modrinth;
            mod.ModId = StringEntry(modrinth, "mod-id");
            mod.Version = StringEntry(modrinth, "version");
        }
        else
        {
            return new PackwizMod();
        }

        return mod;
    }

    /// <summary>Finds the entry whose provider id matches, by reading every file in the index.</summary>
    /// <remarks>
    /// A linear scan, as upstream does it. The index holds one file per mod, so this is proportional
    /// to the mod count -- fine for the hundreds an instance has, and it avoids a second index that
    /// could drift out of step with the files.
    /// </remarks>
    public static PackwizMod GetIndexForModId(string indexDirectory, string modId)
    {
        if (!Directory.Exists(indexDirectory))
        {
            return new PackwizMod();
        }

        foreach (var path in Directory.EnumerateFiles(indexDirectory))
        {
            var mod = GetIndexForMod(indexDirectory, Path.GetFileName(path));

            if (mod.ModId == modId && modId.Length != 0)
            {
                return mod;
            }
        }

        return new PackwizMod();
    }

    // ================================================================== writing

    /// <summary>
    /// Writes or replaces a mod's index entry.
    /// </summary>
    /// <remarks>
    /// An INVALID entry is refused rather than written, because a half-filled file looks like tracking
    /// that is not there: the launcher would offer to update a mod it cannot resolve.
    ///
    /// An existing file under a differently-cased name is RENAMED to the normalized one, so the index
    /// converges on a single spelling rather than accumulating both.
    /// </remarks>
    public static bool UpdateModIndex(string indexDirectory, PackwizMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        if (!mod.IsValid)
        {
            return false;
        }

        var normalized = IndexFileName(mod.Slug);
        var real = GetRealIndexName(indexDirectory, normalized);

        var target = FileSystem.PathCombine(indexDirectory, normalized);

        try
        {
            FileSystem.EnsureFilePathExists(target);

            if (real != normalized && File.Exists(FileSystem.PathCombine(indexDirectory, real)))
            {
                File.Move(FileSystem.PathCombine(indexDirectory, real), target, overwrite: true);
            }

            File.WriteAllText(target, Serialize(mod));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes a mod's index entry.</summary>
    public static bool DeleteModIndex(string indexDirectory, string slug)
    {
        var real = GetRealIndexName(indexDirectory, IndexFileName(slug), mustExist: true);

        return real.Length != 0 && FileSystem.DeletePath(FileSystem.PathCombine(indexDirectory, real));
    }

    /// <summary>
    /// Renders an entry as packwiz TOML.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than through a serializer so the key ORDER and the section layout match
    /// what packwiz itself produces -- these files are read by other tools, and a diff against a
    /// packwiz-managed index should be empty rather than a reshuffle.
    /// </remarks>
    public static string Serialize(PackwizMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        var builder = new StringBuilder();

        builder.Append("name = ").Append(Quote(mod.Name)).Append('\n');
        builder.Append("filename = ").Append(Quote(mod.Filename)).Append('\n');
        builder.Append("side = ").Append(Quote(SideToString(mod.Side))).Append('\n');

        if (mod.ReleaseType.Length != 0)
        {
            builder.Append("x-extremelauncher-release-type = ").Append(Quote(mod.ReleaseType)).Append('\n');
        }

        if (mod.Loaders.Count != 0)
        {
            builder.Append("x-extremelauncher-loaders = ").Append(QuoteArray(mod.Loaders)).Append('\n');
        }

        if (mod.McVersions.Count != 0)
        {
            builder.Append("x-extremelauncher-mc-versions = ").Append(QuoteArray(mod.McVersions)).Append('\n');
        }

        builder.Append("\n[download]\n");

        if (mod.Mode.Length != 0)
        {
            builder.Append("mode = ").Append(Quote(mod.Mode)).Append('\n');
        }

        builder.Append("url = ").Append(Quote(mod.Url)).Append('\n');
        builder.Append("hash-format = ").Append(Quote(mod.HashFormat)).Append('\n');
        builder.Append("hash = ").Append(Quote(mod.Hash)).Append('\n');

        builder.Append("\n[update]\n");
        builder.Append('[').Append("update.").Append(ProviderName(mod.Provider)).Append("]\n");

        if (mod.Provider == ResourceProvider.Flame)
        {
            builder.Append("file-id = ").Append(mod.FileId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("project-id = ").Append(mod.ProjectId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        else
        {
            builder.Append("mod-id = ").Append(Quote(mod.ModId)).Append('\n');
            builder.Append("version = ").Append(Quote(mod.Version)).Append('\n');
        }

        return builder.ToString();
    }

    // ================================================================== helpers

    private static string Quote(string value)
        => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                      .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static string QuoteArray(IEnumerable<string> values)
        => "[" + string.Join(", ", values.Select(Quote)) + "]";

    /// <summary>
    /// Reads a sub-table, or null when the key is absent or holds something else.
    /// </summary>
    /// <remarks>
    /// BEHAVIOUR GAP: Tomlyn's TomlTable indexer THROWS a KeyNotFoundException on a missing key, where
    /// System.Text.Json's JsonObject returns null for one. Every lookup here is on a key that a
    /// malformed file may simply not have, so none of them can use the indexer.
    /// </remarks>
    private static TomlTable? SubTable(TomlTable table, string key)
        => table.TryGetValue(key, out var value) ? value as TomlTable : null;

    private static string StringEntry(TomlTable table, string name)
        => table.TryGetValue(name, out var value) && value is string text ? text : string.Empty;

    /// <remarks>
    /// TOML integers are 64-bit; these ids fit an int and upstream reads them as one, so a value that
    /// does not fit is treated as absent rather than truncated into a different id.
    /// </remarks>
    private static int IntEntry(TomlTable table, string name)
    {
        if (!table.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value switch
        {
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            int number => number,
            string text when int.TryParse(text, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static IEnumerable<string> StringArray(TomlTable table, string name)
    {
        if (!table.TryGetValue(name, out var value) || value is not TomlArray array)
        {
            yield break;
        }

        foreach (var item in array)
        {
            if (item is string text && text.Length != 0)
            {
                yield return text;
            }
        }
    }
}
