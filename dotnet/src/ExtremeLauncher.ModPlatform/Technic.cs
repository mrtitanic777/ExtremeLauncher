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
 * Ported from launcher/modplatform/technic/SolderPackManifest.{h,cpp} and the version.json half of
 * TechnicPackProcessor.cpp.
 *
 * TECHNIC PACKS DO NOT SAY WHAT THEY ARE. Modrinth and CurseForge packs both declare their loader and
 * its version in a field named for the purpose. A Technic pack ships a vanilla-shaped version.json
 * and leaves the launcher to work it out from the LIBRARY LIST -- which loader is present is inferred
 * from Maven coordinates, and its version from wherever that particular loader happens to encode it.
 *
 * That inference is the whole of this file's difficulty, and every branch of it was added because
 * some real pack broke the previous rule. They are ported one for one, with the shapes that motivated
 * them written down, because there is no way to derive them from anything.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

// ================================================================== Solder

/// <summary>A Solder pack's build list.</summary>
/// <remarks>
/// Solder is Technic's server-side pack host. The pack document names builds; a build document names
/// the mods. Two requests, because a pack with a hundred builds would otherwise send all of them.
/// </remarks>
public sealed class SolderPack
{
    public string Recommended { get; set; } = string.Empty;

    public string Latest { get; set; } = string.Empty;

    public List<string> Builds { get; } = [];
}

/// <summary>One mod of a Solder build.</summary>
public sealed class SolderPackBuildMod
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional: Solder allows a mod with no version recorded.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>An md5, which is all Solder publishes. Weak, and the only thing on offer.</summary>
    public string Md5 { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

/// <summary>One build of a Solder pack.</summary>
public sealed class SolderPackBuild
{
    public string Minecraft { get; set; } = string.Empty;

    public List<SolderPackBuildMod> Mods { get; } = [];
}

public static class TechnicSolder
{
    /// <summary>Reads a Solder pack document.</summary>
    public static SolderPack LoadPack(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var pack = new SolderPack
        {
            Recommended = Json.RequireString(obj, "recommended"),
            Latest = Json.RequireString(obj, "latest"),
        };

        foreach (var build in Json.RequireArray(obj, "builds"))
        {
            pack.Builds.Add(Json.RequireString(build));
        }

        return pack;
    }

    /// <summary>Reads a Solder build document.</summary>
    public static SolderPackBuild LoadPackBuild(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var build = new SolderPackBuild { Minecraft = Json.RequireString(obj, "minecraft") };

        foreach (var element in Json.RequireArray(obj, "mods"))
        {
            var mod = Json.RequireObject(element);

            build.Mods.Add(new SolderPackBuildMod
            {
                Name = Json.RequireString(mod, "name"),

                // The only optional field: Solder allows a mod with no version recorded.
                Version = Json.EnsureString(mod, "version"),

                Md5 = Json.RequireString(mod, "md5"),
                Url = Json.RequireString(mod, "url"),
            });
        }

        return build;
    }
}

// ================================================================== version.json

public static class TechnicVersionJson
{
    /// <summary>
    /// Works out an instance's components from a Technic pack's version.json.
    /// </summary>
    /// <param name="fmlMinecraftVersion">
    /// The version from <c>fmlversion.properties</c> inside modpack.jar, where the pack has one. Used
    /// only when version.json does not say — some old FML packs omit <c>inheritsFrom</c> entirely.
    /// </param>
    /// <exception cref="JsonException">
    /// When the Minecraft version cannot be determined at all. Everything else degrades: a pack whose
    /// loader is unrecognised still installs as vanilla plus whatever its jar mods provide.
    /// </exception>
    public static List<PackComponent> DetectComponents(JsonObject root, string fmlMinecraftVersion = "")
    {
        ArgumentNullException.ThrowIfNull(root);

        /*
         * "inheritsFrom" is the vanilla version this one patches, which for a modpack is the Minecraft
         * version. Old FML packs predate the field, hence the fallback -- and if neither says, the
         * pack cannot be installed at all, because there is no version to fetch.
         */
        var minecraft = Json.EnsureString(root, "inheritsFrom");

        if (minecraft.Length == 0)
        {
            minecraft = fmlMinecraftVersion;
        }

        if (minecraft.Length == 0)
        {
            throw new JsonException("Could not understand \"version.json\":\ninheritsFrom is missing");
        }

        var components = new List<PackComponent>
        {
            new(PackComponents.MinecraftUid, PackComponents.NormaliseMinecraftVersion(minecraft), Important: true),
        };

        if (DetectLoader(root) is { } loader)
        {
            components.Add(loader);
        }

        return components;
    }

    /// <summary>
    /// Finds the loader in the library list, or null if none is recognised.
    /// </summary>
    /// <remarks>
    /// FIRST MATCH WINS. Upstream breaks out of the library loop on NeoForge and on Forge, so a later
    /// library cannot override them.
    /// </remarks>
    public static PackComponent? DetectLoader(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        PackComponent? fromPrefixMap = null;

        foreach (var element in Json.EnsureArray(root["libraries"]))
        {
            if (element is not JsonObject library)
            {
                continue;
            }

            var name = Json.EnsureString(library, "name");

            if (name.StartsWith("net.neoforged.fancymodloader:", StringComparison.Ordinal))
            {
                // Returns even when the version cannot be found, matching upstream's break: the pack
                // is NeoForge either way, and guessing another loader from a later library is worse.
                return DetectNeoForgeVersion(root) is { Length: > 0 } version
                    ? new PackComponent(PackComponents.NeoForgeUid, version)
                    : null;
            }

            if (DetectForgeVersion(name) is { } forge)
            {
                return forge;
            }

            /*
             * The prefix map is checked LAST and does not stop the loop -- upstream's inner `break`
             * exits only the map lookup, so the outer loop keeps going and a later Forge or NeoForge
             * library still wins. Recorded and returned only if nothing better turns up.
             */
            fromPrefixMap ??= MatchPrefixMap(name);
        }

        return fromPrefixMap;
    }

    /// <summary>
    /// Reads a Forge version out of its library coordinate.
    /// </summary>
    /// <remarks>
    /// The coordinate is <c>net.minecraftforge:forge:&lt;mc&gt;-&lt;forge&gt;</c>, so the Forge
    /// version is everything after the first hyphen -- except on 1.7.10, where the coordinate is
    /// <c>1.7.10-10.13.4.1614-1.7.10</c> and the Minecraft version is repeated on the end. There the
    /// middle field alone is wanted. Upstream special-cases the "1.7.10-" prefix for exactly this.
    /// </remarks>
    public static PackComponent? DetectForgeVersion(string libraryName)
    {
        ArgumentNullException.ThrowIfNull(libraryName);

        if (!libraryName.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal)
            && !libraryName.StartsWith("net.minecraftforge:fmlloader:", StringComparison.Ordinal))
        {
            return null;
        }

        // Without a hyphen there is no Minecraft-version prefix to strip, and nothing to read.
        if (!libraryName.Contains('-', StringComparison.Ordinal))
        {
            return null;
        }

        var version = Section(libraryName, ':', 2);

        var forge = version.StartsWith("1.7.10-", StringComparison.Ordinal)
            ? Section(libraryName, '-', 1, 1)
            : Section(libraryName, '-', 1);

        return forge.Length == 0 ? null : new PackComponent(PackComponents.ForgeUid, forge);
    }

    /// <summary>
    /// Digs the NeoForge version out of the game arguments.
    /// </summary>
    /// <remarks>
    /// NEOFORGE DOES NOT PUT ITS VERSION IN A LIBRARY COORDINATE, so upstream reads the value that
    /// follows <c>--fml.neoForgeVersion</c> in the game argument list -- a launcher inferring a
    /// component version from a command line it is about to build. Fragile, and there is nowhere else
    /// it appears. <c>--fml.forgeVersion</c> is accepted too: NeoForge kept the older spelling for a
    /// while.
    /// </remarks>
    public static string DetectNeoForgeVersion(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var arguments = Json.EnsureObject(root["arguments"]);
        var expectVersion = false;

        foreach (var element in Json.EnsureArray(arguments["game"]))
        {
            var argument = Json.EnsureString(element);

            if (expectVersion)
            {
                return argument;
            }

            expectVersion = argument is "--fml.neoForgeVersion" or "--fml.forgeVersion";
        }

        return string.Empty;
    }

    /// <summary>Loaders whose library coordinate carries their version in the usual place.</summary>
    private static PackComponent? MatchPrefixMap(string name)
    {
        foreach (var (prefix, uid) in (ReadOnlySpan<(string, string)>)[
            ("net.minecraftforge:minecraftforge:", PackComponents.ForgeUid),
            ("net.fabricmc:fabric-loader:", PackComponents.FabricUid),
            ("org.quiltmc:quilt-loader:", PackComponents.QuiltUid),
        ])
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return new PackComponent(uid, Section(name, ':', 2));
            }
        }

        return null;
    }

    /// <summary>
    /// Qt's <c>QString::section</c>, for the two uses this file makes of it.
    /// </summary>
    /// <remarks>
    /// With no end, everything from <paramref name="start"/> onwards INCLUDING further separators --
    /// which is what makes the 1.7.10 case need the narrower form. Ported rather than replaced by
    /// Split because the difference between the two forms is the whole point of that special case.
    /// </remarks>
    private static string Section(string value, char separator, int start, int? end = null)
    {
        var fields = value.Split(separator);

        if (start >= fields.Length)
        {
            return string.Empty;
        }

        var last = end is null ? fields.Length - 1 : Math.Min(end.Value, fields.Length - 1);

        return string.Join(separator, fields[start..(last + 1)]);
    }
}
