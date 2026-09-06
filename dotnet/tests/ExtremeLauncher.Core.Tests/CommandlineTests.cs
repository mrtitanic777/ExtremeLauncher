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
 * Characterization tests for argument splitting. Upstream has no Qt test for it.
 *
 * This function stands between what a user types into the JVM arguments box and what Java is handed,
 * so its edge cases are all reachable by anyone with an opinion about heap size. The surprising ones
 * are pinned deliberately rather than tidied.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class CommandlineTests
{
    // ================================================================== the ordinary cases

    [Fact]
    public void ArgumentsSplitOnSpaces()
        => Assert.Equal(["-Xmx4G", "-XX:+UseG1GC"], Commandline.SplitArgs("-Xmx4G -XX:+UseG1GC"));

    [Fact]
    public void RunsOfSpacesCollapse()
        => Assert.Equal(["a", "b"], Commandline.SplitArgs("   a     b   "));

    [Fact]
    public void AnEmptyStringGivesNoArguments()
    {
        Assert.Empty(Commandline.SplitArgs(string.Empty));
        Assert.Empty(Commandline.SplitArgs("     "));
    }

    // ================================================================== quoting

    [Fact]
    public void QuotesHoldASpaceTogether()
        => Assert.Equal(["-Dfoo=some path", "-Xmx4G"], Commandline.SplitArgs(@"-Dfoo=""some path"" -Xmx4G"));

    [Fact]
    public void TheQuotesThemselvesAreConsumed()
    {
        // This is what makes the function usable for JVM arguments: the quotes are the user's way of
        // saying "this space is part of the value", not something Java should ever see.
        Assert.Equal(["-Dfoo=a b"], Commandline.SplitArgs(@"-Dfoo=""a b"""));
    }

    [Fact]
    public void SingleAndDoubleQuotesBothWork()
    {
        Assert.Equal(["a b"], Commandline.SplitArgs(@"""a b"""));
        Assert.Equal(["a b"], Commandline.SplitArgs("'a b'"));
    }

    [Fact]
    public void AQuoteRunEndsOnlyAtItsOwnCharacter()
    {
        // A double quote inside single quotes is just a character.
        Assert.Equal([@"say ""hi"""], Commandline.SplitArgs(@"'say ""hi""'"));
        Assert.Equal(["it's"], Commandline.SplitArgs(@"""it's"""));
    }

    [Fact]
    public void QuotesCanOpenPartWayThroughAnArgument()
        => Assert.Equal(["--path=/a b/c"], Commandline.SplitArgs(@"--path=""/a b""/c"));

    [Fact]
    public void AnUnterminatedQuoteSwallowsTheRest()
    {
        // Not an error: a user who typed one quote gets one long argument rather than a refusal.
        Assert.Equal(["a b c"], Commandline.SplitArgs(@"""a b c"));
    }

    [Fact]
    public void AnEmptyArgumentCannotBeExpressed()
    {
        // The final append is guarded on the buffer being non-empty, so "" contributes nothing at all.
        // A program that distinguishes an empty argument from an absent one cannot be given one here.
        Assert.Equal(["a", "b"], Commandline.SplitArgs(@"a """" b"));
        Assert.Empty(Commandline.SplitArgs(@""""""));
    }

    // ================================================================== escaping

    [Fact]
    public void ABackslashEscapesInsideQuotes()
    {
        Assert.Equal([@"a""b"], Commandline.SplitArgs(@"""a\""b"""));

        // Single quotes are NOT literal the way POSIX makes them — a backslash escapes in them too.
        Assert.Equal(["a'b"], Commandline.SplitArgs(@"'a\'b'"));
    }

    [Fact]
    public void ABackslashIsOrdinaryOutsideQuotes()
    {
        // Escaping only happens inside a quoted run, so an unquoted Windows path survives intact.
        Assert.Equal([@"C:\Users\bob"], Commandline.SplitArgs(@"C:\Users\bob"));
        Assert.Equal([@"-Dfoo=C:\Program"], Commandline.SplitArgs(@"-Dfoo=C:\Program"));
    }

    [Fact]
    public void QuotingAWindowsPathDestroysIt()
    {
        // THE TRAP: quoting the path to protect a space is exactly what eats the separators. Inherited,
        // and every existing instance.cfg was written against it. Users write forward slashes or double
        // the backslashes.
        Assert.Equal([@"-Dfoo=C:Usersbob"], Commandline.SplitArgs(@"-Dfoo=""C:\Users\bob"""));

        // The two workarounds, both of which do work.
        Assert.Equal([@"-Dfoo=C:/Users/my bob"], Commandline.SplitArgs(@"-Dfoo=""C:/Users/my bob"""));
        Assert.Equal([@"-Dfoo=C:\Users\my bob"], Commandline.SplitArgs(@"-Dfoo=""C:\\Users\\my bob"""));
    }

    [Fact]
    public void AnEscapedBackslashIsOneBackslash()
        => Assert.Equal([@"a\b"], Commandline.SplitArgs(@"""a\\b"""));

    [Fact]
    public void AnEscapeCanProtectASpace()
        => Assert.Equal(["a b"], Commandline.SplitArgs(@"""a\ b"""));

    [Fact]
    public void ATrailingEscapeInsideQuotesIsDropped()
    {
        // The escape flag is still set when the string runs out, so the backslash never lands.
        Assert.Equal(["ab"], Commandline.SplitArgs(@"""ab\"));
    }

    // ================================================================== realistic input

    [Fact]
    public void ATypicalJvmArgumentsBoxSplitsSensibly()
    {
        var args = Commandline.SplitArgs(
            @"-Xmx6G -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -Dlog4j2.formatMsgNoLookups=true");

        Assert.Equal(4, args.Count);
        Assert.Equal("-Xmx6G", args[0]);
        Assert.Equal("-Dlog4j2.formatMsgNoLookups=true", args[3]);
    }

    [Fact]
    public void AWrapperCommandWithAPathSplitsSensibly()
    {
        // The other caller: the wrapper command, where the program's own path may contain spaces.
        var args = Commandline.SplitArgs(@"""/usr/bin/prime run"" --verbose");

        Assert.Equal(["/usr/bin/prime run", "--verbose"], args);
    }
}
