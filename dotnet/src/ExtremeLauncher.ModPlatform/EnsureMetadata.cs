// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2022 flowln <flowlnlnln@gmail.com>
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
 * Ported from launcher/modplatform/EnsureMetadataTask.{h,cpp}.
 *
 * WHAT IS THIS JAR? A mods folder that someone copied in from elsewhere is a pile of files with no
 * record of where any of them came from -- so no update checks, no dependency resolution, nothing.
 * This is what recovers that: hash each file, ask the provider which release has that hash, ask which
 * project that release belongs to, and write the answer beside the pack as packwiz metadata.
 *
 * NEITHER API ACCEPTS A FILENAME, and that is the point. Names get renamed, versioned, duplicated and
 * suffixed; content does not. Modrinth answers on sha512, CurseForge on a murmur2 fingerprint.
 *
 * THE SHAPE IS A PIPELINE, NOT A SIGNAL GRAPH. Upstream wires five tasks together with signals, holds
 * a m_current_task pointer so abort() has something to forward to, disconnects everything in abort()
 * to avoid delivering to a dead object, and threads results through two member dictionaries. All of
 * that is machinery for "do these four steps in order and let me cancel", which is four awaits and a
 * token. The steps and their order are upstream's; the plumbing is gone.
 */

using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>One file to identify.</summary>
/// <param name="Path">Where it is. Also what gets hashed.</param>
/// <param name="Name">What to call it in messages.</param>
/// <param name="HasMetadataFor">
/// Which provider it already has metadata for, if any. Such a file is skipped entirely.
/// </param>
/// <param name="IsFolder">Folders have no metadata and are reported ready without being looked up.</param>
/// <param name="IsValid">An unreadable or unrecognised resource fails immediately.</param>
public sealed record ResourceToIdentify(
    string Path,
    string Name,
    ResourceProvider? HasMetadataFor = null,
    bool IsFolder = false,
    bool IsValid = true);

/// <summary>What happened to each file.</summary>
public sealed class EnsureMetadataResult
{
    /// <summary>Files that now have metadata, or already did.</summary>
    public List<ResourceToIdentify> Ready { get; } = [];

    /// <summary>Files the provider could not identify.</summary>
    public List<ResourceToIdentify> Failed { get; } = [];
}

/// <summary>The provider calls this needs, kept behind an interface so the flow can be tested.</summary>
/// <remarks>
/// Upstream reaches for a file-static <c>ModrinthAPI</c> and <c>FlameAPI</c> from inside the task,
/// which makes every step of this a network operation. The two implementations below are thin; the
/// ordering and bookkeeping above them are what actually need testing.
/// </remarks>
public interface IMetadataProvider
{
    ResourceProvider Provider { get; }

    /// <summary>Maps each hash the provider recognises to the release it belongs to.</summary>
    Task<IReadOnlyDictionary<string, IndexedVersion>> GetVersionsByHashAsync(
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken);

    /// <summary>Maps each project id to its project.</summary>
    Task<IReadOnlyDictionary<string, IndexedPack>> GetProjectsAsync(
        IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken);
}

public static class EnsureMetadata
{
    /// <summary>The suffix a disabled mod's file carries.</summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>
    /// Identifies each file and writes packwiz metadata for the ones the provider recognises.
    /// </summary>
    /// <param name="indexDirectory">Where the .pw.toml files live.</param>
    /// <param name="hashFile">
    /// How to hash one file. Injected so the flow can be exercised without real jars on disk.
    /// </param>
    public static async Task<EnsureMetadataResult> RunAsync(
        IReadOnlyList<ResourceToIdentify> resources,
        string indexDirectory,
        IMetadataProvider provider,
        Func<string, string>? hashFile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(provider);

        var result = new EnsureMetadataResult();
        var algorithm = Hashing.AlgorithmFor(provider.Provider);

        hashFile ??= path => Hashing.HashFile(path, algorithm);

        // Hashes are the keys everything downstream is joined on, so the map is built once here.
        var byHash = new Dictionary<string, ResourceToIdentify>(StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            /*
             * Three ways a file needs no lookup, in upstream's order. Note that an INVALID resource
             * fails before the folder check, so an unreadable folder is a failure rather than a
             * silent success.
             */
            if (!resource.IsValid)
            {
                result.Failed.Add(resource);
                continue;
            }

            if (resource.HasMetadataFor == provider.Provider)
            {
                result.Ready.Add(resource);
                continue;
            }

            if (resource.IsFolder)
            {
                result.Ready.Add(resource);
                continue;
            }

            string hash;

            try
            {
                hash = hashFile(resource.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or LauncherException)
            {
                // A file that cannot be read cannot be identified, and that is not fatal to the rest.
                result.Failed.Add(resource);
                continue;
            }

            /*
             * A duplicate hash means the same jar under two names. Only one can own the metadata --
             * the entry is keyed by hash -- so the second is reported as failed rather than silently
             * dropped, which is what upstream's QHash insert would do.
             */
            if (!byHash.TryAdd(hash, resource))
            {
                result.Failed.Add(resource);
            }
        }

        if (byHash.Count == 0)
        {
            return result;
        }

        var versions = await provider
            .GetVersionsByHashAsync([.. byHash.Keys], cancellationToken)
            .ConfigureAwait(false);

        /*
         * One project can own several of these files, so the ids are deduplicated before asking. A
         * pack with twenty files from one project is one request, not twenty.
         */
        var addonIds = versions.Values
            .Select(v => v.AddonId)
            .Where(id => id.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var projects = addonIds.Count == 0
            ? new Dictionary<string, IndexedPack>(StringComparer.Ordinal)
            : await provider.GetProjectsAsync(addonIds, cancellationToken).ConfigureAwait(false);

        foreach (var (hash, resource) in byHash)
        {
            if (!versions.TryGetValue(hash, out var version)
                || !projects.TryGetValue(version.AddonId, out var pack))
            {
                // Recognised by neither step, or a release whose project the API did not return.
                result.Failed.Add(resource);
                continue;
            }

            var metadata = CreateMetadata(pack, version, resource);

            if (!Packwiz.UpdateModIndex(indexDirectory, metadata))
            {
                result.Failed.Add(resource);
                continue;
            }

            result.Ready.Add(resource);
        }

        return result;
    }

    /// <summary>Builds the packwiz entry for one identified file.</summary>
    public static PackwizMod CreateMetadata(IndexedPack pack, IndexedVersion version, ResourceToIdentify resource)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(resource);

        var metadata = new PackwizMod
        {
            Slug = pack.Slug,
            Name = pack.Name,

            /*
             * THE PROVIDER'S FILENAME IS NOT USED. The entry has to name the file as it actually sits
             * on disk, or nothing will match it back -- a jar the user renamed, or one CurseForge
             * records under a different name, would look like a different mod on the next scan.
             */
            Filename = LocalFileName(resource.Path),

            HashFormat = version.HashType,
            Hash = version.Hash,
            Provider = pack.Provider,

            /*
             * A version that says nothing about sides inherits the project's answer. Upstream's
             * `version.side.isEmpty() ? pack.side : version.side` -- the file is the more specific
             * statement, so it wins when it makes one.
             */
            Side = Packwiz.StringToSide(version.Side.Length != 0 ? version.Side : pack.Side),

            ReleaseType = ModIndex.ToString(version.VersionType),
        };

        if (pack.Provider == ResourceProvider.Flame)
        {
            /*
             * CurseForge forbids third-party downloads for some projects, so there is often no URL to
             * record. "metadata:curseforge" is packwiz's way of saying "ask CurseForge at install
             * time" -- which is why the mode differs by provider rather than by whether a URL exists.
             */
            metadata.Mode = "metadata:curseforge";

            metadata.ProjectId = ParseId(pack.AddonId);
            metadata.FileId = ParseId(version.FileId);
        }
        else
        {
            metadata.Mode = "url";
            metadata.Url = version.DownloadUrl;

            metadata.ModId = pack.AddonId;
            metadata.Version = version.FileId;
        }

        foreach (var loader in EnumerateLoaders(version.Loaders))
        {
            metadata.Loaders.Add(loader);
        }

        // Sorted, so an entry rewritten from a differently ordered API response does not churn.
        metadata.McVersions.AddRange(version.McVersion.Order(StringComparer.Ordinal));

        return metadata;
    }

    /// <summary>
    /// The file's name on disk, with the disabled marker removed.
    /// </summary>
    /// <remarks>
    /// A disabled mod is <c>foo.jar.disabled</c> on disk, but the metadata has to say <c>foo.jar</c> --
    /// otherwise enabling it renames the file and the entry stops matching. Upstream chops the
    /// suffix by its length; the constant says what the number was.
    /// </remarks>
    public static string LocalFileName(string path)
    {
        var name = System.IO.Path.GetFileName(path);

        return name.EndsWith(DisabledSuffix, StringComparison.Ordinal)
            ? name[..^DisabledSuffix.Length]
            : name;
    }

    /// <summary>Each loader flag that is set, by name.</summary>
    private static IEnumerable<string> EnumerateLoaders(ModLoaderTypes loaders)
        => Enum.GetValues<ModLoaderTypes>()
            .Where(l => l != ModLoaderTypes.None && loaders.HasFlag(l))
            .Select(ModIndex.ToString)
            .Where(name => name.Length != 0);

    /// <summary>A CurseForge id, or 0 if it is not a number after all.</summary>
    private static int ParseId(string id)
        => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}

/// <summary>Identifies files against Modrinth, by sha512.</summary>
public sealed class ModrinthMetadataProvider : IMetadataProvider
{
    private readonly ModrinthApi _api;

    public ModrinthMetadataProvider(ModrinthApi api) => _api = api;

    public ResourceProvider Provider => ResourceProvider.Modrinth;

    /// <remarks>
    /// Modrinth answers with an object keyed by the hashes it recognised and omits the rest, so an
    /// absent key means "unknown file" rather than an error.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IndexedVersion>> GetVersionsByHashAsync(
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken)
    {
        var hashType = ProviderCapabilities.HashTypes(ResourceProvider.Modrinth)[0];
        var response = await _api.CurrentVersionsAsync(hashes, hashType, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, IndexedVersion>(StringComparer.Ordinal);

        foreach (var hash in hashes)
        {
            if (response[hash] is not JsonObject entry)
            {
                continue;
            }

            // One unparseable entry loses that file, not the whole batch.
            try
            {
                if (ModrinthPackIndex.LoadIndexedPackVersion(entry) is { } version)
                {
                    result[hash] = version;
                }
            }
            catch (LauncherException)
            {
                // Left out of the map, so the caller reports it failed.
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, IndexedPack>> GetProjectsAsync(
        IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken)
    {
        /*
         * One id uses the single-project endpoint, which answers with an object; several use the batch
         * one, which answers with an array. Upstream special-cases the same way -- the batch endpoint
         * is not wrong for one id, but the shapes differ and both have to be handled anyway.
         */
        var response = addonIds.Count == 1
            ? await _api.GetProjectAsync(addonIds[0], cancellationToken).ConfigureAwait(false)
            : await _api.GetProjectsAsync(addonIds, cancellationToken).ConfigureAwait(false);

        var entries = addonIds.Count == 1
            ? [Json.RequireObject(response)]
            : Json.RequireArray(response).Select(node => Json.RequireObject(node));

        var result = new Dictionary<string, IndexedPack>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var pack = new IndexedPack();

            try
            {
                ModrinthPackIndex.LoadIndexedPack(pack, entry);
            }
            catch (LauncherException)
            {
                // Skip this project; the files that needed it are reported failed.
                continue;
            }

            result[pack.AddonId] = pack;
        }

        return result;
    }
}

/// <summary>Identifies files against CurseForge, by murmur2 fingerprint.</summary>
public sealed class FlameMetadataProvider : IMetadataProvider
{
    private readonly FlameApi _api;

    public FlameMetadataProvider(FlameApi api) => _api = api;

    public ResourceProvider Provider => ResourceProvider.Flame;

    /// <remarks>
    /// The fingerprints are keyed back by the value CurseForge echoes in the matched file, not by
    /// request order -- the response is not guaranteed to be in the order asked, and unmatched files
    /// simply do not appear.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IndexedVersion>> GetVersionsByHashAsync(
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken)
    {
        var fingerprints = hashes
            .Select(h => uint.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? (uint?)v
                : null)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();

        var result = new Dictionary<string, IndexedVersion>(StringComparer.Ordinal);

        if (fingerprints.Count == 0)
        {
            return result;
        }

        var response = await _api.MatchFingerprintsAsync(fingerprints, cancellationToken).ConfigureAwait(false);

        var data = Json.EnsureObject(Json.RequireObject(response)["data"]);

        foreach (var element in Json.EnsureArray(data["exactMatches"]))
        {
            var match = Json.EnsureObject(element);

            if (match["file"] is not JsonObject file)
            {
                continue;
            }

            var fingerprint = Json.EnsureInteger(file["fileFingerprint"], 0);

            if (fingerprint == 0)
            {
                continue;
            }

            try
            {
                if (FlameModIndex.LoadIndexedPackVersion(file) is { } version)
                {
                    result[fingerprint.ToString(CultureInfo.InvariantCulture)] = version;
                }
            }
            catch (LauncherException)
            {
                // Left out of the map, so the caller reports it failed.
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, IndexedPack>> GetProjectsAsync(
        IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken)
    {
        var response = addonIds.Count == 1
            ? await _api.GetProjectAsync(addonIds[0], cancellationToken).ConfigureAwait(false)
            : await _api.GetProjectsAsync(addonIds, cancellationToken).ConfigureAwait(false);

        // Both CurseForge endpoints wrap their answer in "data"; the single one wraps an object.
        var data = Json.RequireObject(response)["data"];

        var entries = data is JsonArray array
            ? array.Select(node => Json.RequireObject(node))
            : [Json.RequireObject(data)];

        var result = new Dictionary<string, IndexedPack>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var pack = new IndexedPack();

            try
            {
                FlameModIndex.LoadIndexedPack(pack, entry);
            }
            catch (LauncherException)
            {
                continue;
            }

            result[pack.AddonId] = pack;
        }

        return result;
    }
}
