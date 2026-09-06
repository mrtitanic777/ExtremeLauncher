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
 * These matchers were ported early, when nothing in the port consumed them yet — so they went
 * untested, and a bug sat in one of them until FileCopy, FileLink and InstanceCopyTask finally gave
 * them callers. Written now, with the case-sensitivity inversion pinned.
 *
 * What they decide is which files a copy keeps and which it leaves behind, so a matcher that is subtly
 * wrong does not error: it silently copies a user's saves into a fresh instance, or leaves them out of
 * one that asked for them.
 */

using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class PathMatchersTests
{
    // ================================================================== prefix

    [Fact]
    public void APrefixMatcherTakesEverythingBeneathIt()
    {
        var matcher = new SimplePrefixMatcher("instances/");

        Assert.True(matcher.Matches("instances/MyPack/instance.cfg"));
        Assert.True(matcher.Matches("instances/loose.txt"));
        Assert.False(matcher.Matches("libraries/a.jar"));
    }

    /*
     * THE TRAILING SLASH CHANGES THE KIND OF MATCH, not just the pattern. With one it is a prefix;
     * without one it is an EXACT match. That is what makes a whitelist safe to write as a flat list of
     * strings: "instances/" takes a tree, "accounts.json" takes one file, and neither can be confused
     * for a sibling that merely starts with the same letters.
     */
    [Fact]
    public void APrefixNeedsItsTrailingSlashToBeAPrefixAtAll()
    {
        Assert.True(new SimplePrefixMatcher("instances/").Matches("instances/a.cfg"));
        Assert.False(new SimplePrefixMatcher("instances/").Matches("instances-backup/old.cfg"));

        // Without the slash, only the exact path matches -- not even its own children.
        Assert.True(new SimplePrefixMatcher("accounts.json").Matches("accounts.json"));
        Assert.False(new SimplePrefixMatcher("instances").Matches("instances/a.cfg"));
        Assert.False(new SimplePrefixMatcher("instances").Matches("instances-backup/old.cfg"));
    }

    // ================================================================== regexp

    [Fact]
    public void ARegexpMatcherMatchesItsPattern()
    {
        var matcher = new RegexpMatcher("^saves/");

        Assert.True(matcher.Matches("saves/World/level.dat"));
        Assert.False(matcher.Matches("mods/a.jar"));
    }

    /*
     * UPSTREAM BUG #16, fixed. Its caseSensitive(true) sets CaseInsensitiveOption and
     * caseSensitive(false) sets NoPatternOption -- the method does the exact opposite of its name.
     * The one caller, InstanceCopyTask, passes false meaning "match case-insensitively" and therefore
     * got case-SENSITIVE matching, so a folder named "Saves" was not excluded from a copy on Windows
     * or macOS even though the user unticked saves. The name and the call site agreed on the intent;
     * only the implementation disagreed.
     */
    [Fact]
    public void CaseSensitivityFollowsTheNameOfTheSetting()
    {
        Assert.False(new RegexpMatcher("^saves/").CaseSensitive(true).Matches("Saves/World"));
        Assert.True(new RegexpMatcher("^saves/").CaseSensitive(false).Matches("Saves/World"));
    }

    /// <summary>Ignoring case still matches the exact spelling, not just the odd one.</summary>
    [Fact]
    public void IgnoringCaseStillMatchesTheExactSpelling()
        => Assert.True(new RegexpMatcher("^saves/").CaseSensitive(false).Matches("saves/World"));

    /// <summary>Case-sensitive is the default, as the parameter's default value says.</summary>
    [Fact]
    public void MatchingIsCaseSensitiveByDefault()
    {
        Assert.False(new RegexpMatcher("^saves/").Matches("Saves/World"));
        Assert.False(new RegexpMatcher("^saves/").CaseSensitive().Matches("Saves/World"));
    }

    // ================================================================== multi

    [Fact]
    public void AMultiMatcherMatchesIfAnyMemberDoes()
    {
        var matcher = new MultiMatcher();

        matcher.Add(new SimplePrefixMatcher("instances/"));
        matcher.Add(new SimplePrefixMatcher("libraries/"));

        Assert.True(matcher.Matches("instances/a.cfg"));
        Assert.True(matcher.Matches("libraries/a.jar"));
        Assert.False(matcher.Matches("notes.txt"));
    }

    /// <summary>An empty one matches nothing, which is what makes it safe to build up in a loop.</summary>
    [Fact]
    public void AnEmptyMultiMatcherMatchesNothing()
        => Assert.False(new MultiMatcher().Matches("anything"));

    [Fact]
    public void AMultiMatcherCanHoldDifferentKinds()
    {
        var matcher = new MultiMatcher();

        matcher.Add(new SimplePrefixMatcher("instances/"));
        matcher.Add(new RegexpMatcher("[.]cfg$"));

        Assert.True(matcher.Matches("instances/a.txt"));
        Assert.True(matcher.Matches("elsewhere/b.cfg"));
        Assert.False(matcher.Matches("elsewhere/b.txt"));
    }
}
