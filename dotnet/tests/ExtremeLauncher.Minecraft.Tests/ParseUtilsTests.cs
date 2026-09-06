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
 * Ported from tests/ParseUtils_test.cpp -- the complete test_Through vector.
 */

using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class ParseUtilsTests
{
    [Theory]
    [InlineData("2016-02-29T13:49:54+01:00")]
    [InlineData("2016-02-26T15:21:11+00:01")]
    [InlineData("2016-02-24T15:52:36+01:13")]
    [InlineData("2016-02-18T17:41:00+00:00")]
    [InlineData("2016-02-17T15:23:19+00:00")]
    [InlineData("2016-02-16T15:22:39+09:22")]
    [InlineData("2016-02-10T15:06:41+00:00")]
    [InlineData("2016-02-04T15:28:02-05:33")]
    public void TimestampsRoundTripExactly(string timestamp)
    {
        var parsed = ParseUtils.TimeFromS3Time(timestamp);

        Assert.Equal(timestamp, ParseUtils.TimeToS3Time(parsed));
    }

    [Fact]
    public void OffsetsArePreservedRatherThanNormalisedToUtc()
    {
        // The point of the whole file: "+09:22" must not come back as "+00:00" with a shifted clock.
        var parsed = ParseUtils.TimeFromS3Time("2016-02-16T15:22:39+09:22");

        Assert.Equal(new TimeSpan(9, 22, 0), parsed.Offset);
        Assert.Equal(15, parsed.Hour);
        Assert.Equal(22, parsed.Minute);
    }

    [Fact]
    public void NegativeOffsetsKeepTheirSign()
    {
        var parsed = ParseUtils.TimeFromS3Time("2016-02-04T15:28:02-05:33");

        Assert.Equal(new TimeSpan(-5, -33, 0), parsed.Offset);
        Assert.EndsWith("-05:33", ParseUtils.TimeToS3Time(parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedTimestampsAreRejected()
    {
        Assert.False(ParseUtils.TryTimeFromS3Time("not a timestamp", out _));
        Assert.True(ParseUtils.TryTimeFromS3Time("2016-02-29T13:49:54+01:00", out _));
    }
}
