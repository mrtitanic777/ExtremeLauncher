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
 * Ported from launcher/net/MetaCacheSink.{h,cpp}.
 *
 * A FileSink that also drives the HTTP cache: on the way out it turns the request conditional, and on
 * the way back it records what the server said so the next request can be conditional too.
 *
 * Upstream reaches for the global APPLICATION->metacache() in finalizeCache(); the cache is injected
 * here instead, which is what makes it testable without a running application.
 */

using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ExtremeLauncher.Net;

public sealed partial class MetaCacheSink : FileSink
{
    /// <summary>Maximum time to hold a cache entry: one week, in seconds.</summary>
    private const long MaxTimeToExpire = 7 * 24 * 60 * 60;

    private readonly MetaEntry _entry;
    private readonly HttpMetaCache _cache;
    private readonly ChecksumValidator _md5;
    private readonly bool _isEternal;

    private bool _wroteAnyData;

    public MetaCacheSink(MetaEntry entry, HttpMetaCache cache, bool isEternal = false)
        : base(entry.GetFullPath())
    {
        _entry = entry;
        _cache = cache;
        _isEternal = isEternal;

        _md5 = new ChecksumValidator(HashAlgorithmName.MD5);
        AddValidator(_md5);
    }

    public override bool HasLocalData => File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;

    public override SinkInitResult Init(HttpRequestMessage request)
    {
        // Not stale means the cached copy is known good; skip the request entirely.
        if (!_entry.IsStale)
        {
            return SinkInitResult.CacheHit;
        }

        // We have *something* on disk, so ask the server whether it changed rather than re-fetching.
        if (HasLocalData)
        {
            if (_entry.RemoteChangedTimestamp.Length != 0)
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", _entry.RemoteChangedTimestamp);
            }

            if (_entry.ETag.Length != 0)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _entry.ETag);
            }
        }

        return base.Init(request);
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        _wroteAnyData = true;
        base.Write(data);
    }

    public override bool Finalize(HttpResponseMessage response)
    {
        if (!base.Finalize(response))
        {
            return false;
        }

        // Only overwrite the recorded hash if this run actually produced bytes; a 304 leaves the
        // existing file, and its old md5 is still the correct one.
        if (_wroteAnyData)
        {
            _entry.Md5Sum = Convert.ToHexString(_md5.Hash).ToLowerInvariant();
        }

        _entry.ETag = FirstHeader(response, "ETag") ?? string.Empty;

        if (FirstHeader(response, "Last-Modified") is { } lastModified)
        {
            _entry.RemoteChangedTimestamp = lastModified;
        }

        try
        {
            _entry.LocalChangedTimestamp =
                new DateTimeOffset(new FileInfo(FilePath).LastWriteTimeUtc).ToUnixTimeMilliseconds();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _entry.LocalChangedTimestamp = 0;
        }

        ApplyLifetime(response);

        _entry.IsStale = false;
        _cache.UpdateEntry(_entry);

        return true;
    }

    /// <summary>Works out how long this entry may be trusted, preferring the server's own answer.</summary>
    private void ApplyLifetime(HttpResponseMessage response)
    {
        if (_isEternal)
        {
            _entry.IsEternal = true;
        }
        else if (FirstHeader(response, "Cache-Control") is { } cacheControl
                 && MaxAgePattern().Match(cacheControl) is { Success: true } match)
        {
            _entry.MaximumAge = long.TryParse(
                match.Groups[1].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maxAge)
                ? maxAge
                : MaxTimeToExpire;
        }
        else if (FirstHeader(response, "Expires") is { } expires
                 && DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt))
        {
            _entry.MaximumAge = expiresAt.ToUnixTimeSeconds() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        else
        {
            _entry.MaximumAge = MaxTimeToExpire;
        }

        _entry.CurrentAge = FirstHeader(response, "Age") is { } age
                            && long.TryParse(age, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentAge)
            ? currentAge
            : 0;
    }

    private static string? FirstHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }

        return response.Content.Headers.TryGetValues(name, out var contentValues)
            ? contentValues.FirstOrDefault()
            : null;
    }

    [GeneratedRegex("max-age=([0-9]+)")]
    private static partial Regex MaxAgePattern();
}
