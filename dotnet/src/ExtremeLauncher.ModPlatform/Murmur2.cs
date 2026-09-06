// SPDX-License-Identifier: GPL-3.0-only
/*
 * MurmurHash2 was written by Austin Appleby and placed in the public domain. The author disclaimed
 * copyright to that source. The incremental modifications in the upstream launcher were likewise
 * placed in the public domain by their author.
 *
 * Ported from libraries/murmur2/src/MurmurHash2.{h,cpp} and the Murmur2 branch of
 * launcher/modplatform/helpers/HashUtils.cpp.
 *
 * THIS IS CURSEFORGE'S FILE FINGERPRINT, and it is not stock MurmurHash2. Three deviations, all
 * load-bearing -- change any one and the fingerprint stops matching what CurseForge has on record,
 * which silently turns "this mod is known" into "this mod is unrecognised":
 *
 *   1. Whitespace is stripped before hashing (tab, LF, CR and space). A jar rebuilt with different
 *      line endings therefore fingerprints the same.
 *   2. The seed is 1, XORed with the FILTERED length. Stock murmur2 seeds with a caller value XORed
 *      with the raw length. This is why the whole input has to be measured before any of it is mixed.
 *   3. The tail is mixed by the same routine as the body, chosen by a remaining-length counter rather
 *      than by position.
 *
 * Two passes are unavoidable because of (2), so the input must be seekable. That is fine for the only
 * caller -- files on disk.
 */

using System.Buffers;
using System.Buffers.Binary;

namespace ExtremeLauncher.ModPlatform;

public static class Murmur2
{
    // Mixing constants from the original. Not magic, just chosen to mix well.
    private const uint M = 0x5bd1e995;
    private const int R = 24;

    /// <summary>4 MiB, upstream's read size.</summary>
    public const int DefaultBufferSize = 4 * 1024 * 1024;

    /// <summary>The bytes CurseForge ignores: tab, line feed, carriage return and space.</summary>
    public static bool IsFilteredOut(byte c) => c is 9 or 10 or 13 or 32;

    /// <summary>Hashes a seekable stream from its current start, leaving it at the end.</summary>
    /// <param name="filterOut">Which bytes to skip. Null hashes every byte.</param>
    public static uint Hash(Stream stream, Func<byte, bool>? filterOut = null, int bufferSize = DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 4);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("The seed depends on the filtered length, so the input must be re-readable.", nameof(stream));
        }

        filterOut ??= IsFilteredOut;

        var origin = stream.Position;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            // Pass one: the length after filtering, which the seed is built from.
            uint size = 0;

            int read;

            while ((read = stream.Read(buffer, 0, bufferSize)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (!filterOut(buffer[i]))
                    {
                        size++;
                    }
                }
            }

            stream.Position = origin;

            // Pass two: the hash itself. The seed of 1 is forced, as upstream's comment says.
            var info = new State { H = 1u ^ size, Length = size };
            var data = new byte[4];
            var index = 0;

            while ((read = stream.Read(buffer, 0, bufferSize)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];

                    if (filterOut(c))
                    {
                        continue;
                    }

                    data[index] = c;
                    index = (index + 1) % 4;

                    // Every whole group of four gets mixed as it completes.
                    if (index == 0)
                    {
                        MixFourBytes(data, ref info);
                    }
                }
            }

            /*
             * One more call, always. With a length that divides by four this finds Length already 0
             * and does nothing but the final avalanche; otherwise it mixes the 1-3 bytes still in the
             * buffer. Those are at data[0..Length-1] because index and the remaining length stay equal
             * -- each in-loop mix consumes exactly the four bytes that advanced index back to zero.
             */
            MixFourBytes(data, ref info);

            return info.H;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Hashes bytes already in memory.</summary>
    public static uint Hash(ReadOnlySpan<byte> data, Func<byte, bool>? filterOut = null)
    {
        filterOut ??= IsFilteredOut;

        uint size = 0;

        foreach (var c in data)
        {
            if (!filterOut(c))
            {
                size++;
            }
        }

        var info = new State { H = 1u ^ size, Length = size };
        var group = new byte[4];
        var index = 0;

        foreach (var c in data)
        {
            if (filterOut(c))
            {
                continue;
            }

            group[index] = c;
            index = (index + 1) % 4;

            if (index == 0)
            {
                MixFourBytes(group, ref info);
            }
        }

        MixFourBytes(group, ref info);

        return info.H;
    }

    /// <summary>The running hash and how much input is still to come.</summary>
    private struct State
    {
        public uint H;

        /// <summary>Counts DOWN. Reaching under four is what selects the final mix.</summary>
        public uint Length;
    }

    /// <summary>
    /// Mixes one group of four bytes, or performs the final mix once fewer than four remain.
    /// </summary>
    /// <remarks>
    /// Unchecked throughout: every step of MurmurHash2 is defined to wrap, and a checked context would
    /// turn the algorithm into a stream of overflow exceptions rather than a hash.
    /// </remarks>
    private static void MixFourBytes(ReadOnlySpan<byte> data, ref State prev)
    {
        unchecked
        {
            if (prev.Length >= 4)
            {
                // Little-endian, matching the reinterpret_cast upstream does on x86 and ARM.
                var k = BinaryPrimitives.ReadUInt32LittleEndian(data);

                k *= M;
                k ^= k >> R;
                k *= M;

                prev.H *= M;
                prev.H ^= k;

                prev.Length -= 4;

                return;
            }

            // The tail. Fall-through is deliberate: three bytes means all three get folded in.
            switch (prev.Length)
            {
                case 3:
                    prev.H ^= (uint)data[2] << 16;
                    goto case 2;

                case 2:
                    prev.H ^= (uint)data[1] << 8;
                    goto case 1;

                case 1:
                    prev.H ^= data[0];
                    prev.H *= M;
                    break;

                default:
                    // Nothing left over. The avalanche below still runs.
                    break;
            }

            // Final avalanche, so the last bytes affect every bit of the result.
            prev.H ^= prev.H >> 13;
            prev.H *= M;
            prev.H ^= prev.H >> 15;

            prev.Length = 0;
        }
    }
}
