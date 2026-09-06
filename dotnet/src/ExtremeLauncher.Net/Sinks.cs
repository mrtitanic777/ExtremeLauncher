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
 * Rewritten from launcher/net/{Sink,Validator,ChecksumValidator,ByteArraySink,FileSink}.h.
 *
 * The upstream Sink/Validator pair is a hand-rolled streaming pipeline: init() prepares, write() is
 * fed each chunk as it arrives, finalize() checks the result, abort() tears down. Validators hang off
 * a sink and see the same chunks. That maps cleanly onto .NET streams and IncrementalHash, so the
 * shape is kept -- it is a good design and the call sites depend on it.
 *
 * Init() takes the outgoing request and Finalize() the response, mirroring the C++. MetaCacheSink is
 * why: it attaches If-None-Match / If-Modified-Since on the way out and harvests ETag, Last-Modified
 * and Cache-Control on the way back. Validators genuinely do not need them, so they keep the narrower
 * signature.
 */

using System.Security.Cryptography;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Net;

/// <summary>Outcome of preparing a sink, mirroring the Task::State the C++ init() returns.</summary>
public enum SinkInitResult
{
    /// <summary>Proceed with the request.</summary>
    Running,

    /// <summary>Local data is already good; skip the request entirely (a cache hit).</summary>
    CacheHit,

    Failed,
}

public interface IValidator
{
    void Init();

    void Write(ReadOnlySpan<byte> data);

    void Abort();

    bool Validate();
}

/// <summary>Hashes the stream as it passes and compares against an expected digest.</summary>
public sealed class ChecksumValidator : IValidator, IDisposable
{
    private readonly HashAlgorithmName _algorithm;

    private IncrementalHash _hash;
    private byte[] _expected;
    private byte[]? _actual;

    public ChecksumValidator(HashAlgorithmName algorithm, byte[]? expected = null)
    {
        _algorithm = algorithm;
        _expected = expected ?? [];
        _hash = IncrementalHash.CreateHash(algorithm);
    }

    public ChecksumValidator(HashAlgorithmName algorithm, string expectedHex)
        : this(algorithm, Convert.FromHexString(expectedHex))
    {
    }

    /// <summary>The digest computed over everything written so far.</summary>
    public byte[] Hash => _actual ??= _hash.GetCurrentHash();

    public void SetExpected(byte[] expected) => _expected = expected;

    public void Init()
    {
        _hash.Dispose();
        _hash = IncrementalHash.CreateHash(_algorithm);
        _actual = null;
    }

    public void Write(ReadOnlySpan<byte> data) => _hash.AppendData(data);

    public void Abort() => Init();

    /// <summary>An empty expected digest means "no expectation", matching upstream.</summary>
    public bool Validate() => _expected.Length == 0 || _expected.AsSpan().SequenceEqual(Hash);

    public void Dispose() => _hash.Dispose();
}

public interface ISink : IDisposable
{
    SinkInitResult Init(HttpRequestMessage request);

    void Write(ReadOnlySpan<byte> data);

    void Abort();

    bool Finalize(HttpResponseMessage response);

    /// <summary>Whether usable local data exists even if the request fails.</summary>
    bool HasLocalData { get; }

    void AddValidator(IValidator validator);
}

public abstract class SinkBase : ISink
{
    private readonly List<IValidator> _validators = [];

    public abstract bool HasLocalData { get; }

    public void AddValidator(IValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validators.Add(validator);
    }

    public abstract SinkInitResult Init(HttpRequestMessage request);

    public abstract void Write(ReadOnlySpan<byte> data);

    public abstract void Abort();

    public abstract bool Finalize(HttpResponseMessage response);

    public virtual void Dispose()
    {
        foreach (var validator in _validators.OfType<IDisposable>())
        {
            validator.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    protected bool InitAllValidators()
    {
        foreach (var validator in _validators)
        {
            validator.Init();
        }

        return true;
    }

    protected bool WriteAllValidators(ReadOnlySpan<byte> data)
    {
        foreach (var validator in _validators)
        {
            validator.Write(data);
        }

        return true;
    }

    protected bool FinalizeAllValidators()
    {
        foreach (var validator in _validators)
        {
            if (!validator.Validate())
            {
                return false;
            }
        }

        return true;
    }

    protected void FailAllValidators()
    {
        foreach (var validator in _validators)
        {
            validator.Abort();
        }
    }
}

/// <summary>Collects the response into memory.</summary>
public sealed class ByteArraySink : SinkBase
{
    private readonly MemoryStream _buffer = new();

    public override bool HasLocalData => false;

    /// <summary>Everything written so far.</summary>
    public byte[] Data => _buffer.ToArray();

    public override SinkInitResult Init(HttpRequestMessage request)
    {
        _buffer.SetLength(0);
        return InitAllValidators() ? SinkInitResult.Running : SinkInitResult.Failed;
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        _buffer.Write(data);
        WriteAllValidators(data);
    }

    public override void Abort()
    {
        _buffer.SetLength(0);
        FailAllValidators();
    }

    public override bool Finalize(HttpResponseMessage response) => FinalizeAllValidators();

    public override void Dispose()
    {
        _buffer.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Streams the response to a file, writing to a temporary path and moving it into place only once
/// every validator is satisfied.
/// </summary>
/// <remarks>
/// Upstream uses QSaveFile for the same reason: a failed checksum must not leave a corrupt file where
/// a good one is expected. The commit uses <see cref="FileSystem.Write"/>'s sibling-temp-then-move
/// approach so the final move stays on one volume.
/// </remarks>
public class FileSink : SinkBase
{
    protected string FilePath { get; }

    private FileStream? _stream;
    private string? _temporaryPath;

    public FileSink(string path) => FilePath = path;

    public override bool HasLocalData => File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;

    public override SinkInitResult Init(HttpRequestMessage request)
    {
        if (!FileSystem.EnsureFilePathExists(FilePath))
        {
            return SinkInitResult.Failed;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? ".";
        _temporaryPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.part");

        try
        {
            _stream = new FileStream(_temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return SinkInitResult.Failed;
        }

        return InitAllValidators() ? SinkInitResult.Running : SinkInitResult.Failed;
    }

    /// <summary>Whether the response carried a body, as opposed to being an empty cache hit.</summary>
    protected bool WroteAnyData { get; private set; }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length != 0)
        {
            WroteAnyData = true;
        }

        _stream?.Write(data);
        WriteAllValidators(data);
    }

    public override void Abort()
    {
        CloseStream();
        FailAllValidators();
        DeleteTemporary();
    }

    public override bool Finalize(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        CloseStream();

        /*
         * A 304 IS A SUCCESS WITH NOTHING IN IT. Upstream spells out the reasoning in FileSink.cpp:
         * validators run "only for actual downloads, not 'your data is still the same' cache hits".
         *
         * Running them anyway is a real failure mode, not a hypothetical: the parse validator that
         * loads metadata sees an empty body, refuses it, and the whole load fails -- but only on the
         * second run, once an ETag has been recorded. The first run always works, so this hides until
         * a cache exists.
         *
         * 203 counts alongside 200 because it means a proxy transformed the body, not that the body
         * is absent.
         */
        var status = (int)response.StatusCode;
        var gotFile = status is 200 or 203;

        // A body without one of those statuses is still a body, and still has to be checked.
        if (!gotFile && !WroteAnyData)
        {
            DeleteTemporary();
            return true;
        }

        if (!FinalizeAllValidators())
        {
            // Checksum mismatch: throw the partial file away rather than publishing it.
            DeleteTemporary();
            return false;
        }

        try
        {
            File.Move(_temporaryPath!, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteTemporary();
            return false;
        }
    }

    public override void Dispose()
    {
        CloseStream();
        base.Dispose();
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private void DeleteTemporary()
    {
        if (_temporaryPath is null)
        {
            return;
        }

        try
        {
            File.Delete(_temporaryPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
