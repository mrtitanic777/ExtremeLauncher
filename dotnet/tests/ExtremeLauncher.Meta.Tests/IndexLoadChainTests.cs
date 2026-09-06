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
 * Characterization tests for the meta load chain. Upstream has no Qt test for Index::loadVersion.
 *
 * The chain is index → version list → version document, and the ORDER is a dependency order rather
 * than a preference: each document names the hash of the one below it. These tests assert the order
 * and the two places it is short-circuited.
 */

using System.Net;
using System.Text;
using ExtremeLauncher.Net;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class IndexLoadChainTests : IDisposable
{
    private const string MetaBaseUrl = "https://meta.invalid/v1/";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-chain-" + Guid.NewGuid().ToString("N"));

    public IndexLoadChainTests()
    {
        Directory.CreateDirectory(_temp);
        MetaDir = Path.Combine(_temp, "meta");
        Directory.CreateDirectory(MetaDir);
    }

    private string MetaDir { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>Serves a body per meta path and records the order they were asked for.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies;

        public RecordingHandler(Dictionary<string, string> bodies) => _bodies = bodies;

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            // Everything after the meta root, which is how these documents name each other.
            var path = request.RequestUri!.AbsolutePath.Replace("/v1/", string.Empty, StringComparison.Ordinal);
            Paths.Add(path);

            return Task.FromResult(_bodies.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
        }
    }

    private HttpMetaCache NewCache()
    {
        var cache = new HttpMetaCache(Path.Combine(_temp, "index-cache.json"));
        cache.AddBase("meta", MetaDir);

        return cache;
    }

    private static Dictionary<string, string> WholeTree() => new(StringComparer.Ordinal)
    {
        ["index.json"] = """
            { "formatVersion": 1, "packages": [ { "uid": "net.minecraft", "name": "Minecraft" } ] }
            """,

        ["net.minecraft/index.json"] = """
            {
                "formatVersion": 1, "uid": "net.minecraft", "name": "Minecraft",
                "versions": [ { "version": "1.20.1", "type": "release", "releaseTime": "2023-06-12T13:25:51+00:00" } ]
            }
            """,

        ["net.minecraft/1.20.1.json"] = """
            {
                "formatVersion": 1, "uid": "net.minecraft", "version": "1.20.1", "name": "Minecraft",
                "type": "release", "releaseTime": "2023-06-12T13:25:51+00:00", "mainClass": "Main"
            }
            """,
    };

    private (Index Index, HttpClient Client, RecordingHandler Handler) Setup(Dictionary<string, string>? bodies = null)
    {
        var handler = new RecordingHandler(bodies ?? WholeTree());
        return (new Index(), new HttpClient(handler), handler);
    }

    // ================================================================== the chain

    [Fact]
    public async Task TheWholeChainIsFetchedInDependencyOrder()
    {
        var (index, client, handler) = Setup();

        using (client)
        {
            var version = await index.GetLoadedVersionAsync(
                "net.minecraft", "1.20.1", client, NewCache(), MetaDir, MetaBaseUrl);

            // Index first, then the package's list, then the version itself: each names the hash of the
            // one after it, so fetching out of order validates against a stale hash.
            Assert.Equal(["index.json", "net.minecraft/index.json", "net.minecraft/1.20.1.json"], handler.Paths);

            Assert.NotNull(version.Data);
            Assert.Equal("Main", version.Data.MainClass);

            // The placeholder created before the list was fetched is the SAME object the list merge
            // and the document load filled in. Replacing it instead would leave the caller holding an
            // empty version while a detached one carried the data.
            Assert.Same(version, index.Get("net.minecraft", "1.20.1"));
        }
    }

    [Fact]
    public async Task ASecondLoadSkipsTheIndex()
    {
        var (index, client, handler) = Setup();

        using (client)
        {
            var cache = NewCache();

            await index.GetLoadedVersionAsync("net.minecraft", "1.20.1", client, cache, MetaDir, MetaBaseUrl);
            handler.Paths.Clear();

            await index.GetLoadedVersionAsync("net.minecraft", "1.20.1", client, cache, MetaDir, MetaBaseUrl);

            // The index is the one document nothing vouches for, so re-fetching it every time would
            // make every launch wait on the meta server.
            Assert.DoesNotContain("index.json", handler.Paths);
        }
    }

    [Fact]
    public async Task ForcingPutsTheIndexBackInTheChain()
    {
        var (index, client, handler) = Setup();

        using (client)
        {
            var cache = NewCache();

            await index.GetLoadedVersionAsync("net.minecraft", "1.20.1", client, cache, MetaDir, MetaBaseUrl);

            var normal = index.CreateLoadVersionTask(
                "net.minecraft", "1.20.1", client, cache, MetaDir, MetaBaseUrl, NetMode.Online);

            var forced = index.CreateLoadVersionTask(
                "net.minecraft", "1.20.1", client, cache, MetaDir, MetaBaseUrl, NetMode.Online, force: true);

            // `force` decides whether the index step is IN the chain — two steps versus three.
            Assert.Equal(2, normal.TotalSize);
            Assert.Equal(3, forced.TotalSize);

            handler.Paths.Clear();
            await forced.RunAsync();

            // NOTE: the step being present does not mean the network is touched. Each load task
            // short-circuits on an entity that is already current, so a forced chain over a fresh
            // index still fetches nothing. `force` overrides the CHAIN's own skip, not the entity's.
            // Upstream has the same two-level arrangement.
            Assert.DoesNotContain("index.json", handler.Paths);
        }
    }

    [Fact]
    public async Task OfflineFetchesNothingAndUsesWhatIsOnDisk()
    {
        var (index, client, handler) = Setup();

        using (client)
        {
            var cache = NewCache();

            // Populate the on-disk cache first.
            await index.GetLoadedVersionAsync("net.minecraft", "1.20.1", client, cache, MetaDir, MetaBaseUrl);
            handler.Paths.Clear();

            var offline = new Index();

            var version = await offline.GetLoadedVersionAsync(
                "net.minecraft", "1.20.1", client, NewCache(), MetaDir, MetaBaseUrl, NetMode.Offline);

            // The chain collapses to the version document alone, and it comes off disk.
            Assert.Empty(handler.Paths);
            Assert.NotNull(version.Data);
        }
    }

    [Fact]
    public async Task AFailedFetchLeavesTheVersionUnloadedRatherThanThrowing()
    {
        // Only the index resolves; the version document 404s.
        var bodies = WholeTree();
        bodies.Remove("net.minecraft/1.20.1.json");

        var (index, client, _) = Setup(bodies);

        using (client)
        {
            var version = await index.GetLoadedVersionAsync(
                "net.minecraft", "1.20.1", client, NewCache(), MetaDir, MetaBaseUrl);

            // A version is always handed back; the caller checks whether it loaded, as upstream's do.
            Assert.Null(version.Data);
            Assert.False(version.IsLoaded);
        }
    }

    // ================================================================== creation on demand

    [Fact]
    public void AskingForAnUnknownVersionCreatesAPlaceholder()
    {
        var index = new Index();

        // The cold-cache case: nothing has been fetched, so nothing is known — but the document's URL
        // is derivable from the uid and version alone, which is what makes a first run possible.
        Assert.Null(index.Get("net.minecraft", "1.20.1"));

        var created = index.GetOrCreate("net.minecraft", "1.20.1");

        Assert.Equal("net.minecraft/1.20.1.json", created.LocalFilename);
        Assert.NotNull(index.Get("net.minecraft", "1.20.1"));

        // And asking twice gives the same object, not a second placeholder.
        Assert.Same(created, index.GetOrCreate("net.minecraft", "1.20.1"));
    }

    [Fact]
    public void ThePlainLookupStaysNonCreating()
    {
        var list = new VersionList("net.minecraft");

        Assert.Null(list.GetVersion("1.20.1"));
        Assert.False(list.HasVersion("1.20.1"));

        // A caller that means "is this known?" must not quietly populate the list.
        Assert.Empty(list.Versions);
    }

    // ================================================================== the ComponentUpdateTask seam

    [Fact]
    public async Task TheVersionLoaderReportsWhetherABodyArrived()
    {
        var (index, client, _) = Setup();

        using (client)
        {
            var load = index.CreateVersionLoader(client, NewCache(), MetaDir, MetaBaseUrl);

            Assert.True(await load("net.minecraft", "1.20.1", CancellationToken.None));

            // Nothing serves this one, so no body arrives.
            Assert.False(await load("net.minecraft", "9.9.9", CancellationToken.None));
        }
    }

    [Fact]
    public async Task AProfileResolvesEndToEndThroughTheRealLoader()
    {
        var (index, client, handler) = Setup();

        using (client)
        {
            var profile = new PackProfile(new Minecraft.RuntimeContext { System = "linux", JavaArchitecture = "64" });
            profile.AppendComponent(new Component("net.minecraft") { Version = "1.20.1" });

            var task = new ComponentUpdateTask(
                profile,
                index,
                Path.Combine(_temp, "patches"),
                ComponentUpdateMode.Launch,
                NetMode.Online,
                index.CreateVersionLoader(client, NewCache(), MetaDir, MetaBaseUrl));

            Assert.True(await task.RunAsync());

            // Cold cache to a resolved profile: nothing was known at the start of this test.
            Assert.Equal(["index.json", "net.minecraft/index.json", "net.minecraft/1.20.1.json"], handler.Paths);
            Assert.Equal("Minecraft", profile.GetComponent("net.minecraft")!.CachedName);
            Assert.Equal("Main", profile.GetProfile().MainClass);
        }
    }
}
