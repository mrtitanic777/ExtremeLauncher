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
 * Ported from launcher/modplatform/flame/FileResolvingTask.{h,cpp}.
 *
 * TURNS A CURSEFORGE MANIFEST INTO SOMETHING INSTALLABLE. The manifest names (projectID, fileID)
 * pairs and nothing else, so this is the step that finds out what those actually are: three requests,
 * in order, each depending on the last.
 *
 *   1. Every file id at once -> the releases, with their names, hashes and download URLs.
 *   2. Any file CurseForge will not serve -> looked up ON MODRINTH by sha1, in case the same artifact
 *      is hosted there too.
 *   3. Every project id -> the project details, for names and links.
 *
 * STEP 2 IS THE INTERESTING ONE. CurseForge lets an author forbid third-party downloads, and such a
 * file comes back with an empty URL -- the launcher is told what it needs but not allowed to fetch
 * it. Upstream's move is to take the sha1 CurseForge still publishes and ask Modrinth whether it has
 * a file with that hash. Same bytes, different host, and the pack installs without the user hand-
 * downloading anything. Whatever is still blocked afterwards is genuinely unavailable, and the caller
 * has to ask the user to fetch it by hand.
 */

using System.Globalization;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>The two lookups resolution needs, behind an interface so the flow can be tested.</summary>
public interface IFlameResolverApi
{
    /// <summary>The releases for these file ids, keyed by file id.</summary>
    Task<IReadOnlyDictionary<int, IndexedVersion>> GetFilesAsync(
        IReadOnlyList<int> fileIds,
        CancellationToken cancellationToken);

    /// <summary>The projects for these ids, keyed by project id.</summary>
    Task<IReadOnlyDictionary<int, IndexedPack>> GetProjectsAsync(
        IReadOnlyList<int> projectIds,
        CancellationToken cancellationToken);

    /// <summary>Modrinth releases matching these sha1 hashes, keyed by hash.</summary>
    Task<IReadOnlyDictionary<string, IndexedVersion>> GetModrinthVersionsBySha1Async(
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken);
}

/// <summary>One fully resolved entry of a pack.</summary>
public sealed class FlameResolvedFile
{
    public required FlamePackFile Entry { get; init; }

    /// <summary>What CurseForge says the file is. Empty until resolved.</summary>
    public IndexedVersion Version { get; set; } = new();

    /// <summary>The project it belongs to, once fetched.</summary>
    public IndexedPack Pack { get; set; } = new();

    /// <summary>Whether the file was found on Modrinth after CurseForge declined to serve it.</summary>
    public bool ResolvedViaModrinth { get; set; }

    /// <summary>
    /// Whether the file still cannot be downloaded and the user must fetch it by hand.
    /// </summary>
    /// <remarks>
    /// The single question the whole resolution exists to answer, so it is computed from the state
    /// rather than stored -- a flag would be one more thing to keep in step.
    /// </remarks>
    public bool IsBlocked => Version.DownloadUrl.Length == 0;

    /// <summary>Where a user would go to download it themselves.</summary>
    /// <remarks>
    /// Built from the project page plus the file id, which is the only link CurseForge offers for a
    /// specific file. Empty when the project details did not arrive, since a bare "/download/123" is
    /// worse than no link.
    /// </remarks>
    public string ManualDownloadUrl
        => Pack.WebsiteUrl.Length == 0
            ? string.Empty
            : $"{Pack.WebsiteUrl}/download/{Entry.FileId.ToString(CultureInfo.InvariantCulture)}";
}

public static class FlameFileResolver
{
    /// <summary>Resolves every file a manifest names.</summary>
    public static async Task<List<FlameResolvedFile>> ResolveAsync(
        FlamePackManifest manifest,
        IFlameResolverApi api,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(api);

        // Ordered by file id so a resolution is reproducible whatever order the manifest listed them.
        var resolved = manifest.Files.Values
            .OrderBy(f => f.FileId)
            .Select(entry => new FlameResolvedFile { Entry = entry })
            .ToList();

        // An empty pack is a success with nothing in it, not a request for zero files.
        if (resolved.Count == 0)
        {
            return resolved;
        }

        var versions = await api
            .GetFilesAsync([.. resolved.Select(f => f.Entry.FileId)], cancellationToken)
            .ConfigureAwait(false);

        foreach (var file in resolved)
        {
            if (versions.TryGetValue(file.Entry.FileId, out var version))
            {
                file.Version = version;
            }
        }

        await ResolveBlockedViaModrinthAsync(resolved, api, cancellationToken).ConfigureAwait(false);

        await AttachProjectsAsync(resolved, api, cancellationToken).ConfigureAwait(false);

        return resolved;
    }

    /// <summary>
    /// Tries to find files CurseForge will not serve on Modrinth instead, matched by sha1.
    /// </summary>
    private static async Task ResolveBlockedViaModrinthAsync(
        List<FlameResolvedFile> resolved,
        IFlameResolverApi api,
        CancellationToken cancellationToken)
    {
        /*
         * Only files with a sha1 are worth asking about -- the hash is the entire question. CurseForge
         * publishes one even for files it will not serve, which is what makes this possible at all.
         */
        var blocked = resolved
            .Where(f => f.IsBlocked && f.Version.HashType == "sha1" && f.Version.Hash.Length != 0)
            .ToList();

        if (blocked.Count == 0)
        {
            return;
        }

        var hashes = blocked
            .Select(f => f.Version.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyDictionary<string, IndexedVersion> alternatives;

        try
        {
            alternatives = await api.GetModrinthVersionsBySha1Async(hashes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException)
        {
            /*
             * This whole step is opportunistic. Modrinth being unreachable must not fail a CurseForge
             * import -- the files stay blocked, which is exactly where they were without it.
             */
            return;
        }

        foreach (var file in blocked)
        {
            if (!alternatives.TryGetValue(file.Version.Hash, out var alternative)
                || alternative.DownloadUrl.Length == 0)
            {
                continue;
            }

            /*
             * UPSTREAM'S GUARD, kept: "if there's more than one mod loader for this version, we can't
             * know for sure". A Modrinth release can cover several loaders at once where a CurseForge
             * file is per-loader, so a multi-loader match is not confidently the same artifact -- even
             * though the sha1 agreed. Conservative, and the cost of being wrong is installing the
             * wrong jar silently.
             */
            if (alternative.Loaders != ModLoaderTypes.None && !ModIndex.HasSingleModLoader(alternative.Loaders))
            {
                continue;
            }

            // Only the URL is taken. Everything else stays CurseForge's, because the pack is theirs.
            file.Version.DownloadUrl = alternative.DownloadUrl;
            file.ResolvedViaModrinth = true;
        }
    }

    /// <summary>Fetches the project behind each file, for names and links.</summary>
    private static async Task AttachProjectsAsync(
        List<FlameResolvedFile> resolved,
        IFlameResolverApi api,
        CancellationToken cancellationToken)
    {
        var projectIds = resolved
            .Select(f => f.Entry.ProjectId)
            .Where(id => id != 0)
            .Distinct()
            .ToList();

        if (projectIds.Count == 0)
        {
            return;
        }

        var projects = await api.GetProjectsAsync(projectIds, cancellationToken).ConfigureAwait(false);

        /*
         * Every file gets its project, not just the first one naming it. Upstream searches the file
         * list for a matching project id and stops at the first hit, so a pack shipping two files
         * from one project leaves the second without a name or a link -- which is what the blocked-mod
         * dialog shows the user. Fixed here: the mapping is by id, so all of them get it.
         */
        foreach (var file in resolved)
        {
            if (projects.TryGetValue(file.Entry.ProjectId, out var pack))
            {
                file.Pack = pack;
            }
        }
    }

    /// <summary>The files still needing a manual download, in the order they were resolved.</summary>
    public static List<FlameResolvedFile> GetBlocked(IEnumerable<FlameResolvedFile> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return [.. resolved.Where(f => f.IsBlocked)];
    }
}

/// <summary>Drives the resolution against the real APIs.</summary>
public sealed class FlameResolverApi : IFlameResolverApi
{
    private readonly FlameApi _flame;
    private readonly ModrinthApi _modrinth;

    public FlameResolverApi(FlameApi flame, ModrinthApi modrinth)
    {
        _flame = flame;
        _modrinth = modrinth;
    }

    public async Task<IReadOnlyDictionary<int, IndexedVersion>> GetFilesAsync(
        IReadOnlyList<int> fileIds,
        CancellationToken cancellationToken)
    {
        var response = await _flame
            .GetFilesAsync([.. fileIds.Select(id => id.ToString(CultureInfo.InvariantCulture))], cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<int, IndexedVersion>();

        foreach (var element in Json.EnsureArray(Json.RequireObject(response)["data"]))
        {
            var obj = Json.EnsureObject(element);

            // One unparseable release loses that file, not the whole pack.
            try
            {
                if (FlameModIndex.LoadIndexedPackVersion(obj) is { } version
                    && int.TryParse(version.FileId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    result[id] = version;
                }
            }
            catch (LauncherException)
            {
                // Left out; the caller sees it as unresolved.
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<int, IndexedPack>> GetProjectsAsync(
        IReadOnlyList<int> projectIds,
        CancellationToken cancellationToken)
    {
        var ids = projectIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();

        var response = ids.Count == 1
            ? await _flame.GetProjectAsync(ids[0], cancellationToken).ConfigureAwait(false)
            : await _flame.GetProjectsAsync(ids, cancellationToken).ConfigureAwait(false);

        var data = Json.RequireObject(response)["data"];

        var entries = data is System.Text.Json.Nodes.JsonArray array
            ? array.Select(node => Json.RequireObject(node))
            : [Json.RequireObject(data)];

        var result = new Dictionary<int, IndexedPack>();

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

            if (int.TryParse(pack.AddonId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                result[id] = pack;
            }
        }

        return result;
    }

    /// <remarks>
    /// sha1, not Modrinth's preferred sha512, because the hash has to be one CurseForge also
    /// publishes -- and sha1 is the only algorithm both speak for a file the launcher has never seen.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IndexedVersion>> GetModrinthVersionsBySha1Async(
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken)
    {
        var response = await _modrinth.CurrentVersionsAsync(hashes, "sha1", cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, IndexedVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var hash in hashes)
        {
            if (response[hash] is not System.Text.Json.Nodes.JsonObject entry)
            {
                continue;
            }

            try
            {
                if (ModrinthPackIndex.LoadIndexedPackVersion(entry, preferredHashType: "sha1") is { } version)
                {
                    result[hash] = version;
                }
            }
            catch (LauncherException)
            {
                // Left out; the file stays blocked.
            }
        }

        return result;
    }
}
