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
 * Characterization tests for the metadata fetch-and-cache flow. No upstream Qt test exists.
 * Everything runs against a stub handler, so the suite stays offline.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class MetaEntityTests : IDisposable
{
    private const string MetaBaseUrl = "https://meta.invalid/v1/";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-meta-" + Guid.NewGuid().ToString("N"));

    public MetaEntityTests()
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

    private const string IndexJson = """
        { "formatVersion": 1, "packages": [ { "uid": "net.minecraft", "name": "Minecraft", "sha256": "aaa" } ] }
        """;

    private static string Sha256Of(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(_body)),
            });
        }
    }

    private HttpMetaCache NewCache()
    {
        var cache = new HttpMetaCache(Path.Combine(_temp, "index-cache.json"));
        cache.AddBase("meta", MetaDir);
        return cache;
    }

    private MetaEntityLoadTask LoadTask(MetaEntity entity, HttpClient client, NetMode mode = NetMode.Online)
        => entity.CreateLoadTask(client, NewCache(), MetaDir, MetaBaseUrl, mode);

    /// <summary>A handler that holds every request open until all of them have arrived.</summary>
    /// <remarks>
    /// Forces the overlap the race needs. Left to chance, two loads started together usually still
    /// finish one after the other, and the test passes whether or not the bug is present.
    /// </remarks>
    private sealed class RendezvousHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly int _expected;
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public RendezvousHandler(string body, int expected)
        {
            _body = body;
            _expected = expected;
        }

        public int Requests => _arrived;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) >= _expected)
            {
                _allArrived.TrySetResult();
            }

            // Bounded: with the gate in place the second request never arrives, and waiting forever
            // would hang the suite instead of failing it.
            await Task.WhenAny(_allArrived.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken))
                .ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(_body)),
            };
        }
    }

    /*
     * REGRESSION, found end-to-end and only on Windows, only once a cached copy existed.
     *
     * Components update concurrently here (ComponentUpdateTask awaits them with Task.WhenAll) and
     * every one of them wants the same shared index, so two loaders would download and rename the
     * same file at once. The rename fails with access denied, the load reports "failed to validate
     * the response", and resolving the instance dies -- intermittently, which is the worst kind.
     *
     * Upstream never hits this: it is single-threaded around a Qt event loop, so the two loads are
     * serialised for free. Concurrency here is mine, so the exclusion has to be too.
     */
    [Fact]
    public async Task ConcurrentLoadsOfOneEntityDoNotCollide()
    {
        var entity = new Index();
        var handler = new RendezvousHandler(IndexJson, expected: 2);
        using var client = new HttpClient(handler);

        var cache = NewCache();
        var path = Path.Combine(MetaDir, entity.LocalFilename);

        // Both loaders see a document already on disk -- the state the collision needs.
        await File.WriteAllTextAsync(path, IndexJson).ConfigureAwait(true);

        var first = entity.CreateLoadTask(client, cache, MetaDir, MetaBaseUrl).RunAsync();
        var second = entity.CreateLoadTask(client, cache, MetaDir, MetaBaseUrl).RunAsync();

        var results = await Task.WhenAll(first, second).ConfigureAwait(true);

        Assert.All(results, Assert.True);
        Assert.Equal(IndexJson, await File.ReadAllTextAsync(path).ConfigureAwait(true));

        // The second loader finds the document already current and skips the network entirely.
        Assert.Equal(1, handler.Requests);
        Assert.Single(Directory.GetFiles(MetaDir));
    }

    // ================================================================== urls and filenames

    [Fact]
    public void EntityFilenamesFollowTheMetaLayout()
    {
        Assert.Equal("index.json", new Index().LocalFilename);
        Assert.Equal("net.minecraft/index.json", new VersionList("net.minecraft").LocalFilename);
        Assert.Equal("net.minecraft/1.20.1.json", new MetaVersion("net.minecraft", "1.20.1").LocalFilename);
    }

    [Fact]
    public void UrlsResolveAgainstTheMetaBase()
        => Assert.Equal(
            new Uri("https://meta.invalid/v1/net.minecraft/index.json"),
            new VersionList("net.minecraft").GetUrl(MetaBaseUrl));

    // ================================================================== load status

    [Fact]
    public void WithNoExpectedHashOnlyARemoteFetchCountsAsLoaded()
    {
        // The index is the case: nothing vouches for it, so a local copy is never proof of currency.
        var index = new Index { Status = MetaEntity.LoadStatus.Local };
        Assert.False(index.IsLoaded);

        index.Status = MetaEntity.LoadStatus.Remote;
        Assert.True(index.IsLoaded);
    }

    [Fact]
    public void WithAnExpectedHashAnyLoadCountsIfTheHashesAgree()
    {
        var list = new VersionList("net.minecraft")
        {
            Sha256 = "abc",
            FileSha256 = "abc",
            Status = MetaEntity.LoadStatus.Local,
        };

        Assert.True(list.IsLoaded);

        list.FileSha256 = "different";
        Assert.False(list.IsLoaded);
    }

    [Fact]
    public void AVersionNeedsBothItsPatchBodyAndACurrentDocument()
    {
        var version = new MetaVersion("net.minecraft", "1.20.1")
        {
            Sha256 = "abc",
            FileSha256 = "abc",
            Status = MetaEntity.LoadStatus.Local,
        };

        // The document is current, but the patch itself has not been parsed out of it yet.
        Assert.False(version.IsLoaded);

        version.Data = new VersionFile { Uid = "net.minecraft", Version = "1.20.1" };
        Assert.True(version.IsLoaded);

        // And a patch parsed out of a file the index no longer vouches for is not loaded either —
        // without this half, nothing would ever fetch the current copy.
        version.FileSha256 = "stale";
        Assert.False(version.IsLoaded);
    }

    [Fact]
    public void TheStricterVersionCheckIsSeenThroughABaseReference()
    {
        MetaEntity entity = new MetaVersion("net.minecraft", "1.20.1")
        {
            Sha256 = "abc",
            FileSha256 = "abc",
            Status = MetaEntity.LoadStatus.Local,
        };

        // Upstream's is not virtual, so this call site would silently get the looser base answer.
        Assert.False(entity.IsLoaded);
    }

    // ================================================================== loading

    [Fact]
    public async Task DownloadsWhenNothingIsOnDisk()
    {
        var handler = new StubHandler(IndexJson);
        using var client = new HttpClient(handler);

        var index = new Index();

        Assert.True(await LoadTask(index, client).RunAsync());

        Assert.Equal(1, handler.Requests);
        Assert.Equal(MetaEntity.LoadStatus.Remote, index.Status);
        Assert.True(index.HasUid("net.minecraft"));
    }

    [Fact]
    public async Task AGoodLocalCopyWithAMatchingHashSkipsTheNetwork()
    {
        const string body = """
            { "formatVersion": 1, "uid": "net.minecraft", "name": "Minecraft", "versions": [] }
            """;

        Directory.CreateDirectory(Path.Combine(MetaDir, "net.minecraft"));
        File.WriteAllText(Path.Combine(MetaDir, "net.minecraft", "index.json"), body);

        var handler = new StubHandler(body);
        using var client = new HttpClient(handler);

        var list = new VersionList("net.minecraft") { Sha256 = Sha256Of(body) };

        Assert.True(await LoadTask(list, client).RunAsync());

        // The whole point of the hash: no request at all.
        Assert.Equal(0, handler.Requests);
        Assert.Equal(MetaEntity.LoadStatus.Local, list.Status);
    }

    [Fact]
    public async Task AStaleLocalCopyIsDiscardedAndRefetched()
    {
        const string current = """
            { "formatVersion": 1, "uid": "net.minecraft", "name": "Minecraft", "versions": [] }
            """;

        Directory.CreateDirectory(Path.Combine(MetaDir, "net.minecraft"));
        File.WriteAllText(Path.Combine(MetaDir, "net.minecraft", "index.json"), "{ \"stale\": true }");

        var handler = new StubHandler(current);
        using var client = new HttpClient(handler);

        var list = new VersionList("net.minecraft") { Sha256 = Sha256Of(current) };

        Assert.True(await LoadTask(list, client).RunAsync());

        Assert.Equal(1, handler.Requests);
        Assert.Equal(MetaEntity.LoadStatus.Remote, list.Status);
    }

    [Fact]
    public async Task AnUnparseableLocalFileIsDeletedRatherThanRetriedForever()
    {
        var path = Path.Combine(MetaDir, "index.json");
        File.WriteAllText(path, "this is not json");

        var handler = new StubHandler(IndexJson);
        using var client = new HttpClient(handler);

        Assert.True(await LoadTask(new Index(), client).RunAsync());

        // It was replaced by the download, not left to fail again next time.
        Assert.NotEqual("this is not json", File.ReadAllText(path));
    }

    [Fact]
    public async Task AnUnparseableDownloadFailsWithoutOverwritingTheExistingFile()
    {
        // The index carries no expected hash, so it always re-fetches and its local copy is never
        // deleted as stale — which is the case where clobbering would actually be possible.
        var path = Path.Combine(MetaDir, "index.json");
        File.WriteAllText(path, IndexJson);

        var handler = new StubHandler("<html>502 Bad Gateway</html>");
        using var client = new HttpClient(handler);

        Assert.False(await LoadTask(new Index(), client).RunAsync());

        // ParsingValidator rejected the body, so the sink discarded it rather than publishing it over
        // a file that parses.
        Assert.Equal(1, handler.Requests);
        Assert.Equal(IndexJson, File.ReadAllText(path));
    }

    /// <remarks>
    /// Upstream deletes a local file whose hash disagrees with the expected one *before* attempting
    /// the download, so a failed fetch afterwards leaves nothing cached. Surprising, but deliberate:
    /// a file that fails its expected hash is not trustworthy, and keeping it risks launching against
    /// metadata the server has since disowned.
    /// </remarks>
    [Fact]
    public async Task AHashMismatchDiscardsTheLocalFileEvenIfTheDownloadThenFails()
    {
        var path = Path.Combine(MetaDir, "net.minecraft", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "formatVersion": 1, "uid": "net.minecraft", "versions": [] }""");

        var handler = new StubHandler(string.Empty, HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);

        var list = new VersionList("net.minecraft") { Sha256 = Sha256Of("something else entirely") };

        Assert.False(await LoadTask(list, client).RunAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OfflineWithNothingLocalFails()
    {
        var handler = new StubHandler(IndexJson);
        using var client = new HttpClient(handler);

        var task = LoadTask(new Index(), client, NetMode.Offline);

        Assert.False(await task.RunAsync());
        Assert.Equal(0, handler.Requests);
        Assert.Contains("offline", task.FailReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineWithALocalCopyUsesItEvenIfTheHashDisagrees()
    {
        const string body = """
            { "formatVersion": 1, "uid": "net.minecraft", "name": "Minecraft", "versions": [] }
            """;

        Directory.CreateDirectory(Path.Combine(MetaDir, "net.minecraft"));
        File.WriteAllText(Path.Combine(MetaDir, "net.minecraft", "index.json"), body);

        var handler = new StubHandler(body);
        using var client = new HttpClient(handler);

        // Deliberately wrong hash: offline, a stale copy beats no copy.
        var list = new VersionList("net.minecraft") { Sha256 = "not-the-right-hash" };

        Assert.True(await LoadTask(list, client, NetMode.Offline).RunAsync());

        Assert.Equal(0, handler.Requests);
        Assert.Equal(MetaEntity.LoadStatus.Local, list.Status);
    }

    [Fact]
    public async Task ADownloadFailureFailsTheTask()
    {
        var handler = new StubHandler(string.Empty, HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);

        Assert.False(await LoadTask(new Index(), client).RunAsync());
    }

    [Fact]
    public async Task LoadTasksComposeIntoTheTaskTree()
    {
        var handler = new StubHandler(IndexJson);
        using var client = new HttpClient(handler);

        var sequential = new SequentialTask("load meta");
        sequential.AddTask(LoadTask(new Index(), client));

        Assert.True(await sequential.RunAsync());
    }
}
