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
 * Characterization tests for the Net rewrite. There is no upstream Qt test for launcher/net.
 *
 * Everything runs against a stub HttpMessageHandler rather than a live server, so the suite is
 * offline, deterministic, and can assert on how many times a URL was actually requested -- which is
 * what makes the NetJob retry behaviour testable at all.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using ExtremeLauncher.Core;
using ExtremeLauncher.Tasks;
using Xunit;

namespace ExtremeLauncher.Net.Tests;

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) => _responder = responder;

    public static StubHandler Ok(byte[] body)
        => new((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    public static StubHandler Status(HttpStatusCode status)
        => new((_, _) => new HttpResponseMessage(status) { Content = new ByteArrayContent([]) });

    public int CountFor(string url)
    {
        lock (_gate)
        {
            return _counts.GetValueOrDefault(url);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int attempt;
        var key = request.RequestUri!.ToString();

        lock (_gate)
        {
            attempt = _counts.GetValueOrDefault(key) + 1;
            _counts[key] = attempt;
        }

        return Task.FromResult(_responder(request, attempt));
    }
}

public sealed class ChecksumValidatorTests
{
    [Fact]
    public void AcceptsAMatchingDigest()
    {
        var payload = Encoding.UTF8.GetBytes("hello world");
        var expected = SHA256.HashData(payload);

        using var validator = new ChecksumValidator(HashAlgorithmName.SHA256, expected);
        validator.Init();
        validator.Write(payload);

        Assert.True(validator.Validate());
    }

    [Fact]
    public void RejectsAMismatchedDigest()
    {
        using var validator = new ChecksumValidator(HashAlgorithmName.SHA256, SHA256.HashData([1, 2, 3]));
        validator.Init();
        validator.Write(Encoding.UTF8.GetBytes("something else"));

        Assert.False(validator.Validate());
    }

    [Fact]
    public void AnEmptyExpectationAcceptsAnything()
    {
        using var validator = new ChecksumValidator(HashAlgorithmName.SHA256);
        validator.Init();
        validator.Write([9, 9, 9]);

        // Upstream: "if (m_expected.size() && ...)" -- no expectation means no check.
        Assert.True(validator.Validate());
    }

    [Fact]
    public void HashesAcrossChunkBoundaries()
    {
        var payload = Encoding.UTF8.GetBytes("streamed in pieces");

        using var validator = new ChecksumValidator(HashAlgorithmName.SHA256);
        validator.Init();
        validator.Write(payload.AsSpan(0, 8));
        validator.Write(payload.AsSpan(8));

        Assert.Equal(SHA256.HashData(payload), validator.Hash);
    }
}

/// <summary>Throwaway request/response pair for exercising sinks without a live transfer.</summary>
internal static class Probe
{
    public static HttpRequestMessage Request => new(HttpMethod.Get, "https://example.invalid/x");

    public static HttpResponseMessage Response => new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
}

public sealed class SinkTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-net-" + Guid.NewGuid().ToString("N"));

    public SinkTests() => Directory.CreateDirectory(_temp);

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

    [Fact]
    public void ByteArraySinkCollectsWrites()
    {
        using var sink = new ByteArraySink();

        Assert.Equal(SinkInitResult.Running, sink.Init(Probe.Request));
        sink.Write(Encoding.UTF8.GetBytes("abc"));
        sink.Write(Encoding.UTF8.GetBytes("def"));

        Assert.True(sink.Finalize(Probe.Response));
        Assert.Equal("abcdef", Encoding.UTF8.GetString(sink.Data));
    }

    [Fact]
    public void FileSinkCommitsOnlyOnFinalize()
    {
        var path = Path.Combine(_temp, "out.bin");

        using var sink = new FileSink(path);
        Assert.Equal(SinkInitResult.Running, sink.Init(Probe.Request));
        sink.Write(Encoding.UTF8.GetBytes("payload"));

        // Still in the temp file: the destination must not appear until it is known good.
        Assert.False(File.Exists(path));

        Assert.True(sink.Finalize(Probe.Response));
        Assert.Equal("payload", File.ReadAllText(path));
    }

    [Fact]
    public void FileSinkDiscardsThePartialFileWhenAValidatorFails()
    {
        var path = Path.Combine(_temp, "bad.bin");

        using var sink = new FileSink(path);
        sink.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA256, SHA256.HashData([1, 2, 3])));

        Assert.Equal(SinkInitResult.Running, sink.Init(Probe.Request));
        sink.Write(Encoding.UTF8.GetBytes("wrong content"));

        Assert.False(sink.Finalize(Probe.Response));

        // The whole point: a bad download must not land where a good one is expected, and must not
        // leave a stray .part file behind either.
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_temp));
    }

    [Fact]
    public void FileSinkAbortLeavesNothingBehind()
    {
        var path = Path.Combine(_temp, "aborted.bin");

        using var sink = new FileSink(path);
        sink.Init(Probe.Request);
        sink.Write(Encoding.UTF8.GetBytes("partial"));
        sink.Abort();

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_temp));
    }

    /*
     * REGRESSION, found end-to-end: metadata loaded on the first run and failed on every run after.
     *
     * Once an ETag is recorded the conditional request comes back 304 with no body. Validators are
     * built for bodies -- the parse validator that metadata loads through cannot parse nothing -- so
     * running them on a cache hit turns a success into a failure. Upstream skips them for exactly
     * this reason (FileSink.cpp).
     */
    [Fact]
    public void FileSinkTreatsAnEmptyNotModifiedAsSuccessAndLeavesTheFileAlone()
    {
        var path = Path.Combine(_temp, "cached.bin");
        File.WriteAllText(path, "the good copy");

        using var sink = new FileSink(path);

        // Would reject anything it was actually given: proof it never ran.
        sink.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA256, SHA256.HashData([1, 2, 3])));

        Assert.Equal(SinkInitResult.Running, sink.Init(Probe.Request));

        using var notModified = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Content = new ByteArrayContent([]),
        };

        Assert.True(sink.Finalize(notModified));

        Assert.Equal("the good copy", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_temp));
    }

    /*
     * The other half of the same rule: a body still gets checked whatever the status says, so a
     * transformed or unusual-status response cannot smuggle unvalidated bytes onto the disk.
     */
    [Fact]
    public void FileSinkStillValidatesABodyThatArrivesWithANonOkStatus()
    {
        var path = Path.Combine(_temp, "unusual.bin");

        using var sink = new FileSink(path);
        sink.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA256, SHA256.HashData([1, 2, 3])));

        Assert.Equal(SinkInitResult.Running, sink.Init(Probe.Request));
        sink.Write(Encoding.UTF8.GetBytes("unexpected content"));

        using var response = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Content = new ByteArrayContent([]),
        };

        Assert.False(sink.Finalize(response));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_temp));
    }

    [Fact]
    public void FileSinkReportsExistingLocalData()
    {
        var path = Path.Combine(_temp, "existing.bin");
        File.WriteAllText(path, "already here");

        using var sink = new FileSink(path);
        Assert.True(sink.HasLocalData);

        using var missing = new FileSink(Path.Combine(_temp, "absent.bin"));
        Assert.False(missing.HasLocalData);
    }
}

public sealed class DownloadTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-net-dl-" + Guid.NewGuid().ToString("N"));

    public DownloadTests() => Directory.CreateDirectory(_temp);

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

    private static readonly Uri Url = new("https://example.invalid/pack/1.0/pack.json");

    [Fact]
    public async Task DownloadsIntoMemory()
    {
        var body = Encoding.UTF8.GetBytes("""{"ok":true}""");
        using var client = new HttpClient(StubHandler.Ok(body));

        var task = Download.MakeByteArray(client, Url, out var sink);

        Assert.True(await task.RunAsync());
        Assert.Equal(body, sink.Data);
        Assert.Equal(HttpStatusCode.OK, task.StatusCode);
    }

    [Fact]
    public async Task DownloadsToAFile()
    {
        var body = Encoding.UTF8.GetBytes("file body");
        using var client = new HttpClient(StubHandler.Ok(body));

        var path = Path.Combine(_temp, "nested", "pack.json");

        Assert.True(await Download.MakeFile(client, Url, path).RunAsync());
        Assert.Equal("file body", File.ReadAllText(path));
    }

    [Fact]
    public async Task FailsOnAnErrorStatus()
    {
        using var client = new HttpClient(StubHandler.Status(HttpStatusCode.NotFound));

        var task = Download.MakeByteArray(client, Url, out _);

        Assert.False(await task.RunAsync());
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("404", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChecksumMismatchFailsTheDownloadAndLeavesNoFile()
    {
        using var client = new HttpClient(StubHandler.Ok(Encoding.UTF8.GetBytes("actual bytes")));

        var path = Path.Combine(_temp, "checked.bin");
        var task = Download.MakeFile(client, Url, path);
        task.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA256, SHA256.HashData([0, 1, 2])));

        Assert.False(await task.RunAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ChecksumMatchSucceeds()
    {
        var body = Encoding.UTF8.GetBytes("actual bytes");
        using var client = new HttpClient(StubHandler.Ok(body));

        var path = Path.Combine(_temp, "checked-ok.bin");
        var task = Download.MakeFile(client, Url, path);
        task.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA256, SHA256.HashData(body)));

        Assert.True(await task.RunAsync());
        Assert.Equal("actual bytes", File.ReadAllText(path));
    }

    [Fact]
    public async Task AcceptLocalFilesFallsBackToAnExistingFile()
    {
        using var client = new HttpClient(StubHandler.Status(HttpStatusCode.ServiceUnavailable));

        var path = Path.Combine(_temp, "cached.bin");
        File.WriteAllText(path, "stale but usable");

        var task = Download.MakeFile(client, Url, path);
        task.Options = NetRequestOptions.AcceptLocalFiles;

        // Upstream behaviour: a failed request is tolerated when the sink already has local data.
        Assert.True(await task.RunAsync());
        Assert.Equal("stale but usable", File.ReadAllText(path));
    }

    [Fact]
    public async Task WithoutAcceptLocalFilesTheSameCaseFails()
    {
        using var client = new HttpClient(StubHandler.Status(HttpStatusCode.ServiceUnavailable));

        var path = Path.Combine(_temp, "cached2.bin");
        File.WriteAllText(path, "stale");

        Assert.False(await Download.MakeFile(client, Url, path).RunAsync());
    }

    [Fact]
    public async Task HeaderProxiesAreApplied()
    {
        string? seen = null;

        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            seen = request.Headers.TryGetValues("X-Api-Key", out var values) ? values.First() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }));

        var task = Download.MakeByteArray(client, Url, out _);
        task.AddHeaderProxy(new RawHeaderProxy().Add("X-Api-Key", "secret"));

        Assert.True(await task.RunAsync());
        Assert.Equal("secret", seen);
    }

    [Fact]
    public async Task ReportsProgressAndStatus()
    {
        using var client = new HttpClient(StubHandler.Ok(new byte[4096]));

        var task = Download.MakeByteArray(client, Url, out _);

        Assert.True(await task.RunAsync());
        Assert.Equal(4096, task.Progress);
        Assert.Contains("example.invalid", task.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationAbortsTheDownload()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var client = new HttpClient(StubHandler.Ok([1, 2, 3]));
        var task = Download.MakeByteArray(client, Url, out _);

        Assert.False(await task.RunAsync(cts.Token));
        Assert.Equal(TaskState.AbortedByUser, task.State);
    }
}

public sealed class NetJobTests
{
    private static Uri UrlFor(int i) => new($"https://example.invalid/file{i}.bin");

    [Fact]
    public async Task RunsEveryRequest()
    {
        using var client = new HttpClient(StubHandler.Ok(Encoding.UTF8.GetBytes("x")));

        var job = new NetJob("batch", client);

        for (var i = 0; i < 5; i++)
        {
            job.AddNetAction(Download.MakeByteArray(client, UrlFor(i), out _));
        }

        Assert.True(await job.RunAsync());
        Assert.Equal(5, job.Size);
        Assert.Empty(job.FailedFiles);
    }

    [Fact]
    public async Task RetriesAFailingRequestAndSucceedsOnTheThirdAttempt()
    {
        // Fails twice, then succeeds -- exactly the window upstream's `m_try < 3` allows.
        var handler = new StubHandler((_, attempt) => attempt < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("ok")) });

        using var client = new HttpClient(handler);

        var job = new NetJob("retry", client);
        job.AddNetAction(Download.MakeByteArray(client, UrlFor(1), out _));

        Assert.True(await job.RunAsync());
        Assert.Equal(3, handler.CountFor(UrlFor(1).ToString()));
        Assert.Equal(3, job.Attempts);
    }

    [Fact]
    public async Task GivesUpAfterThreeAttempts()
    {
        var handler = StubHandler.Status(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);

        var job = new NetJob("give-up", client);
        job.AddNetAction(Download.MakeByteArray(client, UrlFor(2), out _));

        Assert.False(await job.RunAsync());

        // Three attempts total, matching upstream -- not three *retries*.
        Assert.Equal(3, handler.CountFor(UrlFor(2).ToString()));
        Assert.Single(job.FailedFiles);
        Assert.Contains("file2.bin", job.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulRequestIsNotRetriedAlongsideAFailingOne()
    {
        var handler = new StubHandler((request, attempt) =>
            request.RequestUri!.ToString().Contains("file9", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });

        using var client = new HttpClient(handler);

        var job = new NetJob("mixed", client);
        job.AddNetAction(Download.MakeByteArray(client, UrlFor(8), out _));
        job.AddNetAction(Download.MakeByteArray(client, UrlFor(9), out _));

        Assert.False(await job.RunAsync());

        // The good one runs once; only the failure is requeued.
        Assert.Equal(1, handler.CountFor(UrlFor(8).ToString()));
        Assert.Equal(3, handler.CountFor(UrlFor(9).ToString()));
    }
}

public sealed class NetUtilsTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public void ApplicationErrorsAreServerSide(HttpStatusCode status, bool expected)
        => Assert.Equal(expected, NetUtils.IsApplicationError(status));
}

public sealed class TruncateUrlTests
{
    [Fact]
    public void ShortUrlsAreLeftAlone()
    {
        var url = new Uri("https://example.invalid/a/b");
        Assert.Equal("https://example.invalid/a/b", StringUtils.TruncateUrlHumanFriendly(url, 80));
    }

    [Fact]
    public void LongUrlsCollapseTheMiddleAndKeepTheLastSegment()
    {
        var url = new Uri("https://example.invalid/one/two/three/four/five/six/seven/eight/final.jar");

        var result = StringUtils.TruncateUrlHumanFriendly(url, 50);

        Assert.Contains("...", result, StringComparison.Ordinal);
        Assert.EndsWith("final.jar", result, StringComparison.Ordinal);
        Assert.StartsWith("https://example.invalid/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DroppingSegmentsAloneCanBeEnough()
    {
        // A long middle collapses away entirely, landing under the limit without any hard truncation.
        var url = new Uri("https://example.invalid/" + new string('a', 200) + "/final.jar");

        var result = StringUtils.TruncateUrlHumanFriendly(url, 40);

        Assert.Equal("https://example.invalid/.../final.jar", result);
    }

    [Fact]
    public void HardLimitForcesTheLengthDownWhenCollapsingIsNotEnough()
    {
        // Here the *last* segment is the long part, so collapsing the middle cannot help.
        var url = new Uri("https://example.invalid/a/b/" + new string('z', 100) + ".jar");

        var result = StringUtils.TruncateUrlHumanFriendly(url, 40, hardLimit: true);

        // The upstream arithmetic removes (len - max + 3) characters and appends "...", which lands
        // on exactly max_len.
        Assert.Equal(40, result.Length);
        Assert.EndsWith("...", result, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutHardLimitAnUnshortenableUrlStaysLong()
    {
        var url = new Uri("https://example.invalid/a/b/" + new string('z', 100) + ".jar");

        var result = StringUtils.TruncateUrlHumanFriendly(url, 40);

        Assert.True(result.Length > 40);
        Assert.Contains("...", result, StringComparison.Ordinal);
    }
}
