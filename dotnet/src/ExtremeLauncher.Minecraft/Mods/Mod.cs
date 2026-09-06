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
 * Ported from launcher/minecraft/mod/{Mod,ModDetails}.{h,cpp} and
 * launcher/minecraft/mod/tasks/LocalModParseTask.cpp.
 *
 * SIX METADATA FORMATS, because six mod loaders each invented their own and none of them agreed:
 *
 *   mcmod.info              Forge, pre-1.13. JSON, sometimes a bare array, sometimes wrapped.
 *   META-INF/mods.toml      Forge 1.13+ and NeoForge. TOML, with the mod inside a [[mods]] array.
 *   fabric.mod.json         Fabric. JSON, versioned by "schemaVersion".
 *   quilt.mod.json          Quilt. JSON, everything under "quilt_loader".
 *   litemod.json            LiteLoader. JSON, flat.
 *   forgeversion.properties Forge itself, as a mod. INI, and the version is four separate keys.
 *
 * A jar is checked against all of them in a fixed order, because a mod may ship several — a Fabric mod
 * with a Forge shim carries both — and the first one found wins.
 *
 * NOT PORTED: the NilLoader format, which needs the QDCSS parser (a CSS-like dialect used by nothing
 * else in the codebase), and the Mod side of ResourceFolderModel.
 */

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.Minecraft.Mods;

/// <summary>A licence a mod declares. Any field may be empty; mods fill in what they feel like.</summary>
public sealed record ModLicense(string Name = "", string Id = "", string Url = "", string Description = "");

/// <summary>What a mod says about itself, once one of the six formats has been read.</summary>
public sealed class ModDetails
{
    public string ModId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>The Minecraft version, when the format records one. Only LiteLoader does.</summary>
    public string McVersion { get; set; } = string.Empty;

    public string HomeUrl { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> Authors { get; } = [];

    public string IssueTracker { get; set; } = string.Empty;

    public List<ModLicense> Licenses { get; } = [];

    /// <summary>The icon's path inside the jar, not its contents.</summary>
    public string IconFile { get; set; } = string.Empty;
}

public sealed class Mod : Resource
{
    public Mod(string path) : base(path)
    {
    }

    public ModDetails Details { get; set; } = new();

    /// <summary>The mod's own name, falling back to the filename when it did not say.</summary>
    public string DisplayName => Details.Name.Length != 0 ? Details.Name : Name;

    public override bool Valid => Details.ModId.Length != 0 || Details.Name.Length != 0;
}

public static class ModUtils
{
    /// <summary>
    /// The order a jar's metadata files are looked for in.
    /// </summary>
    /// <remarks>
    /// A mod may ship SEVERAL — a Fabric mod with a Forge shim carries both — and the first found
    /// wins, so this order decides which loader's view of a multi-loader mod the launcher shows.
    /// It is upstream's, and the newest formats come first.
    /// </remarks>
    private static readonly string[] ZipMetadataOrder =
    [
        "META-INF/mods.toml",
        "META-INF/neoforge.mods.toml",
        "mcmod.info",
        "quilt.mod.json",
        "fabric.mod.json",
        "forgeversion.properties",
    ];

    public static bool Process(Mod mod, ProcessingLevel level = ProcessingLevel.Full)
        => mod.Type switch
        {
            ResourceType.Folder => ProcessFolder(mod, level),
            ResourceType.ZipFile => ProcessZip(mod, level),
            ResourceType.LiteMod => ProcessLitemod(mod, level),
            _ => false,
        };

    public static bool ProcessZip(Mod mod, ProcessingLevel level = ProcessingLevel.Full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(mod.Path);

            foreach (var name in ZipMetadataOrder)
            {
                if (zip.GetEntry(name) is not { } entry)
                {
                    continue;
                }

                var contents = ResourcePackUtils.ReadEntry(entry);

                mod.Details = name switch
                {
                    "META-INF/mods.toml" or "META-INF/neoforge.mods.toml" => ReadMcModToml(contents),
                    "mcmod.info" => ReadMcModInfo(contents),
                    "quilt.mod.json" => ReadQuiltModInfo(contents),
                    "fabric.mod.json" => ReadFabricModInfo(contents),
                    "forgeversion.properties" => ReadForgeInfo(contents),
                    _ => new ModDetails(),
                };

                return true;
            }

            return false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads an unpacked mod folder.
    /// </summary>
    /// <remarks>
    /// Only mcmod.info is looked for here. That is upstream's choice and it is narrow, but an unpacked
    /// mod folder is a development arrangement rather than something users have.
    /// </remarks>
    public static bool ProcessFolder(Mod mod, ProcessingLevel level = ProcessingLevel.Full)
    {
        var info = FileSystem.PathCombine(mod.Path, "mcmod.info");

        if (!File.Exists(info))
        {
            return false;
        }

        mod.Details = ReadMcModInfo(ResourcePackUtils.ReadAllBytes(info));
        return true;
    }

    public static bool ProcessLitemod(Mod mod, ProcessingLevel level = ProcessingLevel.Full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(mod.Path);

            if (zip.GetEntry("litemod.json") is not { } entry)
            {
                return false;
            }

            mod.Details = ReadLiteModInfo(ResourcePackUtils.ReadEntry(entry));
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ================================================================== mcmod.info (Forge, pre-1.13)

    /// <summary>
    /// Reads Forge's pre-1.13 metadata.
    /// </summary>
    /// <remarks>
    /// The format changed shape over its life and all of it is still in the wild: the oldest files are
    /// a BARE ARRAY, later ones wrap it in an object under "modlist" or "modList", and the version key
    /// is "modinfoversion" or "modListVersion" depending on the year. Some mods write the version as a
    /// STRING because a tutorial did. All of it is accepted.
    /// </remarks>
    public static ModDetails ReadMcModInfo(byte[] contents)
    {
        JsonNode? document;

        try
        {
            document = ResourcePackUtils.ParseManifest(contents) is { } obj && obj.Count != 0
                ? obj
                : JsonNode.Parse(StripBom(contents));
        }
        catch (System.Text.Json.JsonException)
        {
            return new ModDetails();
        }

        return document switch
        {
            JsonArray array => FromArray(array),
            JsonObject root => FromArray(
                (root["modlist"] ?? root["modList"]) as JsonArray ?? []),
            _ => new ModDetails(),
        };

        static ModDetails FromArray(JsonArray array)
        {
            if (array.Count == 0 || array[0] is not JsonObject first)
            {
                return new ModDetails();
            }

            var details = new ModDetails
            {
                ModId = Json.EnsureString(first, "modid"),
                Version = Json.EnsureString(first, "version"),
                Description = Json.EnsureString(first, "description"),
                HomeUrl = FixUpUrl(Json.EnsureString(first, "url").Trim()),
                IconFile = Json.EnsureString(first, "logoFile"),
            };

            var name = Json.EnsureString(first, "name");

            // A great many mods ship the example mod's metadata unchanged, and showing a folder full
            // of "Example Mod" helps nobody; the filename is more informative.
            if (name != "Example Mod")
            {
                details.Name = name;
            }

            // "authorList" is the documented key; "authors" appears in the wild anyway.
            var authors = first["authorList"] as JsonArray;

            if (authors is null || authors.Count == 0)
            {
                authors = first["authors"] as JsonArray;
            }

            foreach (var author in authors ?? [])
            {
                if (author is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    details.Authors.Add(text);
                }
            }

            return details;
        }
    }

    // ================================================================== mods.toml (Forge 1.13+)

    /// <summary>
    /// Reads Forge's and NeoForge's TOML metadata.
    /// </summary>
    /// <remarks>
    /// The mod's own fields live in the first element of a <c>[[mods]]</c> array, but four of them —
    /// authors, displayURL, issueTrackerURL and license — may sit EITHER at the file's top level or
    /// inside that element, because the template moved them and both forms shipped. The top level is
    /// checked first, matching upstream.
    /// </remarks>
    public static ModDetails ReadMcModToml(byte[] contents)
    {
        var details = new ModDetails();

        Tomlyn.Model.TomlTable root;

        try
        {
            if (!Tomlyn.Toml.TryToModel<Tomlyn.Model.TomlTable>(
                    Encoding.UTF8.GetString(StripBom(contents)),
                    out var parsed,
                    out _)
                || parsed is null)
            {
                return details;
            }

            root = parsed;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return details;
        }

        if (root.TryGetValue("mods", out var modsValue)
            && modsValue is Tomlyn.Model.TomlTableArray { Count: > 0 } mods
            && mods[0] is { } modsTable)
        {
            details.ModId = TomlString(modsTable, "modId");
            details.Version = TomlString(modsTable, "version");
            details.Name = TomlString(modsTable, "displayName");
            details.Description = TomlString(modsTable, "description");

            // Top level first, then the mods table -- both spellings are in the wild.
            var authors = Fallback(root, modsTable, "authors");

            if (authors.Length != 0)
            {
                details.Authors.Add(authors);
            }

            details.HomeUrl = FixUpUrl(Fallback(root, modsTable, "displayURL"));
            details.IssueTracker = Fallback(root, modsTable, "issueTrackerURL");

            if (Fallback(root, modsTable, "license") is { Length: > 0 } license)
            {
                details.Licenses.Add(new ModLicense(license));
            }

            if (TomlString(modsTable, "logoFile") is { Length: > 0 } logo)
            {
                details.IconFile = logo;
            }
            else if (TomlString(root, "logoFile") is { Length: > 0 } topLevelLogo)
            {
                details.IconFile = topLevelLogo;
            }
        }

        return details;

        static string Fallback(Tomlyn.Model.TomlTable root, Tomlyn.Model.TomlTable mods, string key)
        {
            var top = TomlString(root, key);
            return top.Length != 0 ? top : TomlString(mods, key);
        }
    }

    private static string TomlString(Tomlyn.Model.TomlTable table, string key)
        => table.TryGetValue(key, out var value) && value is string text ? text : string.Empty;

    // ================================================================== fabric.mod.json

    /// <remarks>
    /// Everything beyond id, version, name and description is gated on <c>schemaVersion</c> being at
    /// least 1. Version 0 files predate those fields entirely, so reading them would be inventing data.
    /// </remarks>
    public static ModDetails ReadFabricModInfo(byte[] contents)
    {
        var details = new ModDetails();

        if (Parse(contents) is not { } root)
        {
            return details;
        }

        details.ModId = Json.EnsureString(root, "id");
        details.Version = Json.EnsureString(root, "version");
        details.Description = Json.EnsureString(root, "description");

        // Falls back to the id rather than the filename: a Fabric mod always has an id.
        details.Name = root.ContainsKey("name") ? Json.EnsureString(root, "name") : details.ModId;

        if (Json.EnsureInteger(root, "schemaVersion") < 1)
        {
            return details;
        }

        foreach (var author in root["authors"] as JsonArray ?? [])
        {
            // An author is a bare string or an object with a name; both forms are documented.
            details.Authors.Add(author is JsonObject obj
                ? Json.EnsureString(obj, "name")
                : ResourcePackUtils.ProcessComponent(author));
        }

        if (root["contact"] is JsonObject contact)
        {
            details.HomeUrl = Json.EnsureString(contact, "homepage");
            details.IssueTracker = Json.EnsureString(contact, "issues");
        }

        ReadLicenses(root["license"], details.Licenses);
        details.IconFile = ReadIcon(root["icon"]);

        return details;
    }

    // ================================================================== quilt.mod.json

    /// <exception cref="JsonException">The document is not an object, or a required field is missing.</exception>
    public static ModDetails ReadQuiltModInfo(byte[] contents)
    {
        var details = new ModDetails();

        if (Parse(contents) is not { } root || Json.EnsureInteger(root, "schema_version") != 1)
        {
            // Only schema 1 is specified. A future version is left unread rather than guessed at.
            return details;
        }

        var loader = Json.RequireObject(root, "quilt_loader");

        details.ModId = Json.RequireString(loader, "id");
        details.Version = Json.RequireString(loader, "version");

        var metadata = Json.EnsureObject(loader, "metadata");

        details.Name = Json.EnsureString(metadata, "name", details.ModId);
        details.Description = Json.EnsureString(metadata, "description");

        // Contributors are keyed by name with their role as the value; the role is not shown anywhere,
        // so only the keys are kept.
        foreach (var (name, _) in Json.EnsureObject(metadata, "contributors"))
        {
            details.Authors.Add(name);
        }

        var contact = Json.EnsureObject(metadata, "contact");

        details.HomeUrl = Json.EnsureString(contact, "homepage");
        details.IssueTracker = Json.EnsureString(contact, "issues");

        ReadLicenses(metadata["license"], details.Licenses);
        details.IconFile = ReadIcon(metadata["icon"]);

        return details;
    }

    // ================================================================== forgeversion.properties

    /// <summary>
    /// Reads Forge's own version, which it ships as a mod.
    /// </summary>
    /// <remarks>
    /// The identity is HARDCODED because the file carries none — it holds four numbers and nothing
    /// else. An unreadable file still yields the name and id, so Forge shows up in the mod list even
    /// when its version cannot be determined.
    /// </remarks>
    public static ModDetails ReadForgeInfo(byte[] contents)
    {
        var details = new ModDetails
        {
            Name = "Minecraft Forge",
            ModId = "Forge",
            HomeUrl = "http://www.minecraftforge.net/forum/",
        };

        var ini = new IniFile();

        if (!ini.LoadFromBytes(contents))
        {
            return details;
        }

        var major = ini.Get("forge.major.number", "0")?.ToString() ?? "0";
        var minor = ini.Get("forge.minor.number", "0")?.ToString() ?? "0";
        var revision = ini.Get("forge.revision.number", "0")?.ToString() ?? "0";
        var build = ini.Get("forge.build.number", "0")?.ToString() ?? "0";

        details.Version = $"{major}.{minor}.{revision}.{build}";

        return details;
    }

    // ================================================================== litemod.json

    /// <remarks>
    /// The only format that records a Minecraft version, and the only one where the id and the name
    /// are the same field — LiteLoader never had a separate identifier.
    /// </remarks>
    public static ModDetails ReadLiteModInfo(byte[] contents)
    {
        var details = new ModDetails();

        if (Parse(contents) is not { } root)
        {
            return details;
        }

        if (root.ContainsKey("name"))
        {
            details.ModId = details.Name = Json.EnsureString(root, "name");
        }

        // "revision" is the older spelling of the same thing.
        details.Version = root.ContainsKey("version")
            ? Json.EnsureString(root, "version")
            : Json.EnsureString(root, "revision");

        details.McVersion = Json.EnsureString(root, "mcversion");
        details.Description = Json.EnsureString(root, "description");
        details.HomeUrl = Json.EnsureString(root, "url");

        if (Json.EnsureString(root, "author") is { Length: > 0 } author)
        {
            details.Authors.Add(author);
        }

        return details;
    }

    // ================================================================== shared helpers

    private static JsonObject? Parse(byte[] contents)
    {
        try
        {
            return JsonNode.Parse(StripBom(contents)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <remarks>See ResourcePackUtils.ParseManifest: mod metadata is written by hand too.</remarks>
    private static byte[] StripBom(byte[] contents)
        => contents.Length >= 3 && contents[0] == 0xEF && contents[1] == 0xBB && contents[2] == 0xBF
            ? contents[3..]
            : contents;

    /// <summary>
    /// Reads a licence declaration, which may be a string, an object, or an array of either.
    /// </summary>
    /// <remarks>Fabric and Quilt specify the same shape here, so both use this.</remarks>
    private static void ReadLicenses(JsonNode? node, List<ModLicense> into)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    ReadLicenses(item, into);
                }

                break;

            case JsonObject obj:
                into.Add(new ModLicense(
                    Json.EnsureString(obj, "name"),
                    Json.EnsureString(obj, "id"),
                    Json.EnsureString(obj, "url"),
                    Json.EnsureString(obj, "description")));

                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                into.Add(new ModLicense(text));
                break;
        }
    }

    /// <summary>
    /// Picks an icon path from a string, or from a map of sizes to paths.
    /// </summary>
    /// <remarks>
    /// THE LARGEST SIZE WINS, so the launcher has something to downscale rather than something to
    /// stretch. Keys look like "128x128"; when none of them parse as a number the first entry is taken,
    /// because an unparseable key is still a valid path to an icon.
    /// </remarks>
    private static string ReadIcon(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;

            case JsonObject obj:
            {
                var largest = 0;
                var chosen = string.Empty;

                foreach (var (key, entry) in obj)
                {
                    var text = entry is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

                    if (chosen.Length == 0)
                    {
                        chosen = text;
                    }

                    if (int.TryParse(key.Split('x')[0], out var size) && size > largest)
                    {
                        largest = size;
                        chosen = text;
                    }
                }

                return chosen;
            }

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Adds a scheme to a bare host, so a mod's URL is clickable.
    /// </summary>
    /// <remarks>
    /// Plain http, not https: this rewrites URLs from mods written a decade ago, and upgrading one
    /// that has no TLS would break the link rather than fix it.
    /// </remarks>
    private static string FixUpUrl(string url)
    {
        if (url.Length == 0)
        {
            return url;
        }

        return url.StartsWith("http://", StringComparison.Ordinal)
               || url.StartsWith("https://", StringComparison.Ordinal)
               || url.StartsWith("ftp://", StringComparison.Ordinal)
            ? url
            : "http://" + url;
    }
}
