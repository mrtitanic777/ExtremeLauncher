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
 * Ported from launcher/minecraft/mod/{ResourcePack,DataPack,TexturePack,ShaderPack,WorldSave}.{h,cpp}
 * and their parsers in launcher/minecraft/mod/tasks/Local*ParseTask.cpp.
 *
 * The five resource kinds that can be identified by looking at their contents. Each answers one
 * question — is this a valid X, and what does it say about itself — and each supports both a folder
 * and a zip, because users drop both into the same folders.
 *
 * ONE VALIDITY RULE PER KIND, and they differ in ways worth knowing:
 *   ResourcePack  pack.mcmeta REQUIRED, plus an "assets" directory.
 *   DataPack      pack.mcmeta REQUIRED, plus a "data" directory.
 *   TexturePack   pack.txt required; the format predates pack.mcmeta entirely.
 *   ShaderPack    a "shaders" directory. Nothing else; there is no manifest.
 *   WorldSave     a directory containing level.dat, possibly under "saves".
 */

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft.Mods;

// ====================================================================== resource packs

public sealed class ResourcePack : Resource
{
    public ResourcePack(string path) : base(path)
    {
    }

    /// <summary>The pack_format number from pack.mcmeta. Zero when unknown.</summary>
    public int PackFormat { get; set; }

    /// <summary>The description, with Minecraft's text components rendered to HTML.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The raw bytes of pack.png, when it was read.</summary>
    public byte[] Image { get; set; } = [];

    public override bool Valid => PackFormat != 0;
}

public static class ResourcePackUtils
{
    public static bool Process(ResourcePack pack, ProcessingLevel level = ProcessingLevel.Full)
        => pack.Type switch
        {
            ResourceType.Folder => ProcessFolder(pack, level),
            ResourceType.ZipFile => ProcessZip(pack, level),
            _ => false,
        };

    public static bool ProcessFolder(ResourcePack pack, ProcessingLevel level = ProcessingLevel.Full)
    {
        var mcmeta = FileSystem.PathCombine(pack.Path, "pack.mcmeta");

        // The mcmeta is NOT optional: without it there is nothing to distinguish a resource pack from
        // any other folder of files.
        if (!File.Exists(mcmeta) || !ProcessMcMeta(pack, ReadAllBytes(mcmeta)))
        {
            return false;
        }

        if (!Directory.Exists(FileSystem.PathCombine(pack.Path, "assets")))
        {
            return false;
        }

        if (level == ProcessingLevel.BasicInfoOnly)
        {
            return true;
        }

        var image = FileSystem.PathCombine(pack.Path, "pack.png");

        if (File.Exists(image))
        {
            pack.Image = ReadAllBytes(image);
        }

        return true;
    }

    public static bool ProcessZip(ResourcePack pack, ProcessingLevel level = ProcessingLevel.Full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(pack.Path);

            if (zip.GetEntry("pack.mcmeta") is not { } mcmeta || !ProcessMcMeta(pack, ReadEntry(mcmeta)))
            {
                return false;
            }

            if (!HasDirectory(zip, "assets"))
            {
                return false;
            }

            if (level == ProcessingLevel.BasicInfoOnly)
            {
                return true;
            }

            if (zip.GetEntry("pack.png") is { } image)
            {
                pack.Image = ReadEntry(image);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool ProcessMcMeta(ResourcePack pack, byte[] rawData)
    {
        try
        {
            var root = ParseManifest(rawData);
            var packObject = Json.RequireObject(root, "pack");

            pack.PackFormat = Json.EnsureInteger(packObject, "pack_format");
            pack.Description = ProcessComponent(packObject["description"]);

            return true;
        }
        catch (Exception e) when (e is JsonException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renders one of Minecraft's text components to HTML.
    /// </summary>
    /// <remarks>
    /// A component is a string, a number, a bool, an array of components, or an object with "text",
    /// styling flags and a nested "extra" array. STYLES INHERIT INTO "extra" — but only underline and
    /// strikethrough, which is why those two are threaded through as parameters while colour, bold and
    /// italic are re-read from each object. That asymmetry is upstream's and is what the inherited test
    /// vectors pin.
    /// </remarks>
    public static string ProcessComponent(JsonNode? value, bool strikethrough = false, bool underline = false)
    {
        switch (value)
        {
            case null:
                return string.Empty;

            case JsonArray array:
            {
                var builder = new StringBuilder();

                foreach (var item in array)
                {
                    builder.Append(ProcessComponent(item, strikethrough, underline));
                }

                return builder.ToString();
            }

            case JsonObject obj:
                return ProcessComponent(obj, strikethrough, underline);

            case JsonValue json:
                if (json.TryGetValue<string>(out var text))
                {
                    return text;
                }

                if (json.TryGetValue<bool>(out var flag))
                {
                    return flag ? "true" : "false";
                }

                if (json.TryGetValue<double>(out var number))
                {
                    return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return string.Empty;

            default:
                return string.Empty;
        }
    }

    private static string ProcessComponent(JsonObject obj, bool strikethrough, bool underline)
    {
        // Inherited from the parent unless this object says otherwise.
        underline = Json.EnsureBoolean(obj, "underlined", underline);
        strikethrough = Json.EnsureBoolean(obj, "strikethrough", strikethrough);

        var result = Json.EnsureString(obj, "text");

        if (underline)
        {
            result = $"<u>{result}</u>";
        }

        if (strikethrough)
        {
            result = $"<s>{result}</s>";
        }

        // The nested components sit INSIDE this object's span, so they are appended before the span is
        // wrapped around the whole thing.
        result += ProcessComponent(obj["extra"] as JsonArray, strikethrough, underline);

        if (BuildStyle(obj) is { Length: > 0 } style)
        {
            result = $"<span {style}>{result}</span>";
        }

        if (obj["clickEvent"] is JsonObject clickEvent)
        {
            var action = Json.EnsureString(clickEvent, "action");
            var target = Json.EnsureString(clickEvent, "value");

            // Only open_url becomes a link. The other actions run commands or copy to the clipboard,
            // and a pack description is not somewhere to honour those.
            if (action == "open_url" && target.Length != 0)
            {
                result = $"<a href=\"{target}\">{result}</a>";
            }
        }

        return result;
    }

    /// <remarks>
    /// Note that "bold": false and "italic": false emit "normal" rather than nothing: a nested
    /// component has to be able to turn OFF what it inherited, and an absent property is different
    /// from an explicit false.
    /// </remarks>
    private static string BuildStyle(JsonObject obj)
    {
        var styles = new List<string>();

        if (Json.EnsureString(obj, "color") is { Length: > 0 } color)
        {
            styles.Add($"color: {color};");
        }

        if (obj.ContainsKey("bold"))
        {
            styles.Add($"font-weight: {(Json.EnsureBoolean(obj, "bold") ? "bold" : "normal")};");
        }

        if (obj.ContainsKey("italic"))
        {
            styles.Add($"font-style: {(Json.EnsureBoolean(obj, "italic") ? "italic" : "normal")};");
        }

        return styles.Count == 0 ? string.Empty : $"style=\"{string.Join(' ', styles)}\"";
    }

    // ================================================================== shared helpers

    internal static byte[] ReadAllBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses a manifest, tolerating a leading UTF-8 byte order mark.
    /// </summary>
    /// <remarks>
    /// BEHAVIOUR GAP: Qt's QJsonDocument::fromJson skips a BOM; System.Text.Json throws on one. Real
    /// packs have them -- the inherited ResourcePackParse fixture "another_test_folder/pack.mcmeta"
    /// starts with EF BB BF, because it was written by an editor that adds one. Without this, every
    /// pack whose author used Notepad would be rejected as corrupt.
    /// </remarks>
    internal static JsonObject ParseManifest(byte[] rawData)
    {
        ReadOnlySpan<byte> span = rawData;

        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return JsonNode.Parse(span.ToArray()) as JsonObject ?? [];
    }

    internal static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();

        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Whether the archive has a directory at its root by that name.</summary>
    /// <remarks>
    /// Checked by PREFIX rather than by looking for a directory entry: plenty of zips carry no explicit
    /// directory entries at all, only the files inside them.
    /// </remarks>
    internal static bool HasDirectory(ZipArchive zip, string name)
        => zip.Entries.Any(e => e.FullName.StartsWith(name + "/", StringComparison.Ordinal));
}

// ====================================================================== data packs

public sealed class DataPack : Resource
{
    public DataPack(string path) : base(path)
    {
    }

    public int PackFormat { get; set; }

    public string Description { get; set; } = string.Empty;

    public override bool Valid => PackFormat != 0;
}

public static class DataPackUtils
{
    public static bool Process(DataPack pack, ProcessingLevel level = ProcessingLevel.Full)
        => pack.Type switch
        {
            ResourceType.Folder => ProcessFolder(pack, level),
            ResourceType.ZipFile => ProcessZip(pack, level),
            _ => false,
        };

    public static bool ProcessFolder(DataPack pack, ProcessingLevel level = ProcessingLevel.Full)
    {
        var mcmeta = FileSystem.PathCombine(pack.Path, "pack.mcmeta");

        if (!File.Exists(mcmeta) || !ProcessMcMeta(pack, ResourcePackUtils.ReadAllBytes(mcmeta)))
        {
            return false;
        }

        // A "data" directory rather than "assets" — that is the whole difference from a resource pack.
        return Directory.Exists(FileSystem.PathCombine(pack.Path, "data"));
    }

    public static bool ProcessZip(DataPack pack, ProcessingLevel level = ProcessingLevel.Full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(pack.Path);

            if (zip.GetEntry("pack.mcmeta") is not { } mcmeta
                || !ProcessMcMeta(pack, ResourcePackUtils.ReadEntry(mcmeta)))
            {
                return false;
            }

            return ResourcePackUtils.HasDirectory(zip, "data");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool ProcessMcMeta(DataPack pack, byte[] rawData)
    {
        try
        {
            var root = ResourcePackUtils.ParseManifest(rawData);
            var packObject = Json.RequireObject(root, "pack");

            pack.PackFormat = Json.EnsureInteger(packObject, "pack_format");
            pack.Description = ResourcePackUtils.ProcessComponent(packObject["description"]);

            return true;
        }
        catch (Exception e) when (e is JsonException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

// ====================================================================== texture packs

/// <summary>
/// The pre-1.6 format, which predates pack.mcmeta entirely.
/// </summary>
/// <remarks>
/// Its manifest is a plain text file whose whole contents are the description — no JSON, no version,
/// no structure. There is nothing to validate beyond its presence.
/// </remarks>
public sealed class TexturePack : Resource
{
    public TexturePack(string path) : base(path)
    {
    }

    public string Description { get; set; } = string.Empty;

    public byte[] Image { get; set; } = [];

    /// <summary>
    /// True once a pack.txt has been read.
    /// </summary>
    /// <remarks>
    /// An EMPTY description is still valid — a pack.txt of zero bytes is a texture pack that declined
    /// to describe itself, not a broken one.
    /// </remarks>
    public override bool Valid => Parsed;

    internal bool Parsed { get; set; }
}

public static class TexturePackUtils
{
    public static bool Process(TexturePack pack, ProcessingLevel level = ProcessingLevel.Full)
        => pack.Type switch
        {
            ResourceType.Folder => ProcessFolder(pack, level),
            ResourceType.ZipFile => ProcessZip(pack, level),
            _ => false,
        };

    public static bool ProcessFolder(TexturePack pack, ProcessingLevel level = ProcessingLevel.Full)
    {
        var manifest = FileSystem.PathCombine(pack.Path, "pack.txt");

        if (!File.Exists(manifest))
        {
            return false;
        }

        ProcessPackTxt(pack, ResourcePackUtils.ReadAllBytes(manifest));

        if (level == ProcessingLevel.BasicInfoOnly)
        {
            return true;
        }

        var image = FileSystem.PathCombine(pack.Path, "pack.png");

        if (File.Exists(image))
        {
            pack.Image = ResourcePackUtils.ReadAllBytes(image);
        }

        return true;
    }

    public static bool ProcessZip(TexturePack pack, ProcessingLevel level = ProcessingLevel.Full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(pack.Path);

            if (zip.GetEntry("pack.txt") is not { } manifest)
            {
                return false;
            }

            ProcessPackTxt(pack, ResourcePackUtils.ReadEntry(manifest));

            if (level == ProcessingLevel.BasicInfoOnly)
            {
                return true;
            }

            if (zip.GetEntry("pack.png") is { } image)
            {
                pack.Image = ResourcePackUtils.ReadEntry(image);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The whole file is the description, newlines and all.</summary>
    public static bool ProcessPackTxt(TexturePack pack, byte[] rawData)
    {
        // Read as UTF-8 with the platform's line endings normalised away: these files were written by
        // hand on every OS there is, and a stray CR would show up in the launcher's UI.
        pack.Description = Encoding.UTF8.GetString(rawData).Replace("\r\n", "\n", StringComparison.Ordinal);
        pack.Parsed = true;

        return true;
    }
}

// ====================================================================== shader packs

public enum ShaderPackFormat
{
    Invalid,
    Valid,
}

/// <summary>
/// An OptiFine or Iris shader pack.
/// </summary>
/// <remarks>
/// There is no manifest at all: a shader pack is a folder called "shaders", and that is the entire
/// specification. Nothing here can report a version or a description because nothing records one.
/// </remarks>
public sealed class ShaderPack : Resource
{
    public ShaderPack(string path) : base(path)
    {
    }

    public ShaderPackFormat PackFormat { get; set; } = ShaderPackFormat.Invalid;

    public override bool Valid => PackFormat != ShaderPackFormat.Invalid;
}

public static class ShaderPackUtils
{
    public static bool Process(ShaderPack pack, ProcessingLevel level = ProcessingLevel.Full)
        => pack.Type switch
        {
            ResourceType.Folder => ProcessFolder(pack, level),
            ResourceType.ZipFile => ProcessZip(pack, level),
            _ => false,
        };

    public static bool ProcessFolder(ShaderPack pack, ProcessingLevel level = ProcessingLevel.Full)
    {
        if (!Directory.Exists(FileSystem.PathCombine(pack.Path, "shaders")))
        {
            return false;
        }

        pack.PackFormat = ShaderPackFormat.Valid;
        return true;
    }

    public static bool ProcessZip(ShaderPack pack, ProcessingLevel level = ProcessingLevel.Full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(pack.Path);

            if (!ResourcePackUtils.HasDirectory(zip, "shaders"))
            {
                return false;
            }

            pack.PackFormat = ShaderPackFormat.Valid;
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

// ====================================================================== world saves

public enum WorldSaveFormat
{
    /// <summary>The archive holds one world folder at its root.</summary>
    Single,

    /// <summary>The archive holds a "saves" folder, which may hold several worlds.</summary>
    Multi,

    Invalid,
}

public sealed class WorldSave : Resource
{
    public WorldSave(string path) : base(path)
    {
    }

    public WorldSaveFormat SaveFormat { get; set; } = WorldSaveFormat.Invalid;

    /// <summary>The name of the folder holding level.dat, which is the world's name on disk.</summary>
    public string SaveDirName { get; set; } = string.Empty;

    public override bool Valid => SaveFormat != WorldSaveFormat.Invalid;
}

public static class WorldSaveUtils
{
    public static bool Process(WorldSave save, ProcessingLevel level = ProcessingLevel.Full)
        => save.Type switch
        {
            ResourceType.Folder => ProcessFolder(save, level),
            ResourceType.ZipFile => ProcessZip(save, level),
            _ => false,
        };

    public static bool ProcessFolder(WorldSave save, ProcessingLevel level = ProcessingLevel.Full)
    {
        var (found, saveDirName, foundSavesDir) = ContainsLevelDat(save.Path);

        if (!found)
        {
            return false;
        }

        save.SaveDirName = saveDirName;
        save.SaveFormat = foundSavesDir ? WorldSaveFormat.Multi : WorldSaveFormat.Single;

        return true;
    }

    /// <summary>
    /// Looks one level down for a folder holding level.dat, descending into "saves" if it finds one.
    /// </summary>
    /// <remarks>
    /// Users export worlds two ways: the folder itself, or the whole "saves" directory. Both arrive in
    /// the same drop target, so both have to be recognised — and which it was decides the format.
    /// Recursion is ONE level: a "saves" folder is descended into once and never again.
    /// </remarks>
    private static (bool Found, string Name, bool Saves) ContainsLevelDat(string directory, bool saves = false)
    {
        if (!Directory.Exists(directory))
        {
            return (false, string.Empty, saves);
        }

        foreach (var entry in Directory.EnumerateDirectories(directory))
        {
            var name = System.IO.Path.GetFileName(entry);

            if (!saves && name == "saves")
            {
                return ContainsLevelDat(entry, saves: true);
            }

            if (File.Exists(FileSystem.PathCombine(entry, "level.dat")))
            {
                return (true, name, saves);
            }
        }

        return (false, string.Empty, saves);
    }

    public static bool ProcessZip(WorldSave save, ProcessingLevel level = ProcessingLevel.Full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(save.Path);

            var (found, saveDirName, foundSavesDir) = ContainsLevelDat(zip);

            if (!found)
            {
                return false;
            }

            save.SaveDirName = saveDirName;
            save.SaveFormat = foundSavesDir ? WorldSaveFormat.Multi : WorldSaveFormat.Single;

            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (bool Found, string Name, bool Saves) ContainsLevelDat(ZipArchive zip)
    {
        var saves = ResourcePackUtils.HasDirectory(zip, "saves");
        var prefix = saves ? "saves/" : string.Empty;

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;

            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = name[prefix.Length..];
            var slash = relative.IndexOf('/', StringComparison.Ordinal);

            // Exactly one level down: "<world>/level.dat", nothing deeper.
            if (slash < 0 || relative[(slash + 1)..] != "level.dat")
            {
                continue;
            }

            return (true, relative[..slash], saves);
        }

        return (false, string.Empty, saves);
    }
}
