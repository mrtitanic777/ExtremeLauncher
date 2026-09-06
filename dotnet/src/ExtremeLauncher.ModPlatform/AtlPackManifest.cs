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
 * Ported from launcher/modplatform/atlauncher/ATLPackManifest.{h,cpp}.
 *
 * THE ONLY PACK FORMAT WITH AN INSTALLER IN IT. The others list files; an ATLauncher pack describes a
 * small install program -- mods that are optional, mods that are recommended, mods that depend on
 * other mods, mods grouped so only one of a group may be chosen, mods hidden from the user entirely,
 * and mods that are archives to be unpacked into a named folder rather than dropped in as they are.
 * The launcher has to present that as a choice and then honour it.
 *
 * MOD TYPES ARE A DELIVERY INSTRUCTION, NOT A CATEGORY. "mods" means the mods folder, "jar" means
 * patch it into the game jar, "extract" means unpack it, "decomp" means unpack it and take one file
 * out. Nineteen of them, documented at wiki.atlauncher.com/mod_types, and each is a different thing
 * to do with the download.
 *
 * ALL OF THIS IS PARSING ONLY. The install program these fields describe belongs with the instance
 * layer; what is here is the reading of it, which is the half that can be tested without one.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>Where a file comes from, which decides whether the launcher may fetch it at all.</summary>
public enum AtlDownloadType
{
    /// <summary>From ATLauncher's own servers.</summary>
    Server,

    /// <summary>The user must fetch it in a browser -- the same problem as a blocked CurseForge file.</summary>
    Browser,

    /// <summary>Straight from a URL.</summary>
    Direct,

    Unknown,
}

/// <summary>What to DO with a downloaded file. See wiki.atlauncher.com/mod_types.</summary>
public enum AtlModType
{
    Root,
    Forge,
    Jar,
    Mods,
    Flan,
    Dependency,
    Ic2Lib,
    DenLib,
    Coremods,
    Mcpc,
    Plugins,
    Extract,
    Decomp,
    TexturePack,
    ResourcePack,
    ShaderPack,
    TexturePackExtract,
    ResourcePackExtract,
    Millenaire,
    Unknown,
}

/// <summary>The loader a pack version asks for.</summary>
public sealed class AtlVersionLoader
{
    public string Type { get; set; } = string.Empty;

    public bool Latest { get; set; }

    public bool Recommended { get; set; }

    /// <summary>Whether the user is offered a choice of loader version.</summary>
    public bool Choose { get; set; }

    public string Version { get; set; } = string.Empty;
}

/// <summary>A library the pack needs, fetched outside the usual metadata.</summary>
public sealed class AtlVersionLibrary
{
    public string Url { get; set; } = string.Empty;

    public string File { get; set; } = string.Empty;

    public string Md5 { get; set; } = string.Empty;

    public AtlDownloadType Download { get; set; } = AtlDownloadType.Unknown;

    public string DownloadRaw { get; set; } = string.Empty;

    /// <summary>Set when the library is only for a server install.</summary>
    public string Server { get; set; } = string.Empty;
}

/// <summary>The pack's configuration archive.</summary>
public sealed class AtlVersionConfigs
{
    public int FileSize { get; set; }

    public string Sha1 { get; set; } = string.Empty;
}

/// <summary>One mod of an ATLauncher pack, with everything its installer needs to know.</summary>
public sealed class AtlVersionMod
{
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string File { get; set; } = string.Empty;

    /// <summary>Optional: an md5, and it is all ATLauncher offers.</summary>
    public string Md5 { get; set; } = string.Empty;

    public AtlDownloadType Download { get; set; } = AtlDownloadType.Unknown;

    public string DownloadRaw { get; set; } = string.Empty;

    public AtlModType Type { get; set; } = AtlModType.Unknown;

    public string TypeRaw { get; set; } = string.Empty;

    /// <summary>For an archive: what kind of thing its contents are.</summary>
    public AtlModType ExtractTo { get; set; } = AtlModType.Unknown;

    public string ExtractToRaw { get; set; } = string.Empty;

    /// <summary>Where inside the instance to unpack it.</summary>
    public string ExtractFolder { get; set; } = string.Empty;

    public AtlModType DecompType { get; set; } = AtlModType.Unknown;

    public string DecompTypeRaw { get; set; } = string.Empty;

    /// <summary>The one file to take out of the archive.</summary>
    public string DecompFile { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Whether the user may decline it.</summary>
    public bool Optional { get; set; }

    /// <summary>Whether the pack suggests taking it. Only meaningful when optional.</summary>
    public bool Recommended { get; set; }

    /// <summary>Whether it starts ticked.</summary>
    public bool Selected { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Whether it is a supporting library rather than a mod the user would recognise.</summary>
    public bool Library { get; set; }

    /// <summary>Mods sharing a group are alternatives: choosing one deselects the others.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Names of mods this one needs. Selecting it must select those too.</summary>
    public List<string> Depends { get; } = [];

    public string Colour { get; set; } = string.Empty;

    /// <summary>A warning to show before the user selects it.</summary>
    public string Warning { get; set; } = string.Empty;

    /// <summary>Whether it is a client-side mod.</summary>
    public bool Client { get; set; }

    /// <summary>
    /// Whether the user should never see it.
    /// </summary>
    /// <remarks>
    /// Computed, not read: hidden OR library. A library is not marked hidden but has no business in a
    /// mod chooser either -- the user cannot make a meaningful decision about IC2's shim jar.
    /// </remarks>
    public bool EffectivelyHidden => Hidden || Library;
}

public static class AtlPackManifest
{
    /// <summary>The placeholder ATLauncher uses for a path separator inside a JSON string.</summary>
    /// <remarks>
    /// <c>%s%</c>, replaced with "/". It exists because the field is written by hand into JSON where a
    /// backslash would have to be escaped, and packs predate anyone being careful about that.
    /// </remarks>
    public const string PathSeparatorPlaceholder = "%s%";

    public static AtlDownloadType ParseDownloadType(string raw)
        => raw switch
        {
            "server" => AtlDownloadType.Server,
            "browser" => AtlDownloadType.Browser,
            "direct" => AtlDownloadType.Direct,
            _ => AtlDownloadType.Unknown,
        };

    /// <summary>Parses a mod type, or Unknown.</summary>
    /// <remarks>
    /// NOTE "depandency". ATLauncher shipped the misspelling, packs were published with it, and it now
    /// has to be accepted forever -- upstream takes both spellings and so does this. Removing it would
    /// break real packs that are still installable today.
    /// </remarks>
    public static AtlModType ParseModType(string raw)
        => raw switch
        {
            "root" => AtlModType.Root,
            "forge" => AtlModType.Forge,
            "jar" => AtlModType.Jar,
            "mods" => AtlModType.Mods,
            "flan" => AtlModType.Flan,
            "dependency" or "depandency" => AtlModType.Dependency,
            "ic2lib" => AtlModType.Ic2Lib,
            "denlib" => AtlModType.DenLib,
            "coremods" => AtlModType.Coremods,
            "mcpc" => AtlModType.Mcpc,
            "plugins" => AtlModType.Plugins,
            "extract" => AtlModType.Extract,
            "decomp" => AtlModType.Decomp,
            "texturepack" => AtlModType.TexturePack,
            "resourcepack" => AtlModType.ResourcePack,
            "shaderpack" => AtlModType.ShaderPack,
            "texturepackextract" => AtlModType.TexturePackExtract,
            "resourcepackextract" => AtlModType.ResourcePackExtract,
            "millenaire" => AtlModType.Millenaire,
            _ => AtlModType.Unknown,
        };

    /// <summary>Reads the loader block.</summary>
    /// <remarks>
    /// EACH LOADER HIDES ITS VERSION UNDER A DIFFERENT KEY -- Forge under "version", Fabric under
    /// "loader" -- so the type has to be read first and the version looked up by it. A loader this
    /// launcher does not know leaves the version empty rather than failing: the pack may still be
    /// installable as vanilla.
    /// </remarks>
    public static AtlVersionLoader LoadVersionLoader(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var loader = new AtlVersionLoader
        {
            Type = Json.RequireString(obj, "type"),
            Choose = Json.EnsureBoolean(obj["choose"], false),
        };

        var metadata = Json.RequireObject(obj, "metadata");

        loader.Latest = Json.EnsureBoolean(metadata["latest"], false);
        loader.Recommended = Json.EnsureBoolean(metadata["recommended"], false);

        loader.Version = loader.Type switch
        {
            "forge" => Json.EnsureString(metadata, "version"),
            "fabric" => Json.EnsureString(metadata, "loader"),
            _ => string.Empty,
        };

        return loader;
    }

    public static AtlVersionLibrary LoadVersionLibrary(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var library = new AtlVersionLibrary
        {
            Url = Json.RequireString(obj, "url"),
            File = Json.RequireString(obj, "file"),
            Md5 = Json.RequireString(obj, "md5"),
            DownloadRaw = Json.RequireString(obj, "download"),
            Server = Json.EnsureString(obj, "server"),
        };

        library.Download = ParseDownloadType(library.DownloadRaw);

        return library;
    }

    public static AtlVersionConfigs LoadVersionConfigs(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        return new AtlVersionConfigs
        {
            FileSize = Json.RequireInteger(obj, "filesize"),
            Sha1 = Json.RequireString(obj, "sha1"),
        };
    }

    /// <summary>Reads one mod entry.</summary>
    public static AtlVersionMod LoadVersionMod(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var mod = new AtlVersionMod
        {
            Name = Json.RequireString(obj, "name"),
            Version = Json.RequireString(obj, "version"),
            Url = Json.RequireString(obj, "url"),
            File = Json.RequireString(obj, "file"),

            // Optional, and an md5 is all ATLauncher offers even when present.
            Md5 = Json.EnsureString(obj, "md5"),

            DownloadRaw = Json.RequireString(obj, "download"),
            TypeRaw = Json.RequireString(obj, "type"),
        };

        mod.Download = ParseDownloadType(mod.DownloadRaw);
        mod.Type = ParseModType(mod.TypeRaw);

        /*
         * A NAME-BASED CORRECTION, and upstream explains why it has to exist: Forge detection relies
         * on the mod's type being "forge", but there is little practical difference between "jar" and
         * "forge" and some packs use "jar" for Forge itself. Without this the pack installs with no
         * Forge component and every mod fails to load. Matching on the exact name is crude, and it is
         * the only signal available.
         */
        if (mod.Name == "Minecraft Forge" && mod.Type == AtlModType.Jar)
        {
            mod.TypeRaw = "forge";
            mod.Type = AtlModType.Forge;
        }

        if (obj.ContainsKey("extractTo"))
        {
            mod.ExtractToRaw = Json.RequireString(obj, "extractTo");
            mod.ExtractTo = ParseModType(mod.ExtractToRaw);

            mod.ExtractFolder = Json.EnsureString(obj, "extractFolder")
                .Replace(PathSeparatorPlaceholder, "/", StringComparison.Ordinal);
        }

        if (obj.ContainsKey("decompFile"))
        {
            mod.DecompTypeRaw = Json.RequireString(obj, "decompType");
            mod.DecompType = ParseModType(mod.DecompTypeRaw);
            mod.DecompFile = Json.RequireString(obj, "decompFile");
        }

        mod.Description = Json.EnsureString(obj, "description");
        mod.Optional = Json.EnsureBoolean(obj["optional"], false);
        mod.Recommended = Json.EnsureBoolean(obj["recommended"], false);
        mod.Selected = Json.EnsureBoolean(obj["selected"], false);
        mod.Hidden = Json.EnsureBoolean(obj["hidden"], false);
        mod.Library = Json.EnsureBoolean(obj["library"], false);
        mod.Group = Json.EnsureString(obj, "group");

        if (obj.ContainsKey("depends"))
        {
            foreach (var depends in Json.RequireArray(obj, "depends"))
            {
                mod.Depends.Add(Json.RequireString(depends));
            }
        }

        mod.Colour = Json.EnsureString(obj, "colour");
        mod.Warning = Json.EnsureString(obj, "warning");
        mod.Client = Json.EnsureBoolean(obj["client"], false);

        return mod;
    }

    /// <summary>
    /// The mods a fresh install starts with selected.
    /// </summary>
    /// <remarks>
    /// A non-optional mod is always in. An optional one starts in if the pack marked it selected. The
    /// two flags are separate because "the user may decline this" and "it starts ticked" are different
    /// statements, and a pack can make either without the other.
    /// </remarks>
    public static List<AtlVersionMod> GetDefaultSelection(IEnumerable<AtlVersionMod> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        return [.. mods.Where(m => !m.Optional || m.Selected)];
    }

    /// <summary>
    /// Adds everything the chosen mods depend on, following dependencies transitively.
    /// </summary>
    /// <remarks>
    /// Dependencies are named by mod NAME, which is the only identifier an ATLauncher pack gives its
    /// mods. A name that matches nothing is ignored rather than fatal -- packs list dependencies on
    /// mods they no longer ship.
    /// </remarks>
    public static List<AtlVersionMod> ResolveDependencies(
        IReadOnlyList<AtlVersionMod> allMods,
        IEnumerable<AtlVersionMod> selected)
    {
        ArgumentNullException.ThrowIfNull(allMods);
        ArgumentNullException.ThrowIfNull(selected);

        var byName = new Dictionary<string, AtlVersionMod>(StringComparer.Ordinal);

        foreach (var mod in allMods)
        {
            // First wins, so a pack with two mods of one name resolves to the earlier entry.
            byName.TryAdd(mod.Name, mod);
        }

        var result = new List<AtlVersionMod>();
        var seen = new HashSet<AtlVersionMod>();
        var queue = new Queue<AtlVersionMod>(selected);

        while (queue.Count != 0)
        {
            var mod = queue.Dequeue();

            // Also what stops a dependency cycle, which nothing in the format forbids.
            if (!seen.Add(mod))
            {
                continue;
            }

            result.Add(mod);

            foreach (var name in mod.Depends)
            {
                if (byName.TryGetValue(name, out var dependency))
                {
                    queue.Enqueue(dependency);
                }
            }
        }

        // Back into the pack's own order, so the install list does not depend on traversal order.
        return [.. allMods.Where(seen.Contains)];
    }
}
