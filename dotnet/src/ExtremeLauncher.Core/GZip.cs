// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from launcher/GZip.{h,cpp}.
 *
 * The C++ drives zlib directly with a (16 + MAX_WBITS) window, which selects the gzip wrapper --
 * exactly what System.IO.Compression.GZipStream produces, so the zlib dependency is dropped here.
 * The compressed bytes are not guaranteed identical to zlib's, but no caller compares them: the only
 * users are launcher/minecraft/World.cpp (level.dat) and OtherLogsPage.cpp (gzipped logs), both of
 * which round-trip only.
 */

using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace ExtremeLauncher.Core;

public static class GZip
{
    /// <summary>Decompresses gzip data. Empty input is passed through unchanged, as upstream does.</summary>
    /// <returns><see langword="false"/> if the data is not valid gzip.</returns>
    public static bool TryUnzip(byte[] compressedBytes, out byte[] uncompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(compressedBytes);

        if (compressedBytes.Length == 0)
        {
            uncompressedBytes = compressedBytes;
            return true;
        }

        try
        {
            using var input = new MemoryStream(compressedBytes, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            gzip.CopyTo(output);
            var result = output.ToArray();

            if (!HasValidTrailer(compressedBytes, result))
            {
                uncompressedBytes = [];
                return false;
            }

            uncompressedBytes = result;
            return true;
        }
        catch (InvalidDataException)
        {
            uncompressedBytes = [];
            return false;
        }
        catch (IOException)
        {
            uncompressedBytes = [];
            return false;
        }
    }

    /// <summary>
    /// Verifies the gzip trailer's CRC-32 and ISIZE against what was actually decompressed.
    /// </summary>
    /// <remarks>
    /// Required for parity with the C++. zlib only reports success once it reaches Z_STREAM_END, so a
    /// truncated or corrupt stream makes <c>GZip::unzip</c> return false. GZipStream is more lenient:
    /// when the underlying stream runs out mid-block it reports end-of-data and hands back whatever
    /// it managed to inflate, without throwing. Checking the trailer restores the stricter behaviour.
    ///
    /// This assumes a single-member stream, which is what every caller produces and consumes.
    /// </remarks>
    private static bool HasValidTrailer(byte[] compressed, byte[] uncompressed)
    {
        const int trailerLength = 8;

        if (compressed.Length < trailerLength)
        {
            return false;
        }

        var trailer = compressed.AsSpan(compressed.Length - trailerLength);
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);

        // ISIZE is the uncompressed size modulo 2^32.
        if (expectedSize != (uint)uncompressed.LongLength)
        {
            return false;
        }

        Span<byte> actualCrc = stackalloc byte[4];
        Crc32.Hash(uncompressed, actualCrc);

        return BinaryPrimitives.ReadUInt32LittleEndian(actualCrc) == expectedCrc;
    }

    /// <summary>Compresses to gzip. Empty input is passed through unchanged, as upstream does.</summary>
    public static bool TryZip(byte[] uncompressedBytes, out byte[] compressedBytes)
    {
        ArgumentNullException.ThrowIfNull(uncompressedBytes);

        if (uncompressedBytes.Length == 0)
        {
            compressedBytes = uncompressedBytes;
            return true;
        }

        using var output = new MemoryStream();

        // Scoped so the trailer is flushed before ToArray().
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(uncompressedBytes, 0, uncompressedBytes.Length);
        }

        compressedBytes = output.ToArray();
        return true;
    }
}
