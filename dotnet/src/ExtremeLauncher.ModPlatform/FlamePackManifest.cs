// SPDX-License-Identifier: Apache-2.0
/*
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
 * Ported from launcher/modplatform/flame/PackManifest.{h,cpp}.
 *
 * NOTE THE LICENCE: this file is Apache-2.0, not GPL-3.0-only like the rest of the port. It carries
 * MultiMC's original header because it is a port of a file that predates the fork, and the header
 * travels with the code.
 *
 * A CURSEFORGE PACK NAMES IDS, NOT URLS. Where a .mrpack lists a download URL and a sha512 for every
 * file, manifest.json lists a (projectID, fileID) pair and nothing else:
 *
 *     { "projectID": 306612, "fileID": 3814740, "required": true }
 *
 * So nothing can be downloaded straight from the manifest. Every entry has to be resolved through the
 * API first -- which is why importing a CurseForge pack needs an API key and network access before
 * the first byte of any mod arrives, and why a pack whose files were since deleted from CurseForge
 * cannot be installed at all even if the files exist on a mirror somewhere.
 *
 * IT ALSO MEANS THE MANIFEST CARRIES NO INTEGRITY INFORMATION. There is no hash to verify a download
 * against; the launcher trusts whatever the API hands back. That is CurseForge's design, not a gap in
 * this port, and it is worth knowing when comparing the two formats' security properties.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>One file a CurseForge pack names, before it has been resolved to a download.</summary>
public sealed class FlamePackFile
{
    public int ProjectId { get; set; }

    public int FileId { get; set; }

    /// <summary>
    /// Whether the pack requires this file.
    /// </summary>
    /// <remarks>
    /// The manifest spells this the positive way round, unlike Modrinth's env block. Absent means
    /// required, so a pack that says nothing gets everything.
    /// </remarks>
    public bool Required { get; set; } = true;

    /// <summary>Where it goes once resolved. Mods unless the API says it is another resource kind.</summary>
    public string TargetFolder { get; set; } = "mods";
}

/// <summary>A loader the pack asks for.</summary>
/// <remarks>
/// The id is a compound string such as "forge-47.2.0" -- CurseForge does not separate the loader from
/// its version here. Splitting it is the installer's job, not the parser's.
/// </remarks>
public sealed class FlameModloader
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Whether this is the loader to install. A pack may list several and mark one.</summary>
    public bool Primary { get; set; }
}

public sealed class FlameMinecraft
{
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// A free-text libraries field.
    /// </summary>
    /// <remarks>
    /// Upstream's comment says it is used only by a custom launcher for the 1.2.5 FTB retro pack, and
    /// that the intended meaning is hardcoded in CurseForge's own client -- the manifest format says
    /// nothing about it. Read and carried, never interpreted.
    /// </remarks>
    public string Libraries { get; set; } = string.Empty;

    public List<FlameModloader> ModLoaders { get; } = [];
}

/// <summary>A parsed CurseForge manifest.json.</summary>
public sealed class FlamePackManifest
{
    public string ManifestType { get; set; } = string.Empty;

    public int ManifestVersion { get; set; }

    public FlameMinecraft Minecraft { get; } = new();

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>The files the pack names, keyed by file id.</summary>
    /// <remarks>
    /// Keyed rather than listed, matching upstream's QMap, because resolution answers come back keyed
    /// by file id and have to be matched up. A manifest naming the same file id twice therefore
    /// collapses to one entry -- which is upstream's behaviour and harmless, since the second entry
    /// would name the identical download.
    /// </remarks>
    public Dictionary<int, FlamePackFile> Files { get; } = [];

    /// <summary>The folder inside the zip whose contents are copied over the instance.</summary>
    public string Overrides { get; set; } = DefaultOverrides;

    /// <summary>The name CurseForge uses when a pack does not say.</summary>
    public const string DefaultOverrides = "overrides";
}

public static class FlamePack
{
    /// <summary>The only manifest type this understands.</summary>
    public const string SupportedManifestType = "minecraftModpack";

    /// <summary>The only manifest version this understands.</summary>
    public const int SupportedManifestVersion = 1;

    /// <summary>Reads a manifest.json.</summary>
    public static FlamePackManifest Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var obj = Json.RequireObject(Json.RequireDocument(data, "manifest.json"));
        var manifest = new FlamePackManifest
        {
            ManifestType = Json.RequireString(obj, "manifestType"),
        };

        /*
         * CHECKED BEFORE ANYTHING ELSE IS READ. CurseForge uses manifest.json for other things too, so
         * this is what distinguishes a modpack from, say, a world save -- and the fields below would
         * parse happily against the wrong kind of document and produce a nonsense pack.
         */
        if (manifest.ManifestType != SupportedManifestType)
        {
            throw new JsonException("Not a modpack manifest!");
        }

        manifest.ManifestVersion = Json.RequireInteger(obj, "manifestVersion");

        if (manifest.ManifestVersion != SupportedManifestVersion)
        {
            throw new JsonException($"Unknown manifest version ({manifest.ManifestVersion})");
        }

        ParseMinecraft(Json.RequireObject(obj, "minecraft"), manifest.Minecraft);

        // Defaults that keep an under-specified pack installable, exactly as upstream chooses them.
        manifest.Name = Json.EnsureString(obj, "name", "Unnamed");
        manifest.Version = Json.EnsureString(obj, "version");
        manifest.Author = Json.EnsureString(obj, "author", "Anonymous");

        foreach (var element in Json.EnsureArray(obj["files"]))
        {
            var file = ParseFile(Json.RequireObject(element));

            manifest.Files[file.FileId] = file;
        }

        manifest.Overrides = Json.EnsureString(obj, "overrides", FlamePackManifest.DefaultOverrides);

        /*
         * SECURITY, AND NOT PRESENT UPSTREAM. The overrides folder name comes out of an untrusted
         * manifest and is used as a path inside the archive, so it gets the same containment check
         * the .mrpack file paths get. See ModrinthPack.ValidateRelativePath for the reasoning.
         */
        ModrinthPack.ValidateRelativePath(manifest.Overrides);

        return manifest;
    }

    private static FlamePackFile ParseFile(System.Text.Json.Nodes.JsonObject obj)
        => new()
        {
            ProjectId = Json.RequireInteger(obj, "projectID"),
            FileId = Json.RequireInteger(obj, "fileID"),

            // Absent means required: a pack that says nothing gets everything.
            Required = Json.EnsureBoolean(obj["required"], true),
        };

    private static void ParseMinecraft(System.Text.Json.Nodes.JsonObject obj, FlameMinecraft minecraft)
    {
        minecraft.Version = Json.RequireString(obj, "version");
        minecraft.Libraries = Json.EnsureString(obj, "libraries");

        foreach (var element in Json.EnsureArray(obj["modLoaders"]))
        {
            var loader = Json.RequireObject(element);

            minecraft.ModLoaders.Add(new FlameModloader
            {
                Id = Json.RequireString(loader, "id"),
                Primary = Json.EnsureBoolean(loader["primary"], false),
            });
        }
    }

    /// <summary>
    /// The loader to install, or null when the pack names none.
    /// </summary>
    /// <remarks>
    /// DELIBERATE DIVERGENCE. Upstream ignores the <c>primary</c> flag entirely on import -- it loops
    /// every modLoaders entry, overwrites its working variables each time, and never breaks, so the
    /// LAST recognised loader wins and the flag is dead. It is written on export and never read back.
    /// </remarks>
    /// <remarks>
    /// Here the flag wins, because it is the manifest's own explicit statement of which loader to
    /// install and CurseForge's exporter sets it on exactly the intended one. Falling back to the last
    /// recognised entry keeps upstream's answer for every manifest that marks nothing -- so this
    /// changes behaviour only for packs where upstream was picking against a stated preference.
    /// </remarks>
    public static FlameModloader? GetPrimaryModloader(FlamePackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return manifest.Minecraft.ModLoaders.Find(l => l.Primary)
            ?? manifest.Minecraft.ModLoaders.LastOrDefault();
    }

    /// <summary>
    /// Splits a loader id such as "forge-47.2.0" into its loader and version.
    /// </summary>
    /// <remarks>
    /// The FIRST hyphen separates them, not the last: loader names have no hyphen and versions
    /// routinely do (<c>fabric-0.15.0-build.1</c>). Splitting from the wrong end silently produces a
    /// loader called "fabric-0.15.0" that no metadata server has heard of.
    /// </remarks>
    /// <returns>The loader name and its version, either of which may be empty.</returns>
    public static (string Loader, string Version) SplitModloaderId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var separator = id.IndexOf('-', StringComparison.Ordinal);

        return separator < 0
            ? (id, string.Empty)
            : (id[..separator], id[(separator + 1)..]);
    }
}
