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
 * The blocked-file fallback is what most of these are about. It decides whether a CurseForge pack
 * installs unattended or stops to ask the user to hand-download a dozen jars, and every branch of it
 * fails quietly rather than loudly -- a wrong match installs the wrong jar with no error at all.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class FlameFileResolverTests
{
    private sealed class FakeApi : IFlameResolverApi
    {
        public Dictionary<int, IndexedVersion> Files { get; } = [];

        public Dictionary<int, IndexedPack> Projects { get; } = [];

        public Dictionary<string, IndexedVersion> ModrinthBySha1 { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<IReadOnlyList<int>> FileLookups { get; } = [];

        public List<IReadOnlyList<string>> ModrinthLookups { get; } = [];

        public Exception? ModrinthFailure { get; set; }

        public Task<IReadOnlyDictionary<int, IndexedVersion>> GetFilesAsync(
            IReadOnlyList<int> fileIds,
            CancellationToken cancellationToken)
        {
            FileLookups.Add(fileIds);

            IReadOnlyDictionary<int, IndexedVersion> found = Files
                .Where(kv => fileIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            return Task.FromResult(found);
        }

        public Task<IReadOnlyDictionary<int, IndexedPack>> GetProjectsAsync(
            IReadOnlyList<int> projectIds,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<int, IndexedPack> found = Projects
                .Where(kv => projectIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            return Task.FromResult(found);
        }

        public Task<IReadOnlyDictionary<string, IndexedVersion>> GetModrinthVersionsBySha1Async(
            IReadOnlyList<string> hashes,
            CancellationToken cancellationToken)
        {
            ModrinthLookups.Add(hashes);

            if (ModrinthFailure is not null)
            {
                return Task.FromException<IReadOnlyDictionary<string, IndexedVersion>>(ModrinthFailure);
            }

            IReadOnlyDictionary<string, IndexedVersion> found = ModrinthBySha1
                .Where(kv => hashes.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            return Task.FromResult(found);
        }
    }

    private static FlamePackManifest ManifestWith(params (int ProjectId, int FileId)[] files)
    {
        var manifest = new FlamePackManifest();

        foreach (var (projectId, fileId) in files)
        {
            manifest.Files[fileId] = new FlamePackFile { ProjectId = projectId, FileId = fileId };
        }

        return manifest;
    }

    private static IndexedVersion Release(
        string fileId,
        string downloadUrl = "https://edge.forgecdn.net/files/a.jar",
        string hash = "",
        string hashType = "sha1",
        ModLoaderTypes loaders = ModLoaderTypes.Forge)
        => new()
        {
            FileId = fileId,
            FileName = $"file-{fileId}.jar",
            DownloadUrl = downloadUrl,
            Hash = hash,
            HashType = hashType,
            Loaders = loaders,
        };

    // ================================================================== the happy path

    [Fact]
    public async Task EveryFileIsResolvedToItsReleaseAndProject()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100");
        api.Projects[10] = new IndexedPack { AddonId = "10", Name = "Mod A", WebsiteUrl = "https://cf.invalid/a" };

        var resolved = await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100)), api)
            .ConfigureAwait(true);

        var file = Assert.Single(resolved);

        Assert.Equal("file-100.jar", file.Version.FileName);
        Assert.Equal("Mod A", file.Pack.Name);
        Assert.False(file.IsBlocked);
    }

    /// <summary>An empty pack is a success with nothing in it, not a request for zero files.</summary>
    [Fact]
    public async Task AnEmptyManifestCostsNoRequests()
    {
        var api = new FakeApi();

        Assert.Empty(await FlameFileResolver.ResolveAsync(new FlamePackManifest(), api).ConfigureAwait(true));
        Assert.Empty(api.FileLookups);
    }

    /// <summary>Ordered by file id, so a resolution is reproducible whatever order the manifest used.</summary>
    [Fact]
    public async Task ResolutionOrderIsStable()
    {
        var api = new FakeApi();

        foreach (var id in (int[])[300, 100, 200])
        {
            api.Files[id] = Release(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var resolved = await FlameFileResolver
            .ResolveAsync(ManifestWith((1, 300), (2, 100), (3, 200)), api)
            .ConfigureAwait(true);

        Assert.Equal([100, 200, 300], resolved.Select(f => f.Entry.FileId));
    }

    /// <summary>Every file id in one request, not one request per file.</summary>
    [Fact]
    public async Task AllFileIdsAreAskedForAtOnce()
    {
        var api = new FakeApi();

        await FlameFileResolver.ResolveAsync(ManifestWith((1, 100), (2, 200), (3, 300)), api).ConfigureAwait(true);

        Assert.Equal([100, 200, 300], Assert.Single(api.FileLookups).Order());
    }

    /*
     * Upstream searches the file list for a matching project id and stops at the first hit, so a pack
     * shipping two files from one project leaves the second without a name or a link -- which is what
     * the blocked-mod dialog then shows the user. Mapping by id gives all of them the project.
     */
    [Fact]
    public async Task TwoFilesFromOneProjectBothGetTheProject()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100");
        api.Files[101] = Release("101");
        api.Projects[10] = new IndexedPack { AddonId = "10", Name = "Mod A" };

        var resolved = await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100), (10, 101)), api)
            .ConfigureAwait(true);

        Assert.All(resolved, f => Assert.Equal("Mod A", f.Pack.Name));
    }

    // ================================================================== blocked files

    /*
     * CurseForge lets an author forbid third-party downloads: the launcher is told what it needs and
     * not allowed to fetch it. The sha1 CurseForge still publishes is what makes the fallback possible.
     */
    [Fact]
    public async Task AFileCurseforgeWillNotServeIsLookedUpOnModrinthBySha1()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: "abc123");
        api.ModrinthBySha1["abc123"] = Release("mr", downloadUrl: "https://cdn.modrinth.com/a.jar");

        var file = Assert.Single(await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100)), api)
            .ConfigureAwait(true));

        Assert.Equal("https://cdn.modrinth.com/a.jar", file.Version.DownloadUrl);
        Assert.True(file.ResolvedViaModrinth);
        Assert.False(file.IsBlocked);

        // Only the URL is taken -- the rest of the entry stays CurseForge's, because the pack is theirs.
        Assert.Equal("file-100.jar", file.Version.FileName);
    }

    /// <summary>A servable file is never asked about, so an unblocked pack costs no Modrinth calls.</summary>
    [Fact]
    public async Task NothingBlockedMeansNoModrinthRequest()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100");

        await FlameFileResolver.ResolveAsync(ManifestWith((10, 100)), api).ConfigureAwait(true);

        Assert.Empty(api.ModrinthLookups);
    }

    /// <summary>Without a sha1 there is no question to ask.</summary>
    [Theory]
    [InlineData("", "sha1")]
    [InlineData("abc123", "md5")]
    public async Task ABlockedFileWithNoUsableHashIsNotLookedUp(string hash, string hashType)
    {
        var api = new FakeApi();
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: hash, hashType: hashType);

        var file = Assert.Single(await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100)), api)
            .ConfigureAwait(true));

        Assert.Empty(api.ModrinthLookups);
        Assert.True(file.IsBlocked);
    }

    /*
     * UPSTREAM'S GUARD, kept. A Modrinth release can cover several loaders at once where a CurseForge
     * file is per-loader, so a multi-loader match is not confidently the same artifact even though
     * the sha1 agreed. The cost of being wrong is installing the wrong jar with no error.
     */
    [Fact]
    public async Task AMultiLoaderModrinthMatchIsNotTrusted()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: "abc123");
        api.ModrinthBySha1["abc123"] = Release(
            "mr",
            downloadUrl: "https://cdn.modrinth.com/a.jar",
            loaders: ModLoaderTypes.Fabric | ModLoaderTypes.Quilt);

        var file = Assert.Single(await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100)), api)
            .ConfigureAwait(true));

        Assert.True(file.IsBlocked);
        Assert.False(file.ResolvedViaModrinth);
    }

    /// <summary>A match declaring no loader at all is accepted — a resource pack has none.</summary>
    [Fact]
    public async Task AModrinthMatchWithNoLoaderIsAccepted()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: "abc123");
        api.ModrinthBySha1["abc123"] = Release(
            "mr",
            downloadUrl: "https://cdn.modrinth.com/a.jar",
            loaders: ModLoaderTypes.None);

        var file = Assert.Single(await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100)), api)
            .ConfigureAwait(true));

        Assert.False(file.IsBlocked);
    }

    /*
     * The fallback is opportunistic. Modrinth being unreachable must not fail a CurseForge import --
     * the files stay blocked, which is exactly where they were without the attempt.
     */
    [Fact]
    public async Task ModrinthBeingUnreachableDoesNotFailTheImport()
    {
        var api = new FakeApi { ModrinthFailure = new HttpRequestException("no route to host") };
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: "abc123");
        api.Files[200] = Release("200");

        var resolved = await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100), (20, 200)), api)
            .ConfigureAwait(true);

        Assert.Equal(2, resolved.Count);
        Assert.True(resolved.Single(f => f.Entry.FileId == 100).IsBlocked);
        Assert.False(resolved.Single(f => f.Entry.FileId == 200).IsBlocked);
    }

    /// <summary>A hash Modrinth does not know leaves the file blocked, which is not an error.</summary>
    [Fact]
    public async Task AnUnmatchedHashLeavesTheFileBlocked()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: "abc123");

        var blocked = FlameFileResolver.GetBlocked(await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100)), api)
            .ConfigureAwait(true));

        Assert.Single(blocked);
    }

    /// <summary>Duplicate hashes are asked about once.</summary>
    [Fact]
    public async Task RepeatedHashesAreDeduplicatedBeforeAsking()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: "same");
        api.Files[200] = Release("200", downloadUrl: string.Empty, hash: "same");

        await FlameFileResolver.ResolveAsync(ManifestWith((10, 100), (20, 200)), api).ConfigureAwait(true);

        Assert.Single(Assert.Single(api.ModrinthLookups));
    }

    // ================================================================== manual download links

    [Fact]
    public async Task ABlockedFileGetsALinkTheUserCanFollow()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: "abc123");
        api.Projects[10] = new IndexedPack { AddonId = "10", WebsiteUrl = "https://cf.invalid/mods/a" };

        var file = Assert.Single(await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100)), api)
            .ConfigureAwait(true));

        Assert.Equal("https://cf.invalid/mods/a/download/100", file.ManualDownloadUrl);
    }

    /// <summary>A bare "/download/123" is worse than no link, so an unknown project gives none.</summary>
    [Fact]
    public async Task AFileWithNoProjectGetsNoManualLink()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100", downloadUrl: string.Empty, hash: "abc123");

        var file = Assert.Single(await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100)), api)
            .ConfigureAwait(true));

        Assert.Equal(string.Empty, file.ManualDownloadUrl);
    }

    /// <summary>A file the API never returned stays unresolved rather than losing the pack.</summary>
    [Fact]
    public async Task AFileTheApiDoesNotKnowIsReportedBlocked()
    {
        var api = new FakeApi();
        api.Files[100] = Release("100");

        var resolved = await FlameFileResolver
            .ResolveAsync(ManifestWith((10, 100), (20, 999)), api)
            .ConfigureAwait(true);

        Assert.Equal(2, resolved.Count);
        Assert.True(resolved.Single(f => f.Entry.FileId == 999).IsBlocked);
    }
}
