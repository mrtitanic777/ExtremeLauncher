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
 * The command line is a COMPATIBILITY SURFACE. Desktop entries, Steam shortcuts and batch files across
 * this lineage invoke it as `launcher --launch <id> --server <address>`, and every one of them breaks
 * if an option is renamed or stops taking a value. These pin the surface, short forms included.
 *
 * Upstream has no tests for any of this, because its parsing lives inside the Application constructor
 * and testing it would mean constructing a QApplication. That is precisely why its "--server without
 * --launch" check -- pure logic, one line -- has never been exercised by anything but a user.
 */

using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LauncherArgumentsTests
{
    [Fact]
    public void NoArgumentsAsksForNothing()
    {
        var parsed = LauncherArguments.Parse([]);

        Assert.Null(parsed.DataDirectory);
        Assert.Equal(string.Empty, parsed.InstanceIdToLaunch);
        Assert.Equal(string.Empty, parsed.Error);
        Assert.False(parsed.ShowHelp);
    }

    [Fact]
    public void NullArgumentsAreTreatedAsNone()
        => Assert.Equal(string.Empty, LauncherArguments.Parse(null).Error);

    // ================================================================== the long and short of it

    /// <summary>Both spellings of every option, because scripts in the wild use both.</summary>
    [Theory]
    [InlineData("-l")]
    [InlineData("--launch")]
    public void AnInstanceCanBeGivenByEitherSpelling(string option)
        => Assert.Equal("MyPack", LauncherArguments.Parse([option, "MyPack"]).InstanceIdToLaunch);

    [Theory]
    [InlineData("-d")]
    [InlineData("--dir")]
    public void ADataDirectoryCanBeGivenByEitherSpelling(string option)
        => Assert.Equal("D:/Games/Prism", LauncherArguments.Parse([option, "D:/Games/Prism"]).DataDirectory);

    [Fact]
    public void AFullLaunchLineIsUnderstood()
    {
        var parsed = LauncherArguments.Parse(
            ["--dir", "D:/Prism", "--launch", "MyPack", "--server", "example.com:25566", "--alive"]);

        Assert.Equal("D:/Prism", parsed.DataDirectory);
        Assert.Equal("MyPack", parsed.InstanceIdToLaunch);
        Assert.Equal("example.com:25566", parsed.ServerToJoin);
        Assert.True(parsed.LiveCheck);
        Assert.Equal(string.Empty, parsed.Error);
    }

    [Fact]
    public void AWorldCanBeJoinedInsteadOfAServer()
    {
        var parsed = LauncherArguments.Parse(["-l", "MyPack", "-w", "New World"]);

        Assert.Equal("New World", parsed.WorldToJoin);
        Assert.Equal(string.Empty, parsed.ServerToJoin);
    }

    // ================================================================== upstream's own validation

    /*
     * --server, --world and --profile mean nothing without --launch. Accepting one alone would start
     * the launcher normally while the user sat waiting for a game that was never asked for.
     */
    [Theory]
    [InlineData("--server", "example.com")]
    [InlineData("--world", "New World")]
    [InlineData("--profile", "Steve")]
    public void JoiningSomethingWithoutAnInstanceIsAnError(string option, string value)
    {
        var parsed = LauncherArguments.Parse([option, value]);

        Assert.Contains("--launch", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void JoiningSomethingWithAnInstanceIsFine()
        => Assert.Equal(string.Empty, LauncherArguments.Parse(["--launch", "P", "--server", "e.com"]).Error);

    // ================================================================== importing

    /// <summary>Bare arguments are import URLs — that is what makes a .mrpack double-click work.</summary>
    [Fact]
    public void BareArgumentsAreImportUrls()
    {
        var parsed = LauncherArguments.Parse(["C:/Downloads/pack.mrpack"]);

        Assert.Equal(["C:/Downloads/pack.mrpack"], parsed.UrlsToImport);
    }

    [Fact]
    public void ImportsCanBeGivenExplicitlyAndPositionally()
    {
        var parsed = LauncherArguments.Parse(["-I", "https://example.com/a.mrpack", "b.zip"]);

        Assert.Equal(["https://example.com/a.mrpack", "b.zip"], parsed.UrlsToImport);
    }

    /*
     * AN UNKNOWN OPTION IS AN ERROR, not an import URL. Upstream treats every positional argument as a
     * URL, and a mistyped "--lanuch" would land in that bucket -- silently importing "--lanuch" helps
     * nobody, and the launcher would start normally as though nothing had been asked for.
     */
    [Fact]
    public void AMistypedOptionIsRejectedRatherThanImported()
    {
        var parsed = LauncherArguments.Parse(["--lanuch", "MyPack"]);

        Assert.Contains("--lanuch", parsed.Error, StringComparison.Ordinal);
        Assert.Empty(parsed.UrlsToImport);
    }

    // ================================================================== what this build cannot do

    /*
     * PARSED BUT NOT ACTED ON, and it says so. An option accepted in silence is worse than one
     * rejected: a script passing --profile would appear to work while the game started as somebody
     * else entirely.
     */
    [Fact]
    public void OptionsThisBuildCannotHonourAreNamed()
    {
        var parsed = LauncherArguments.Parse(["--launch", "P", "--profile", "Steve"]);

        Assert.Contains(parsed.Unsupported, u => u.Contains("--profile", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportedOptionsAreNotReportedAsUnsupported()
        => Assert.Empty(LauncherArguments.Parse(["--launch", "P", "--server", "e.com"]).Unsupported);

    // ================================================================== edges

    /// <summary>A value-taking option at the very end takes no value rather than reading past the array.</summary>
    [Fact]
    public void AnOptionWithNoValueAtTheEndDoesNotOverrun()
    {
        var parsed = LauncherArguments.Parse(["--launch"]);

        Assert.Equal(string.Empty, parsed.InstanceIdToLaunch);
        Assert.Equal(string.Empty, parsed.Error);
    }

    [Fact]
    public void HelpAndVersionAreRecognised()
    {
        Assert.True(LauncherArguments.Parse(["--help"]).ShowHelp);
        Assert.True(LauncherArguments.Parse(["-h"]).ShowHelp);
        Assert.True(LauncherArguments.Parse(["--version"]).ShowVersion);
        Assert.True(LauncherArguments.Parse(["-V"]).ShowVersion);
    }

    /// <summary>An instance id may contain spaces, so it must not be split or re-joined.</summary>
    [Fact]
    public void AnInstanceIdKeepsItsSpaces()
        => Assert.Equal("My Old Pack", LauncherArguments.Parse(["--launch", "My Old Pack"]).InstanceIdToLaunch);

    // ================================================================== the metadata server

    /*
     * Not upstream's option, and the reason it exists is worth keeping: BuildConfig points at
     * meta.extremelauncher.net, which does not resolve. Without a way to override it the window fails
     * every resolve with "Some component metadata load tasks failed" and no way for a user to fix it.
     */
    [Fact]
    public void AMetadataServerCanBeGivenForTheRun()
        => Assert.Equal(
            "https://meta.prismlauncher.org/v1/",
            LauncherArguments.Parse(["--meta", "https://meta.prismlauncher.org/v1/"]).MetaUrl);

    [Fact]
    public void WithNoMetaOptionThereIsNoOverride()
        => Assert.Null(LauncherArguments.Parse(["--launch", "P"]).MetaUrl);
}
