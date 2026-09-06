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
 * Two formatters that read alike and answer different questions, so the tests are mostly about where
 * each one stops: a play time never shows seconds once there are hours, and an elapsed time skips
 * whatever is zero rather than padding it.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class TimeFormatTests
{
    // ================================================================== play times

    /// <summary>Always exactly two units, and which two shifts as the duration grows.</summary>
    [Theory]
    [InlineData(0, "0min 0s")]
    [InlineData(45, "0min 45s")]
    [InlineData(90, "1min 30s")]
    [InlineData(3599, "59min 59s")]
    [InlineData(3600, "1h 0min")]
    [InlineData(3661, "1h 1min")]
    [InlineData(86399, "23h 59min")]
    [InlineData(86400, "1d 0h 0min")]
    [InlineData(90061, "1d 1h 1min")]
    public void PlayTimesReportTheTwoLargestUsefulUnits(long seconds, string expected)
        => Assert.Equal(expected, TimeFormat.PrettifyDuration(seconds));

    /// <summary>Seconds vanish once there is an hour — nobody reads "14h 3min 22s" for a play time.</summary>
    [Fact]
    public void SecondsDisappearOnceThereIsAnHour()
    {
        Assert.Contains("s", TimeFormat.PrettifyDuration(59), StringComparison.Ordinal);
        Assert.DoesNotContain("s", TimeFormat.PrettifyDuration(3600).Replace("min", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>With noDays, hours keep counting past 24 instead of rolling over.</summary>
    [Theory]
    [InlineData(86400, "24h 0min")]
    [InlineData(360000, "100h 0min")]
    public void HoursCanRunPastADayWhenAskedTo(long seconds, string expected)
        => Assert.Equal(expected, TimeFormat.PrettifyDuration(seconds, noDays: true));

    // ================================================================== elapsed times

    /*
     * Days, hours and minutes are skipped when zero -- but SECONDS are printed whenever anything
     * larger registered, so "1h 0s" is correct and "1h" is not. Upstream's condition for the seconds
     * field is "did anything at all register", unlike the three above it. My first expectations here
     * were wrong and the code was right.
     */
    [Theory]
    [InlineData(3661, "1h 1m 1s")]
    [InlineData(3600, "1h 0s")]
    [InlineData(3601, "1h 1s")]
    [InlineData(61, "1m 1s")]
    [InlineData(45, "45s")]
    [InlineData(90061, "1days 1h 1m 1s")]
    public void ElapsedTimesSkipZeroUnitsExceptSeconds(double seconds, string expected)
        => Assert.Equal(expected, TimeFormat.HumanReadableDuration(seconds));

    /// <summary>An empty string would read as a missing measurement rather than an instant one.</summary>
    [Fact]
    public void AnInstantDurationStillSaysSomething()
        => Assert.Equal("0ms", TimeFormat.HumanReadableDuration(0));

    /// <summary>Precision only decides whether milliseconds appear at all; see the port note.</summary>
    [Fact]
    public void MillisecondsAppearOnlyWhenPrecisionAllowsThem()
    {
        Assert.Equal("1s", TimeFormat.HumanReadableDuration(1.5));
        Assert.Equal("1s 500ms", TimeFormat.HumanReadableDuration(1.5, precision: 1));
    }

    [Fact]
    public void ADayIsReported()
        => Assert.Equal("1days 1h 0s", TimeFormat.HumanReadableDuration(90000));

    /*
     * DELIBERATE DIVERGENCE: no field padding. Upstream writes through a QTextStream with
     * qSetFieldWidth(2), which in Qt persists until changed -- so it pads the unit letters as well as
     * the numbers, producing runs of stray spaces. Nothing parses this string, so the padding is
     * cosmetic and it was plainly not the intent.
     */
    [Fact]
    public void UnitsAreNotPaddedToAFixedWidth()
        => Assert.Equal("1h 1m 1s", TimeFormat.HumanReadableDuration(3661));

    [Fact]
    public void ANegativeDurationIsSigned()
        => Assert.Equal("-1m 1s", TimeFormat.HumanReadableDuration(-61));
}
