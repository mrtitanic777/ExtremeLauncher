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
 * Ported from the generateIndex halves of ModrinthPackExportTask.cpp and FlamePackExportTask.cpp.
 *
 * THE OTHER DIRECTION: an instance back out to a shareable pack. Everything else in this wave reads
 * someone else's pack; this writes one, and the same asymmetries show up mirrored.
 *
 * A .mrpack CARRIES EVERYTHING NEEDED TO INSTALL IT -- URL, sha1, sha512 and size per file. A
 * CurseForge manifest carries only ids, so the pack it produces is smaller and useless without the
 * API. That is the same difference the importers run into, seen from the writing end.
 *
 * ROUND-TRIPPING IS THE TEST THAT MATTERS. These generators are checked by feeding their output back
 * through the parsers in this wave: a field spelled wrong here is a pack that other launchers reject,
 * and no amount of asserting the JSON I meant to write would catch a name only the reader knows.
 */

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace ExtremeLauncher.ModPlatform;

/// <summary>One file of an instance, as the exporters need it.</summary>
/// <param name="Path">Where it sits, relative to the game directory.</param>
/// <param name="Enabled">
/// Whether the mod is turned on. A disabled mod is exported as optional rather than dropped, so the
/// pack still records that the author had it.
/// </param>
public sealed record ExportFile(
    string Path,
    string Url,
    string Sha1,
    string Sha512,
    long Size,
    PackwizSide Side = PackwizSide.UniversalSide,
    bool Enabled = true);

/// <summary>One file of an instance for a CurseForge export, which needs only its ids.</summary>
public sealed record FlameExportFile(
    int ProjectId,
    int FileId,
    bool Enabled = true,
    string Name = "",
    string Authors = "",
    bool IsMod = true);

public static class PackExport
{
    /// <summary>The suffix a disabled mod's file carries.</summary>
    public const string DisabledSuffix = ".disabled";

    // ================================================================== Modrinth

    /// <summary>Builds a modrinth.index.json.</summary>
    /// <param name="optionalFiles">
    /// Whether disabled mods become optional entries. With this off they are exported as ordinary
    /// required files, suffix and all.
    /// </param>
    public static JsonObject CreateModrinthIndex(
        string name,
        string versionId,
        string summary,
        IReadOnlyList<PackComponent> components,
        IEnumerable<ExportFile> files,
        bool optionalFiles = true)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(files);

        var index = new JsonObject
        {
            ["formatVersion"] = ModrinthPack.SupportedFormatVersion,
            ["game"] = "minecraft",
            ["name"] = name,
            ["versionId"] = versionId,
        };

        // Omitted rather than written empty: the field is optional and an empty summary says nothing.
        if (summary.Length != 0)
        {
            index["summary"] = summary;
        }

        var dependencies = new JsonObject();

        foreach (var component in components)
        {
            if (ModrinthDependencyKey(component.Uid) is { } key)
            {
                dependencies[key] = component.Version;
            }
        }

        index["dependencies"] = dependencies;

        var filesOut = new JsonArray();

        foreach (var file in files)
        {
            filesOut.Add(CreateModrinthFile(file, optionalFiles));
        }

        index["files"] = filesOut;

        return index;
    }

    private static JsonObject CreateModrinthFile(ExportFile file, bool optionalFiles)
    {
        var path = file.Path;
        var env = new JsonObject();

        /*
         * A DISABLED MOD IS EXPORTED AS OPTIONAL, not dropped, and its path loses the .disabled
         * suffix. Both halves matter: the pack still records that the author shipped it, and an
         * importer that installs it writes a filename the launcher will recognise.
         */
        if (optionalFiles && (!file.Enabled || path.EndsWith(DisabledSuffix, StringComparison.Ordinal)))
        {
            if (path.EndsWith(DisabledSuffix, StringComparison.Ordinal))
            {
                path = path[..^DisabledSuffix.Length];
            }

            env["client"] = "optional";
            env["server"] = "optional";
        }
        else
        {
            env["client"] = "required";
            env["server"] = "required";
        }

        /*
         * THE SIDE HANDLING IS ASYMMETRIC ON PURPOSE, and upstream's comment says why: "a server side
         * mod does not imply that the mod does not work on the client". So a client-only mod is marked
         * server-unsupported, but a server-only mod is left installable on the client -- because a
         * .mrpack entry marked server-only is SKIPPED by client importers, and wrongly marking one
         * would silently drop a mod the pack needs.
         */
        if (file.Side == PackwizSide.ClientSide)
        {
            env["server"] = "unsupported";
        }

        return new JsonObject
        {
            ["env"] = env,
            ["path"] = path,

            // An array, because the format allows mirrors -- an export knows only the one.
            ["downloads"] = new JsonArray { file.Url },

            ["hashes"] = new JsonObject
            {
                ["sha1"] = file.Sha1,
                ["sha512"] = file.Sha512,
            },

            ["fileSize"] = file.Size,
        };
    }

    /// <summary>The dependency key a component uid maps to, or null if the format has no name for it.</summary>
    /// <remarks>The exact inverse of <see cref="PackComponents.FromModrinth"/>.</remarks>
    public static string? ModrinthDependencyKey(string uid)
        => uid switch
        {
            PackComponents.MinecraftUid => "minecraft",
            PackComponents.QuiltUid => "quilt-loader",
            PackComponents.FabricUid => "fabric-loader",
            PackComponents.ForgeUid => "forge",
            PackComponents.NeoForgeUid => "neoforge",
            _ => null,
        };

    // ================================================================== CurseForge

    /// <summary>Builds a manifest.json.</summary>
    public static JsonObject CreateFlameManifest(
        string name,
        string version,
        string author,
        IReadOnlyList<PackComponent> components,
        IEnumerable<FlameExportFile> files,
        bool optionalFiles = true)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(files);

        var manifest = new JsonObject
        {
            ["manifestType"] = FlamePack.SupportedManifestType,
            ["manifestVersion"] = FlamePack.SupportedManifestVersion,
            ["name"] = name,
            ["version"] = version,
            ["author"] = author,

            // Always the default name on export; the format allows others but nothing gains by it.
            ["overrides"] = FlamePackManifest.DefaultOverrides,
        };

        var minecraftVersion = components
            .FirstOrDefault(c => c.Uid == PackComponents.MinecraftUid)
            .Version ?? string.Empty;

        var minecraft = new JsonObject { ["version"] = minecraftVersion };

        var loaders = new JsonArray();

        if (CreateFlameLoaderId(components) is { } id)
        {
            loaders.Add(new JsonObject { ["id"] = id, ["primary"] = true });
        }

        minecraft["modLoaders"] = loaders;
        manifest["minecraft"] = minecraft;

        var filesOut = new JsonArray();

        foreach (var file in files)
        {
            filesOut.Add(new JsonObject
            {
                ["projectID"] = file.ProjectId,
                ["fileID"] = file.FileId,

                /*
                 * With optional files off, EVERYTHING is required -- including mods the user had
                 * disabled. Upstream's `enabled || !optionalFiles`, and the right call: the format
                 * has no way to say "shipped but off", so the choice is between exporting it as
                 * required or losing it.
                 */
                ["required"] = file.Enabled || !optionalFiles,
            });
        }

        manifest["files"] = filesOut;

        return manifest;
    }

    /// <summary>
    /// The single compound loader id a CurseForge manifest can carry, or null for vanilla.
    /// </summary>
    /// <remarks>
    /// ONE LOADER ONLY, and the priority is fixed rather than derived: Quilt, then Fabric, then Forge,
    /// then NeoForge. The format has room for a list but every consumer reads one, so an instance
    /// carrying two components has to lose one, and a stable order at least makes which one
    /// predictable.
    /// </remarks>
    public static string? CreateFlameLoaderId(IReadOnlyList<PackComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        string? VersionOf(string uid) => components.Any(c => c.Uid == uid)
            ? components.First(c => c.Uid == uid).Version
            : null;

        if (VersionOf(PackComponents.QuiltUid) is { } quilt)
        {
            return "quilt-" + quilt;
        }

        if (VersionOf(PackComponents.FabricUid) is { } fabric)
        {
            return "fabric-" + fabric;
        }

        if (VersionOf(PackComponents.ForgeUid) is { } forge)
        {
            return "forge-" + forge;
        }

        if (VersionOf(PackComponents.NeoForgeUid) is { } neoForge)
        {
            /*
             * The 1.20.1 special case again, mirrored from the import side: CurseForge expects the
             * Minecraft version embedded for that one release. Writing it without would produce a
             * manifest CurseForge's own client cannot install.
             */
            var prefix = VersionOf(PackComponents.MinecraftUid) == "1.20.1" ? "1.20.1-" : string.Empty;

            return "neoforge-" + prefix + neoForge;
        }

        return null;
    }

    /// <summary>
    /// Builds the modlist.html CurseForge packs ship alongside the manifest.
    /// </summary>
    /// <remarks>
    /// Purely for humans -- nothing reads it back -- but it is what a pack page shows as its mod list,
    /// so names and authors are HTML-escaped. A mod called <c>&lt;script&gt;</c> is unlikely; a mod
    /// with an ampersand in its name is not.
    /// </remarks>
    public static string CreateFlameModList(IEnumerable<FlameExportFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var builder = new StringBuilder("<ul>");

        foreach (var file in files)
        {
            // Mods only. Resource packs and shaders are in the pack but not in its mod list.
            if (!file.IsMod)
            {
                continue;
            }

            var url = ModIndex.GetMetaUrl(
                ResourceProvider.Flame,
                file.ProjectId.ToString(CultureInfo.InvariantCulture));

            var authors = file.Authors.Length != 0
                ? HttpUtility.HtmlEncode($" (by {file.Authors})")
                : string.Empty;

            builder
                .Append("<li><a href=\"")
                .Append(HttpUtility.HtmlEncode(url))
                .Append("\">")
                .Append(HttpUtility.HtmlEncode(file.Name))
                .Append(authors)
                .Append("</a></li>\n");
        }

        return builder.Append("</ul>").ToString();
    }
}
