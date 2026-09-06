// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2023 Rachel Powers <508861+Ryex@users.noreply.github.com>
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
 * Rewritten from launcher/net/{NetRequest,Download,Upload,HeaderProxy,RawHeaderProxy,NetUtils}.*
 *
 * Upstream this is ~470 lines of QNetworkReply signal wiring: executeTask() builds a QNetworkRequest,
 * connects seven signals, and the response is assembled across downloadReadyRead/downloadError/
 * downloadFinished, with hand-written redirect handling because Qt's own was unreliable
 * (QTBUG-41061 is cited in the source). HttpClient follows redirects itself and gives a stream, so
 * nearly all of that disappears -- this file is the whole request layer.
 *
 * Deliberately kept: the sink/validator pipeline, the "cache hit short-circuits the request" path,
 * the AcceptLocalFiles fallback, and the human-readable speed/ETA status text, since the UI reads it.
 */

using System.Diagnostics;
using System.Net;
using ExtremeLauncher.Core;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Net;

[Flags]
public enum NetRequestOptions
{
    None = 0,

    /// <summary>Fall back to whatever the sink already has locally if the request fails.</summary>
    AcceptLocalFiles = 1,

    MakeEternal = 2,
}

public readonly record struct HeaderPair(string Name, string Value);

public interface IHeaderProxy
{
    IReadOnlyList<HeaderPair> Headers(HttpRequestMessage request);
}

/// <summary>A fixed set of headers.</summary>
public sealed class RawHeaderProxy : IHeaderProxy
{
    private readonly List<HeaderPair> _headers = [];

    public RawHeaderProxy()
    {
    }

    public RawHeaderProxy(IEnumerable<HeaderPair> headers) => _headers.AddRange(headers);

    public RawHeaderProxy Add(string name, string value)
    {
        _headers.Add(new HeaderPair(name, value));
        return this;
    }

    public IReadOnlyList<HeaderPair> Headers(HttpRequestMessage request) => _headers;
}

public static class NetUtils
{
    /// <summary>
    /// Whether a status is a server-side problem worth retrying, as opposed to a client mistake.
    /// </summary>
    /// <remarks>Ported from <c>Net::isApplicationError</c>, which enumerates QNetworkReply errors.</remarks>
    public static bool IsApplicationError(HttpStatusCode status)
        => status is HttpStatusCode.InternalServerError
            or HttpStatusCode.NotImplemented
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests;
}

public abstract class NetRequest : LauncherTask
{
    private readonly List<IHeaderProxy> _headerProxies = [];

    private bool _usedLocalFallback;

    /// <summary>Kept so Finalize can see the response headers; only valid inside a run.</summary>
    private HttpResponseMessage? _response;

    protected NetRequest(HttpClient client, Uri url, ISink sink, string name = "") : base(name)
    {
        Client = client;
        Url = url;
        Sink = sink;
    }

    public Uri Url { get; set; }

    public NetRequestOptions Options { get; set; } = NetRequestOptions.None;

    public HttpStatusCode? StatusCode { get; private set; }

    public string ErrorString { get; private set; } = string.Empty;

    public override bool CanAbort => true;

    protected HttpClient Client { get; }

    protected ISink Sink { get; }

    public void AddValidator(IValidator validator) => Sink.AddValidator(validator);

    public void AddHeaderProxy(IHeaderProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _headerProxies.Add(proxy);
    }

    protected abstract HttpRequestMessage CreateRequest();

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus($"Requesting {StringUtils.TruncateUrlHumanFriendly(Url, 80)}");

        _usedLocalFallback = false;
        _response = null;

        // Built once and reused: the sink may attach conditional headers to it during Init.
        using var request = CreateRequest();
        var initResult = Sink.Init(request);

        switch (initResult)
        {
            case SinkInitResult.CacheHit:
                // Local copy is already current: skip the request entirely.
                return;

            case SinkInitResult.Failed:
                throw new TaskFailedException("Failed to initialize sink");

            case SinkInitResult.Running:
                break;

            default:
                throw new TaskFailedException("Failed to initialize sink");
        }

        try
        {
            await SendAndConsumeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Sink.Abort();
            throw;
        }
        catch (HttpRequestException e)
        {
            ErrorString = e.Message;

            // The sink may still hold a usable earlier download.
            if (Options.HasFlag(NetRequestOptions.AcceptLocalFiles) && Sink.HasLocalData)
            {
                _usedLocalFallback = true;
            }
            else
            {
                Sink.Abort();
                throw new TaskFailedException($"Request to {Url} failed: {e.Message}", e);
            }
        }

        if (_usedLocalFallback)
        {
            // Abort, never finalize. Finalizing would commit the empty in-progress file over the
            // local copy we are falling back to -- destroying the very data being preserved.
            Sink.Abort();
            return;
        }

        try
        {
            if (_response is null || !Sink.Finalize(_response))
            {
                // Almost always a checksum mismatch; the sink has already discarded the partial file.
                // The status is worth naming: "it did not validate" reads the same for a checksum
                // mismatch, an unparseable body and an empty cache-hit response, which need very
                // different fixes.
                throw new TaskFailedException(
                    $"Failed to validate the response from {Url}"
                    + (_response is null ? " (no response)" : $" (HTTP {(int)_response.StatusCode})"));
            }
        }
        finally
        {
            _response?.Dispose();
            _response = null;
        }
    }

    private async Task SendAndConsumeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var proxy in _headerProxies)
        {
            foreach (var (name, value) in proxy.Headers(request))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        // NOT disposed here: Finalize() reads ETag / Last-Modified / Cache-Control off it after this
        // method returns. Disposal happens in ExecuteAsync's finally.
        var response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        _response = response;
        StatusCode = response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            ErrorString = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            if (Options.HasFlag(NetRequestOptions.AcceptLocalFiles) && Sink.HasLocalData)
            {
                _usedLocalFallback = true;
                return;
            }

            Sink.Abort();
            throw new TaskFailedException($"Request to {Url} failed: {ErrorString}");
        }

        var total = response.Content.Headers.ContentLength ?? -1;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[81920];
        long received = 0;

        var stopwatch = Stopwatch.StartNew();
        var lastReportAt = TimeSpan.Zero;
        long lastReportBytes = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            Sink.Write(buffer.AsSpan(0, read));
            received += read;

            // Throttled: the upstream version recomputes speed on every readyRead, which for a fast
            // link means formatting strings far more often than any UI can display them.
            var elapsed = stopwatch.Elapsed;

            if ((elapsed - lastReportAt).TotalMilliseconds >= 100)
            {
                ReportProgress(received, total, received - lastReportBytes, elapsed - lastReportAt);
                lastReportAt = elapsed;
                lastReportBytes = received;
            }
        }

        ReportProgress(received, total < 0 ? received : total, 0, TimeSpan.Zero);
    }

    private void ReportProgress(long received, long total, long bytesSince, TimeSpan since)
    {
        var progressText =
            $"{StringUtils.HumanReadableFileSize(received)} / {StringUtils.HumanReadableFileSize(total)}";

        string speedText;

        if (since.TotalMilliseconds > 0)
        {
            var bytesPerSecond = bytesSince / since.TotalSeconds;

            var eta = total > 0 && bytesPerSecond > 0
                ? TimeSpan.FromSeconds((total - received) / bytesPerSecond).ToString(@"hh\:mm\:ss")
                : "unknown";

            speedText = $"{StringUtils.HumanReadableFileSize(bytesPerSecond)} /s ({eta})";
        }
        else
        {
            speedText = "0 B/s";
        }

        SetDetails(progressText + "\n" + speedText);
        SetProgress(received, total);
    }
}

/// <summary>A GET request.</summary>
public sealed class Download : NetRequest
{
    private Download(HttpClient client, Uri url, ISink sink, string name) : base(client, url, sink, name)
    {
    }

    protected override HttpRequestMessage CreateRequest() => new(HttpMethod.Get, Url);

    /// <summary>Downloads into memory. Read the result from the returned sink.</summary>
    public static Download MakeByteArray(HttpClient client, Uri url, out ByteArraySink sink, string name = "")
    {
        sink = new ByteArraySink();
        return new Download(client, url, sink, name);
    }

    /// <summary>Downloads to a file, committed only once validators pass.</summary>
    public static Download MakeFile(HttpClient client, Uri url, string path, string name = "")
        => new(client, url, new FileSink(path), name);

    /// <summary>Downloads through a caller-supplied sink.</summary>
    public static Download Make(HttpClient client, Uri url, ISink sink, string name = "")
        => new(client, url, sink, name);
}

/// <summary>A POST request with a fixed body.</summary>
public sealed class Upload : NetRequest
{
    private readonly byte[] _payload;
    private readonly string _contentType;

    private Upload(HttpClient client, Uri url, ISink sink, byte[] payload, string contentType, string name)
        : base(client, url, sink, name)
    {
        _payload = payload;
        _contentType = contentType;
    }

    protected override HttpRequestMessage CreateRequest()
    {
        var content = new ByteArrayContent(_payload);
        content.Headers.TryAddWithoutValidation("Content-Type", _contentType);

        return new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
    }

    public static Upload MakeByteArray(
        HttpClient client,
        Uri url,
        byte[] payload,
        out ByteArraySink sink,
        string contentType = "application/json",
        string name = "")
    {
        sink = new ByteArraySink();
        return new Upload(client, url, sink, payload, contentType, name);
    }
}
