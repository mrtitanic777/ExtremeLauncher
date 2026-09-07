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
 * For JavaArguments.CheckJvmArgs (JavaCommon::checkJVMArgs): the JVM-argument validation the settings
 * pages run. Every refused flag is named, memory is checked ahead of the version pin, and ordinary
 * arguments pass — including the upstream typo "-XX-MaxHeapSize", kept on purpose.
 */

using ExtremeLauncher.Java;
using Xunit;

namespace ExtremeLauncher.Java.Tests;

public sealed class JavaArgumentsTests
{
    [Theory]
    [InlineData("-Xmx4G")]
    [InlineData("-Xms512m")]
    [InlineData("-XX:PermSize=128m")]
    [InlineData("-XX:InitialHeapSize=256m")]
    [InlineData("-XX-MaxHeapSize=2g")] // upstream's typo, matched as written
    [InlineData("-Dsomething=1 -Xmx4G -Dother=2")]
    public void ManualMemoryOptionsAreRefused(string args)
        => Assert.Equal(JvmArgsProblem.ManualMemory, JavaArguments.CheckJvmArgs(args));

    [Fact]
    public void PinningARequiredVersionIsRefused()
        => Assert.Equal(JvmArgsProblem.RequiredVersion, JavaArguments.CheckJvmArgs("-version:1.8+"));

    [Theory]
    [InlineData("")]
    [InlineData("-Dfile.encoding=UTF-8")]
    [InlineData("-XX:+UseG1GC -XX:MaxGCPauseMillis=50")]
    [InlineData("-Dsun.rmi.dgc.server.gcInterval=2147483646")]
    public void OrdinaryArgumentsPass(string args)
    {
        Assert.Equal(JvmArgsProblem.None, JavaArguments.CheckJvmArgs(args));
        Assert.True(JavaArguments.AreJvmArgsSafe(args));
    }

    /// <summary>Memory is checked before the version pin, so both-at-once reports memory.</summary>
    [Fact]
    public void MemoryIsReportedAheadOfTheVersionPin()
        => Assert.Equal(JvmArgsProblem.ManualMemory, JavaArguments.CheckJvmArgs("-Xmx4G -version:1.8+"));

    [Fact]
    public void ARefusedArgumentIsNotSafeAndCarriesAWarning()
    {
        Assert.False(JavaArguments.AreJvmArgsSafe("-Xmx4G"));
        Assert.Contains("Memory group", JavaArguments.WarningFor(JvmArgsProblem.ManualMemory), StringComparison.Ordinal);
        Assert.Contains("not safe", JavaArguments.WarningFor(JvmArgsProblem.RequiredVersion), StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoWarningWhenNothingIsWrong()
        => Assert.Equal(string.Empty, JavaArguments.WarningFor(JvmArgsProblem.None));
}
