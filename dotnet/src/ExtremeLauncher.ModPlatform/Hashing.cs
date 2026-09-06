// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2022 flowln <flowlnlnln@gmail.com>
 *  Copyright (c) 2023 Trial97 <alexandru.tripon97@gmail.com>
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
 * Ported from launcher/modplatform/helpers/HashUtils.{h,cpp}.
 *
 * HOW A LOCAL FILE IS IDENTIFIED TO A PROVIDER. The launcher has a jar on disk and wants to know which
 * project and version it is. Neither API accepts a filename -- names are edited, renamed and
 * duplicated -- so both answer on content instead, and this is what computes the question.
 */

using System.Globalization;
using System.Security.Cryptography;
using ExtremeLauncher.Core;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.ModPlatform;

public enum HashAlgorithm
{
    /// <summary>Present for completeness; see <see cref="Hashing.Hash(Stream, HashAlgorithm)"/>.</summary>
    Md4,

    Md5,
    Sha1,
    Sha256,
    Sha512,

    /// <summary>CurseForge's file fingerprint. See <see cref="Murmur2"/>.</summary>
    Murmur2,

    Unknown,
}

public static class Hashing
{
    /// <summary>The provider's spelling, or "unknown".</summary>
    public static string AlgorithmToString(HashAlgorithm type)
        => type switch
        {
            HashAlgorithm.Md4 => "md4",
            HashAlgorithm.Md5 => "md5",
            HashAlgorithm.Sha1 => "sha1",
            HashAlgorithm.Sha256 => "sha256",
            HashAlgorithm.Sha512 => "sha512",
            HashAlgorithm.Murmur2 => "murmur2",
            _ => "unknown",
        };

    /// <summary>Anything unrecognised is <see cref="HashAlgorithm.Unknown"/>.</summary>
    public static HashAlgorithm AlgorithmFromString(string type)
        => type switch
        {
            "md4" => HashAlgorithm.Md4,
            "md5" => HashAlgorithm.Md5,
            "sha1" => HashAlgorithm.Sha1,
            "sha256" => HashAlgorithm.Sha256,
            "sha512" => HashAlgorithm.Sha512,
            "murmur2" => HashAlgorithm.Murmur2,
            _ => HashAlgorithm.Unknown,
        };

    /// <summary>
    /// The algorithm to identify a local file to this provider.
    /// </summary>
    /// <remarks>
    /// NOT the first entry of <see cref="ProviderCapabilities.HashTypes"/> for CurseForge, and upstream
    /// is right not to use it. That list is what the API will accept when it reports a hash back;
    /// asking "which file is this" goes to the fingerprint endpoint, which only knows murmur2.
    /// Modrinth has no such split, so it does take the first entry -- sha512.
    /// </remarks>
    public static HashAlgorithm AlgorithmFor(ResourceProvider provider)
        => provider switch
        {
            ResourceProvider.Modrinth => AlgorithmFromString(ProviderCapabilities.HashTypes(provider)[0]),
            ResourceProvider.Flame => HashAlgorithm.Murmur2,
            _ => HashAlgorithm.Unknown,
        };

    /// <summary>
    /// Hashes a stream, returning the text form the provider APIs expect.
    /// </summary>
    /// <returns>
    /// Lowercase hex for the cryptographic hashes; a DECIMAL number for murmur2, because that is what
    /// upstream's QString::number produces and what CurseForge's fingerprint endpoint is given. An
    /// empty string for <see cref="HashAlgorithm.Unknown"/>.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// For <see cref="HashAlgorithm.Md4"/>. The BCL has no MD4 and neither provider asks for one --
    /// upstream only lists it because Qt happens to offer it. Refusing beats returning a plausible
    /// hash computed with the wrong algorithm.
    /// </exception>
    public static string Hash(Stream stream, HashAlgorithm type)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (type == HashAlgorithm.Murmur2)
        {
            return Murmur2.Hash(stream).ToString(CultureInfo.InvariantCulture);
        }

        if (type == HashAlgorithm.Unknown)
        {
            return string.Empty;
        }

        if (type == HashAlgorithm.Md4)
        {
            throw new NotSupportedException("MD4 is not available in .NET, and no provider requests it.");
        }

        using var algorithm = Create(type);

        return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Hashes bytes already in memory.</summary>
    public static string Hash(byte[] data, HashAlgorithm type)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var stream = new MemoryStream(data, writable: false);

        return Hash(stream, type);
    }

    /// <summary>Hashes a file on disk.</summary>
    /// <remarks>
    /// Sequential access is a real hint here rather than decoration: murmur2 reads the whole file
    /// twice, and mod jars run to tens of megabytes.
    /// </remarks>
    public static string HashFile(string path, HashAlgorithm type)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        return Hash(stream, type);
    }

    private static System.Security.Cryptography.HashAlgorithm Create(HashAlgorithm type)
        => type switch
        {
            HashAlgorithm.Md5 => MD5.Create(),
            HashAlgorithm.Sha1 => SHA1.Create(),
            HashAlgorithm.Sha256 => SHA256.Create(),
            HashAlgorithm.Sha512 => SHA512.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No such hash algorithm."),
        };
}

/// <summary>Hashes one file as a task, so a UI can show progress and cancel it.</summary>
/// <remarks>
/// Upstream hands the work to QtConcurrent and watches a QFuture. Here it is one Task.Run: hashing a
/// large jar is seconds of CPU, so it must not run on a caller's thread, but it needs no more
/// machinery than that.
/// </remarks>
public sealed class HasherTask : LauncherTask
{
    private readonly HashAlgorithm _algorithm;

    public HasherTask(string path, HashAlgorithm algorithm)
        : base($"Hashing {System.IO.Path.GetFileName(path)}")
    {
        FilePath = path;
        _algorithm = algorithm;
    }

    public HasherTask(string path, string algorithm)
        : this(path, Hashing.AlgorithmFromString(algorithm))
    {
    }

    public HasherTask(string path, ResourceProvider provider)
        : this(path, Hashing.AlgorithmFor(provider))
    {
    }

    public string FilePath { get; }

    /// <summary>The hash, once the task has succeeded.</summary>
    public string Result { get; private set; } = string.Empty;

    public override bool CanAbort => true;

    /// <summary>Raised on success, mirroring upstream's resultsReady signal.</summary>
    public event EventHandler<string>? ResultsReady;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus($"Hashing {System.IO.Path.GetFileName(FilePath)}");

        Result = await Task.Run(() => Hashing.HashFile(FilePath, _algorithm), cancellationToken).ConfigureAwait(false);

        /*
         * An empty result means Unknown was asked for -- there is no file whose sha1 is the empty
         * string. Upstream fails the task on it rather than reporting success with nothing, and a
         * caller that took an empty hash at face value would send a lookup that matches nothing.
         */
        if (Result.Length == 0)
        {
            throw new TaskFailedException("Empty hash!");
        }

        ResultsReady?.Invoke(this, Result);
    }
}
