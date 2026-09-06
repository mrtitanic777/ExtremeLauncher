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
 * Ported from launcher/minecraft/OneSixVersionFormat.{h,cpp}.
 *
 * The launcher's own patch format: a superset of Mojang's, adding jar mods, Java agents, LaunchWrapper
 * tweakers, extra JVM arguments, traits, and inter-package dependencies. Every patch in an instance's
 * version stack -- vanilla, Forge, Fabric, the user's own tweaks -- is one of these.
 *
 * MMC- PREFIXED KEYS are MultiMC-era extensions to a library entry: local-file hints, absolute URLs,
 * filename and display-name overrides. They are read from BOTH "MMC-absulute_url" and
 * "MMC-absoluteUrl" -- the first is a typo that shipped, and files containing it still exist.
 *
 * !! ONE UPSTREAM BUG FIXED -- see PORTING.md !!
 * versionFileToJson()'s "mods" block guards on patch->mods but iterates patch->jarMods, so serializing
 * a patch with mods writes the jar mods out under the "mods" key. Faithfully reproducing that would
 * corrupt user patch files on every save. Fixed here, and tested.
 *
 * NOT PORTED: the "runtimes" array, which needs Java::Metadata from wave 4's deferred half.
 */

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

public static partial class OneSixVersionFormat
{
    /// <summary>A uid must look like a reverse-DNS name; anything else is a security concern.</summary>
    [GeneratedRegex(@"\A(?:[a-zA-Z0-9-_]+(?:\.[a-zA-Z0-9-_]+)*)\z")]
    private static partial Regex ValidUidPattern();

    // ================================================================== libraries

    public static Library LibraryFromJson(ProblemContainer problems, JsonObject libraryObject, string filename)
    {
        var result = MojangVersionFormat.LibraryFromJson(problems, libraryObject, filename);

        ReadString(libraryObject, "MMC-hint", value => result.Hint = value);

        // Both spellings: the first shipped as a typo and files with it are still out there.
        ReadString(libraryObject, "MMC-absulute_url", value => result.AbsoluteUrl = value);
        ReadString(libraryObject, "MMC-absoluteUrl", value => result.AbsoluteUrl = value);

        ReadString(libraryObject, "MMC-filename", value => result.Filename = value);
        ReadString(libraryObject, "MMC-displayname", value => result.DisplayNameOverride = value);

        return result;
    }

    public static JsonObject LibraryToJson(Library library)
    {
        var result = MojangVersionFormat.LibraryToJson(library);

        if (library.AbsoluteUrl.Length != 0)
        {
            result["MMC-absoluteUrl"] = library.AbsoluteUrl;
        }

        if (library.Hint.Length != 0)
        {
            result["MMC-hint"] = library.Hint;
        }

        if (library.Filename.Length != 0)
        {
            result["MMC-filename"] = library.Filename;
        }

        if (library.DisplayNameOverride.Length != 0)
        {
            result["MMC-displayname"] = library.DisplayNameOverride;
        }

        return result;
    }

    // ================================================================== reading

    public static VersionFile VersionFileFromJson(JsonObject? document, string filename, bool requireOrder = false)
    {
        var root = document ?? throw new JsonException($"{filename} is empty or null");
        var result = new VersionFile();

        if (MetadataFormat.ParseFormatVersion(root, required: false) == MetadataVersion.Invalid)
        {
            throw new JsonException($"{filename} does not contain a recognizable version of the metadata format.");
        }

        if (requireOrder && root.ContainsKey("order"))
        {
            result.Order = Json.RequireInteger(root, "order");
        }

        result.Name = Json.EnsureString(root, "name");

        // "fileId" is the pre-rename spelling of "uid".
        result.Uid = root.ContainsKey("uid") ? Json.EnsureString(root, "uid") : Json.EnsureString(root, "fileId");

        if (!ValidUidPattern().IsMatch(result.Uid))
        {
            result.AddProblem(
                ProblemSeverity.Error,
                "The component's 'uid' contains illegal characters! This can cause security issues.");
        }

        result.Version = Json.EnsureString(root, "version");

        MojangVersionFormat.ReadVersionProperties(root, result);

        // Legacy Minecraft window embedding.
        ReadString(root, "appletClass", value => result.AppletClass = value);

        ReadStringArray(root, "+tweakers", result.AddTweakers.Add);
        ReadStringArray(root, "+traits", value => result.Traits.Add(value));
        ReadStringArray(root, "+jvmArgs", result.AddnJvmArguments.Add);

        ReadJarMods(root, result, filename);
        ReadObjectArray(root, "mods", obj => result.Mods.Add(LibraryFromJson(result, obj, filename)));

        ReadLibraries(root, result, filename);

        ReadObjectArray(root, "mavenFiles", obj => result.MavenFiles.Add(LibraryFromJson(result, obj, filename)));

        ReadObjectArray(root, "+agents", obj =>
        {
            var library = LibraryFromJson(result, obj, filename);
            result.Agents.Add(new Agent(library, Json.EnsureString(obj, "argument")));
        });

        ReadMainJar(root, result, filename);
        ReadDependencies(root, result);

        if (root.ContainsKey("volatile"))
        {
            result.IsVolatile = Json.RequireBoolean(root, "volatile");
        }

        // Only the meta server's "net.minecraft.java" package carries these; every other patch has
        // no "runtimes" member at all.
        if (root["runtimes"] is JsonArray runtimes)
        {
            foreach (var runtime in runtimes)
            {
                result.Runtimes.Add(Java.JavaMetadata.Parse(Json.EnsureObject(runtime)));
            }
        }

        ReportRemovedFeatures(root, result);

        return result;
    }

    private static void ReadLibraries(JsonObject root, VersionFile result, string filename)
    {
        var hasPlus = root.ContainsKey("+libraries");
        var hasPlain = root.ContainsKey("libraries");

        if (hasPlus && hasPlain)
        {
            result.AddProblem(
                ProblemSeverity.Warning,
                "Version file has both '+libraries' and 'libraries'. This is no longer supported.");
        }

        if (hasPlain)
        {
            ReadObjectArray(root, "libraries", obj => result.Libraries.Add(LibraryFromJson(result, obj, filename)));
        }

        if (hasPlus)
        {
            ReadObjectArray(root, "+libraries", obj => result.Libraries.Add(LibraryFromJson(result, obj, filename)));
        }
    }

    private static void ReadJarMods(JsonObject root, VersionFile result, string filename)
    {
        if (root.ContainsKey("jarMods"))
        {
            ReadObjectArray(root, "jarMods", obj => result.JarMods.Add(LibraryFromJson(result, obj, filename)));
            return;
        }

        // DEPRECATED: '+jarMods' is only here for backwards compatibility.
        if (root.ContainsKey("+jarMods"))
        {
            ReadObjectArray(
                root,
                "+jarMods",
                obj => result.JarMods.Add(PlusJarModFromJson(result, obj, filename, result.Name)));
        }
    }

    private static void ReadMainJar(JsonObject root, VersionFile result, string filename)
    {
        if (root["mainJar"] is JsonObject mainJar)
        {
            result.MainJar = LibraryFromJson(result, mainJar, filename);
            return;
        }

        if (result.MinecraftVersion.Length == 0)
        {
            return;
        }

        // Reconstruct it from the version id plus the client download.
        var library = new Library($"com.mojang:minecraft:{result.MinecraftVersion}:client");

        if (result.MojangDownloads.TryGetValue("client", out var client))
        {
            library.MojangDownloads = new MojangLibraryDownloadInfo(client);
        }
        else
        {
            result.AddProblem(
                ProblemSeverity.Error,
                "URL for the main jar could not be determined - Mojang removed the server that we used as fallback.");
        }

        result.MainJar = library;
    }

    private static void ReadDependencies(JsonObject root, VersionFile result)
    {
        if (root.ContainsKey("requires"))
        {
            foreach (var requirement in MetadataFormat.ParseRequires(root, "requires"))
            {
                result.Requires.Add(requirement);
            }
        }

        // "mcVersion" is shorthand for requiring a specific Minecraft version.
        var dependsOnMinecraft = Json.EnsureString(root, "mcVersion");

        if (dependsOnMinecraft.Length != 0)
        {
            // Add() is a no-op if net.minecraft is already required, since Require is keyed by uid.
            result.Requires.Add(new Require("net.minecraft", dependsOnMinecraft));
        }

        if (root.ContainsKey("conflicts"))
        {
            foreach (var conflict in MetadataFormat.ParseRequires(root, "conflicts"))
            {
                result.Conflicts.Add(conflict);
            }
        }
    }

    /// <summary>Flags format elements that were removed rather than silently ignoring them.</summary>
    private static void ReportRemovedFeatures(JsonObject root, VersionFile result)
    {
        foreach (var removed in (ReadOnlySpan<string>)
                 ["tweakers", "-libraries", "-tweakers", "-minecraftArguments", "+minecraftArguments"])
        {
            if (root.ContainsKey(removed))
            {
                result.AddProblem(ProblemSeverity.Error, $"Version file contains unsupported element '{removed}'");
            }
        }
    }

    /// <summary>
    /// Reads a legacy <c>+jarMods</c> entry, which named a file rather than a Maven coordinate.
    /// </summary>
    /// <remarks>
    /// A unique coordinate is invented on the spot, the original name becomes the filename override,
    /// and the entry is marked local because it lives in the instance's jarmods folder.
    /// </remarks>
    public static Library PlusJarModFromJson(
        ProblemContainer problems,
        JsonObject libraryObject,
        string filename,
        string originalName)
    {
        if (!libraryObject.ContainsKey("name"))
        {
            throw new JsonException($"{filename} contains a jarmod that doesn't have a 'name' field");
        }

        var result = new Library($"org.multimc.jarmods:{Guid.NewGuid():D}:1")
        {
            Filename = Json.EnsureString(libraryObject, "name"),
            Hint = "local",
        };

        var displayName = Json.EnsureString(libraryObject, "originalName");

        result.DisplayNameOverride = displayName.Length != 0
            ? displayName
            : originalName.Replace(" (jar mod)", string.Empty, StringComparison.Ordinal);

        return result;
    }

    // ================================================================== writing

    public static JsonObject VersionFileToJson(VersionFile patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var root = new JsonObject();

        Json.WriteString(root, "name", patch.Name);
        Json.WriteString(root, "uid", patch.Uid);
        Json.WriteString(root, "version", patch.Version);

        MetadataFormat.SerializeFormatVersion(root, MetadataVersion.InitialRelease);

        MojangVersionFormat.WriteVersionProperties(patch, root);

        if (patch.MainJar is { } mainJar)
        {
            root["mainJar"] = LibraryToJson(mainJar);
        }

        Json.WriteString(root, "appletClass", patch.AppletClass);
        Json.WriteStringList(root, "+tweakers", patch.AddTweakers);
        Json.WriteStringList(root, "+traits", patch.Traits);
        Json.WriteStringList(root, "+jvmArgs", patch.AddnJvmArguments);

        if (patch.Agents.Count != 0)
        {
            var array = new JsonArray();

            foreach (var agent in patch.Agents)
            {
                var agentObject = LibraryToJson(agent.Library);

                if (agent.Argument.Length != 0)
                {
                    agentObject["argument"] = agent.Argument;
                }

                array.Add(agentObject);
            }

            root["+agents"] = array;
        }

        WriteLibraries(root, "libraries", patch.Libraries);
        WriteLibraries(root, "mavenFiles", patch.MavenFiles);
        WriteLibraries(root, "jarMods", patch.JarMods);

        // Upstream iterates jarMods here; that is a copy-paste bug. See the file header.
        WriteLibraries(root, "mods", patch.Mods);

        MetadataFormat.SerializeRequires(root, ToRequireSet(patch.Requires), "requires");
        MetadataFormat.SerializeRequires(root, ToRequireSet(patch.Conflicts), "conflicts");

        if (patch.IsVolatile)
        {
            root["volatile"] = true;
        }

        return root;
    }

    private static RequireSet? ToRequireSet(RequireSet source) => source.Count == 0 ? null : source;

    private static void WriteLibraries(JsonObject root, string key, IReadOnlyList<Library> libraries)
    {
        if (libraries.Count == 0)
        {
            return;
        }

        var array = new JsonArray();

        foreach (var library in libraries)
        {
            array.Add(LibraryToJson(library));
        }

        root[key] = array;
    }

    // ================================================================== helpers

    private static void ReadString(JsonObject root, string key, Action<string> assign)
    {
        if (root.ContainsKey(key))
        {
            assign(Json.RequireString(root, key));
        }
    }

    private static void ReadStringArray(JsonObject root, string key, Action<string> add)
    {
        if (!root.ContainsKey(key))
        {
            return;
        }

        foreach (var element in Json.RequireArray(root, key))
        {
            add(Json.RequireString(element));
        }
    }

    private static void ReadObjectArray(JsonObject root, string key, Action<JsonObject> handle)
    {
        if (!root.ContainsKey(key))
        {
            return;
        }

        foreach (var element in Json.RequireArray(root, key))
        {
            handle(element as JsonObject ?? throw new JsonException($"'{key}' entry is not an object"));
        }
    }
}
