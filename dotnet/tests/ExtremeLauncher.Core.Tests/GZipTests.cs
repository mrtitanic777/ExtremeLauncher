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
 * Ported from tests/GZip_test.cpp.
 */

using System.Text;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class GZipTests
{
    /// <remarks>
    /// Mirrors the upstream test: round-trip random buffers at Fibonacci lengths up to 10 MiB, which
    /// sweeps both tiny inputs and sizes past any internal buffer boundary. The seed is fixed here
    /// (upstream uses std::random_device) so a failure is reproducible.
    /// </remarks>
    [Fact]
    public void RoundTripsRandomBuffersAtFibonacciLengths()
    {
        const int size = 10 * 1024 * 1024;

        var random = new byte[size];
        new Random(Seed: 20240816).NextBytes(random);

        int previous = 1, current = 1;

        do
        {
            var copy = random.AsSpan(0, current).ToArray();

            Assert.True(GZip.TryZip(copy, out var compressed));
            Assert.True(GZip.TryUnzip(compressed, out var decompressed));
            Assert.True(copy.AsSpan().SequenceEqual(decompressed), $"round-trip failed at length {current}");

            (previous, current) = (current, previous + current);
        }
        while (current < size);
    }

    [Fact]
    public void CompressibleDataActuallyShrinks()
    {
        var input = Encoding.UTF8.GetBytes(new string('a', 100_000));

        Assert.True(GZip.TryZip(input, out var compressed));
        Assert.True(compressed.Length < input.Length / 10);

        Assert.True(GZip.TryUnzip(compressed, out var decompressed));
        Assert.Equal(input, decompressed);
    }

    [Fact]
    public void EmptyInputPassesThroughBothWays()
    {
        // Upstream returns the input untouched rather than emitting a valid empty gzip stream.
        Assert.True(GZip.TryZip([], out var compressed));
        Assert.Empty(compressed);

        Assert.True(GZip.TryUnzip([], out var decompressed));
        Assert.Empty(decompressed);
    }

    [Fact]
    public void MalformedInputFailsRatherThanThrowing()
    {
        Assert.False(GZip.TryUnzip(Encoding.UTF8.GetBytes("this is definitely not gzip"), out var output));
        Assert.Empty(output);
    }

    [Fact]
    public void TruncatedStreamFails()
    {
        Assert.True(GZip.TryZip(Encoding.UTF8.GetBytes(new string('x', 10_000)), out var compressed));

        var truncated = compressed.AsSpan(0, compressed.Length / 2).ToArray();

        Assert.False(GZip.TryUnzip(truncated, out _));
    }
}
