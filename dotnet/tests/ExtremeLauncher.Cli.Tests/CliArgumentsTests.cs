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
 * THE ONE PIECE OF LOGIC IN THE CLI. Program.cs is deliberately thin wiring, but pulling the flags
 * out of an argument list -- in any order, without a stray flag becoming the instance id -- is real
 * behaviour with real footguns, so it gets tests. Everything here is side-effect-free: no data
 * directory, no network, no game.
 */

using ExtremeLauncher.Cli;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Cli.Tests;

public sealed class CliArgumentsTests
{
    // ParseLaunchRequest is given the list with the command word still at [0], the way Main passes it;
    // Rest() skips that word.
    private static LaunchRequest Parse(params string[] afterLaunch)
        => Program.ParseLaunchRequest(new List<string>(["launch", .. afterLaunch]), "https://meta.example/");

    // ================================================================== the whole launch line

    [Fact]
    public void TheInstanceIdIsWhatIsLeftAfterTheFlagsAreTakenOut()
    {
        var request = Parse("My Pack", "--offline");

        Assert.Equal("My Pack", request.InstanceId);
        Assert.True(request.Offline);
    }

    [Fact]
    public void FlagsMayComeBeforeOrAfterTheInstanceId()
    {
        // A user types them in whatever order feels natural; the id is whatever is not a flag.
        var request = Parse("--offline", "My", "Pack", "--dry-run");

        Assert.Equal("My Pack", request.InstanceId);
        Assert.True(request.Offline);
        Assert.True(request.DryRun);
    }

    [Fact]
    public void ADryRunFlagIsNeverMistakenForTheInstanceId()
    {
        /*
         * The trap this design exists to avoid: a flag left in the list would be joined into the id.
         * --dry-run is pulled out as the flag it is, and the id is only the real name.
         */
        var request = Parse("MyPack", "--offline", "--dry-run");

        Assert.True(request.DryRun);
        Assert.Equal("MyPack", request.InstanceId);
        Assert.DoesNotContain("--", request.InstanceId, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoNameThePlayerIsPlayer()
    {
        Assert.Equal("Player", Parse("MyPack").PlayerName);
    }

    [Fact]
    public void TheNameOptionSetsThePlayer()
    {
        Assert.Equal("Steve", Parse("MyPack", "--name", "Steve").PlayerName);
    }

    [Fact]
    public void AMissingJavaOverrideIsAnEmptyStringNotNull()
    {
        // LaunchRequest.JavaPath is a non-null string; "not overridden" is empty, not null.
        Assert.Equal(string.Empty, Parse("MyPack").JavaPath);
        Assert.Equal("/opt/jdk/bin/java", Parse("MyPack", "--java", "/opt/jdk/bin/java").JavaPath);
    }

    [Fact]
    public void AServerIsCarried()
    {
        Assert.Equal("play.example.net:25566", Parse("MyPack", "--server", "play.example.net:25566").Server);
    }

    [Fact]
    public void AWorldIsCarried()
    {
        // The new option: a singleplayer world by name, spaces and all.
        Assert.Equal("New World", Parse("MyPack", "--world", "New World").World);
    }

    [Fact]
    public void ServerAndWorldAreBothPassedThroughUntouched()
    {
        /*
         * The CLI does not choose between them -- LauncherService does, server-first. Its job is only
         * to not silently drop one, which an earlier "world instead of server" reading would have.
         */
        var request = Parse("MyPack", "--server", "play.example.net", "--world", "New World");

        Assert.Equal("play.example.net", request.Server);
        Assert.Equal("New World", request.World);
    }

    [Fact]
    public void TheMetaUrlIsCarried()
    {
        Assert.Equal("https://meta.example/", Parse("MyPack").MetaUrl);
    }

    // ================================================================== the helpers themselves

    [Fact]
    public void TakeOptionRemovesThePairAndReturnsTheValue()
    {
        var list = new List<string> { "launch", "MyPack", "--name", "Steve" };

        Assert.Equal("Steve", Program.TakeOption(list, "--name"));
        Assert.Equal(["launch", "MyPack"], list);
    }

    [Fact]
    public void TakeOptionForAnAbsentNameReturnsNullAndChangesNothing()
    {
        var list = new List<string> { "launch", "MyPack" };

        Assert.Null(Program.TakeOption(list, "--name"));
        Assert.Equal(["launch", "MyPack"], list);
    }

    [Fact]
    public void TakeOptionWithNoValueAfterItReturnsNull()
    {
        // "--name" as the very last token has nothing to consume; the guard is index + 1 >= Count.
        var list = new List<string> { "launch", "MyPack", "--name" };

        Assert.Null(Program.TakeOption(list, "--name"));
    }

    [Fact]
    public void TakeFlagRemovesTheFlagAndReportsWhetherItWasThere()
    {
        var list = new List<string> { "launch", "MyPack", "--offline" };

        Assert.True(Program.TakeFlag(list, "--offline"));
        Assert.DoesNotContain("--offline", list);
        Assert.False(Program.TakeFlag(list, "--offline"));
    }

    [Fact]
    public void RestJoinsEverythingAfterTheCommandSoAnIdMayHaveSpaces()
    {
        Assert.Equal("1.20.1 Fabric", Program.Rest(["launch", "1.20.1", "Fabric"]));
    }

    [Fact]
    public void RestOfJustTheCommandIsEmpty()
    {
        Assert.Equal(string.Empty, Program.Rest(["launch"]));
    }
}
