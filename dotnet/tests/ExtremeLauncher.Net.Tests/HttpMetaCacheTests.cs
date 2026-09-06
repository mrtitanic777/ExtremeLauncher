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
 * Characterization tests for HttpMetaCache and MetaCacheSink. No upstream Qt test exists.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace ExtremeLauncher.Net.Tests;

public sealed class HttpMetaCacheTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-cache-" + Guid.NewGuid().ToString("N"));

    public HttpMetaCacheTests()
    {
        Directory.CreateDirectory(_temp);
        BaseDir = Path.Combine(_temp, "assets");
        Directory.CreateDirectory(BaseDir);
        IndexFile = Path.Combine(_temp, "index.json");
    }

    private string BaseDir { get; }

    private string IndexFile { get; }

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

    private HttpMetaCache NewCache()
    {
        var cache = new HttpMetaCache(IndexFile);
        cache.AddBase("assets", BaseDir);
        return cache;
    }

    [Fact]
    public void UnknownEntriesResolveAsStale()
    {
        var cache = NewCache();

        var entry = cache.ResolveEntry("assets", "objects/ab/abcdef");

        Assert.True(entry.IsStale);
        Assert.Equal("assets", entry.BaseId);
        Assert.Equal(BaseDir, entry.BasePath);
    }

    [Fact]
    public void FullPathCombinesBaseAndRelativePath()
    {
        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "objects/file.bin");

        Assert.Equal(Core.FileSystem.PathCombine(BaseDir, "objects/file.bin"), entry.GetFullPath());
    }

    [Fact]
    public void StaleEntriesAreNeverPersisted()
    {
        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "objects/file.bin");

        Assert.False(cache.UpdateEntry(entry)); // still stale

        cache.SaveNow();

        var reloaded = NewCache();
        reloaded.Load();

        Assert.Null(reloaded.GetEntry("assets", "objects/file.bin"));
    }

    [Fact]
    public void FreshEntriesRoundTripThroughTheIndex()
    {
        var path = Path.Combine(BaseDir, "file.bin");
        File.WriteAllText(path, "cached body");

        var cache = NewCache();

        var entry = cache.ResolveEntry("assets", "file.bin");
        entry.ETag = "\"abc123\"";
        entry.Md5Sum = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        entry.LocalChangedTimestamp = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc).ToUnixTimeMilliseconds();
        entry.MaximumAge = 3600;
        entry.IsStale = false;

        Assert.True(cache.UpdateEntry(entry));

        var reloaded = NewCache();
        reloaded.Load();

        var loaded = reloaded.GetEntry("assets", "file.bin");

        Assert.NotNull(loaded);
        Assert.Equal("\"abc123\"", loaded.ETag);
        Assert.Equal(entry.Md5Sum, loaded.Md5Sum);
        Assert.False(loaded.IsStale);
    }

    [Fact]
    public void AnEntryWhoseFileVanishedGoesStale()
    {
        var path = Path.Combine(BaseDir, "gone.bin");
        File.WriteAllText(path, "here for now");

        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "gone.bin");
        entry.Md5Sum = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        entry.LocalChangedTimestamp = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc).ToUnixTimeMilliseconds();
        entry.MaximumAge = 3600;
        entry.IsStale = false;
        cache.UpdateEntry(entry);

        File.Delete(path);

        Assert.True(cache.ResolveEntry("assets", "gone.bin").IsStale);
    }

    [Fact]
    public void AnEntryEditedBehindOurBackGoesStale()
    {
        var path = Path.Combine(BaseDir, "edited.bin");
        File.WriteAllText(path, "original");

        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "edited.bin");
        entry.Md5Sum = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        entry.LocalChangedTimestamp = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc).ToUnixTimeMilliseconds();
        entry.MaximumAge = 3600;
        entry.IsStale = false;
        cache.UpdateEntry(entry);

        // Different content and a different mtime: the md5 check must catch it.
        File.WriteAllText(path, "tampered with");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.True(cache.ResolveEntry("assets", "edited.bin").IsStale);
    }

    [Fact]
    public void AMismatchedExpectedETagGoesStale()
    {
        var path = Path.Combine(BaseDir, "tagged.bin");
        File.WriteAllText(path, "body");

        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "tagged.bin");
        entry.ETag = "\"one\"";
        entry.Md5Sum = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        entry.LocalChangedTimestamp = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc).ToUnixTimeMilliseconds();
        entry.MaximumAge = 3600;
        entry.IsStale = false;
        cache.UpdateEntry(entry);

        Assert.False(cache.ResolveEntry("assets", "tagged.bin", "\"one\"").IsStale);
        Assert.True(cache.ResolveEntry("assets", "tagged.bin", "\"two\"").IsStale);
    }

    [Fact]
    public void EternalEntriesNeverExpire()
    {
        var entry = new MetaEntry { IsEternal = true, CurrentAge = long.MaxValue / 2, MaximumAge = 0 };

        Assert.False(entry.IsExpired(0));
    }

    [Fact]
    public void EntriesPastTheirMaximumAgeExpire()
    {
        var entry = new MetaEntry { CurrentAge = 100, MaximumAge = 50 };

        Assert.True(entry.IsExpired(0));
        Assert.False(new MetaEntry { CurrentAge = 10, MaximumAge = 50 }.IsExpired(0));
    }

    [Fact]
    public void EntriesForUnregisteredBasesAreRejected()
    {
        var cache = NewCache();

        Assert.False(cache.UpdateEntry(new MetaEntry { BaseId = "nope", IsStale = false }));
        Assert.Equal(string.Empty, cache.GetBasePath("nope"));
    }
}

public sealed class MetaCacheSinkTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-cachesink-" + Guid.NewGuid().ToString("N"));

    public MetaCacheSinkTests()
    {
        Directory.CreateDirectory(_temp);
        BaseDir = Path.Combine(_temp, "assets");
        Directory.CreateDirectory(BaseDir);
    }

    private string BaseDir { get; }

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

    private HttpMetaCache NewCache()
    {
        var cache = new HttpMetaCache(Path.Combine(_temp, "index.json"));
        cache.AddBase("assets", BaseDir);
        return cache;
    }

    [Fact]
    public void AFreshEntryShortCircuitsNothing()
    {
        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "new.bin");

        using var sink = new MetaCacheSink(entry, cache);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/new.bin");

        // Stale entry with no local file: a normal request.
        Assert.Equal(SinkInitResult.Running, sink.Init(request));
        Assert.False(request.Headers.Contains("If-None-Match"));
    }

    [Fact]
    public void ANonStaleEntryIsACacheHit()
    {
        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "hit.bin");
        entry.IsStale = false;

        using var sink = new MetaCacheSink(entry, cache);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/hit.bin");

        Assert.Equal(SinkInitResult.CacheHit, sink.Init(request));
    }

    [Fact]
    public void AnExistingFileMakesTheRequestConditional()
    {
        var cache = NewCache();
        File.WriteAllText(Path.Combine(BaseDir, "cond.bin"), "old body");

        var entry = cache.ResolveEntry("assets", "cond.bin");
        entry.ETag = "\"v1\"";
        entry.RemoteChangedTimestamp = "Wed, 21 Oct 2015 07:28:00 GMT";
        entry.IsStale = true;

        using var sink = new MetaCacheSink(entry, cache);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/cond.bin");

        Assert.Equal(SinkInitResult.Running, sink.Init(request));

        Assert.Equal("\"v1\"", request.Headers.GetValues("If-None-Match").Single());
        Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", request.Headers.GetValues("If-Modified-Since").Single());
    }

    [Fact]
    public void FinalizeRecordsTheServerMetadata()
    {
        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "recorded.bin");

        using var sink = new MetaCacheSink(entry, cache);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/recorded.bin");

        Assert.Equal(SinkInitResult.Running, sink.Init(request));

        var body = Encoding.UTF8.GetBytes("downloaded body");
        sink.Write(body);

        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("ETag", "\"v2\"");
        response.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=1800");
        response.Headers.TryAddWithoutValidation("Age", "42");

        // Last-Modified and Expires are ENTITY headers: .NET models them on Content.Headers, and
        // adding them to Headers is silently dropped. This is why MetaCacheSink.FirstHeader looks in
        // both collections rather than just response.Headers.
        response.Content.Headers.TryAddWithoutValidation("Last-Modified", "Wed, 21 Oct 2015 07:28:00 GMT");

        Assert.True(sink.Finalize(response));

        Assert.Equal("\"v2\"", entry.ETag);
        Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", entry.RemoteChangedTimestamp);
        Assert.Equal(1800, entry.MaximumAge);
        Assert.Equal(42, entry.CurrentAge);
        Assert.False(entry.IsStale);

        // The md5 is recorded from what actually went through the sink.
        Assert.Equal(Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant(), entry.Md5Sum);

        // ...and the entry is now filed in the cache.
        Assert.NotNull(cache.GetEntry("assets", "recorded.bin"));
    }

    [Fact]
    public void WithoutCacheHeadersTheEntryGetsTheOneWeekDefault()
    {
        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "defaulted.bin");

        using var sink = new MetaCacheSink(entry, cache);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/defaulted.bin");

        sink.Init(request);
        sink.Write(Encoding.UTF8.GetBytes("x"));

        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };

        Assert.True(sink.Finalize(response));
        Assert.Equal(7 * 24 * 60 * 60, entry.MaximumAge);
        Assert.Equal(0, entry.CurrentAge);
    }

    [Fact]
    public void EternalSinksMarkTheEntryEternal()
    {
        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "eternal.bin");

        using var sink = new MetaCacheSink(entry, cache, isEternal: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/eternal.bin");

        sink.Init(request);
        sink.Write(Encoding.UTF8.GetBytes("x"));

        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=10");

        Assert.True(sink.Finalize(response));

        // Eternal wins over whatever the server said.
        Assert.True(entry.IsEternal);
    }

    [Fact]
    public async Task ACacheHitSkipsTheRequestEntirely()
    {
        var cache = NewCache();
        var entry = cache.ResolveEntry("assets", "skipped.bin");
        entry.IsStale = false;

        var handler = StubHandler.Ok(Encoding.UTF8.GetBytes("should never be fetched"));
        using var client = new HttpClient(handler);

        var url = new Uri("https://example.invalid/skipped.bin");
        var task = Download.Make(client, url, new MetaCacheSink(entry, cache));

        Assert.True(await task.RunAsync());

        // The whole point of the cache: no request was made.
        Assert.Equal(0, handler.CountFor(url.ToString()));
    }
}
