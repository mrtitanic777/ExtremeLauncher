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
 * MOST OF THESE TEST THE PLAN, NOT THE LINKING. The depth limit is the whole design -- it decides
 * whether the copy shares a folder with the original or only shares file contents, which are different
 * products -- and it is entirely decided before a single link is created. It also needs no elevated
 * privilege to check, which the linking itself does on Windows.
 *
 * The tests that really create links are marked skippable for exactly that reason: a symlink needs
 * SeCreateSymbolicLinkPrivilege or developer mode on Windows, and a test suite that fails on an
 * ordinary developer machine gets ignored rather than fixed.
 */

using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class FileLinkTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-link-" + Guid.NewGuid().ToString("N"));

    private readonly string _source;
    private readonly string _destination;

    public FileLinkTests()
    {
        _source = Path.Combine(_temp, "src");
        _destination = Path.Combine(_temp, "dst");

        Directory.CreateDirectory(_source);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private void Make(params string[] relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(_source, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "content of " + relative);
        }
    }

    private int PlanCount(FileLink link)
    {
        Assert.True(link.Run(dryRun: true));

        return link.TotalToLink;
    }

    // ================================================================== the depth limit

    /*
     * AT DEPTH 0 THE FOLDER IS LINKED, NOT ITS CONTENTS: one link instead of three hundred, and adding
     * a mod to the copy adds it to the original too. This is the cheap-and-shared product.
     */
    [Fact]
    public void DepthZeroLinksTheTopLevelEntriesOnce()
    {
        Make("mods/a.jar", "mods/b.jar", "mods/c.jar", "options.txt");

        var link = new FileLink(_source, _destination).LinkRecursively(true).SetMaxDepth(0);

        // "mods" once, plus options.txt -- not three jars.
        Assert.Equal(2, PlanCount(link));
    }

    /// <summary>At unlimited depth every file is linked, so the folders are independent.</summary>
    [Fact]
    public void UnlimitedDepthLinksEveryFile()
    {
        Make("mods/a.jar", "mods/b.jar", "mods/c.jar", "options.txt");

        var link = new FileLink(_source, _destination).LinkRecursively(true).SetMaxDepth(-1);

        Assert.Equal(4, PlanCount(link));
    }

    [Fact]
    public void ADepthLimitAppliesPerLevel()
    {
        Make("config/mod/a.cfg", "config/mod/b.cfg", "config/other/c.cfg");

        // Depth 1 keeps "config/mod" and "config/other" apart but not their contents.
        Assert.Equal(
            2,
            PlanCount(new FileLink(_source, _destination).LinkRecursively(true).SetMaxDepth(1)));

        Assert.Equal(
            1,
            PlanCount(new FileLink(_source, _destination).LinkRecursively(true).SetMaxDepth(0)));
    }

    /*
     * Truncating a deep path yields the same directory once per file inside it, so already-planned
     * sources are skipped. Without the dedup a mods folder of three hundred jars plans three hundred
     * links to the same folder -- and creating the second one fails.
     */
    [Fact]
    public void TruncatedPathsAreDeduplicated()
    {
        Make("mods/a.jar", "mods/b.jar", "mods/sub/c.jar", "mods/sub/d.jar");

        Assert.Equal(1, PlanCount(new FileLink(_source, _destination).LinkRecursively(true).SetMaxDepth(0)));
    }

    /// <summary>Without recursion the whole source is one link, whatever the depth says.</summary>
    [Fact]
    public void WithoutRecursionTheSourceIsLinkedWhole()
    {
        Make("mods/a.jar", "options.txt");

        Assert.Equal(1, PlanCount(new FileLink(_source, _destination).LinkRecursively(false)));
    }

    /*
     * UPSTREAM BUG #15, fixed. It forces recursion on for hard links -- a directory cannot be hard
     * linked -- but leaves the DEPTH LIMIT applying, so a truncated path plans a hard link to a
     * directory, which cannot exist and fails every time. Reachable straight from the UI:
     * InstanceCopyTask passes depth 0 whenever "link recursively" is unticked, so ticking "hard links"
     * without it produces a copy that always fails.
     *
     * Upstream would plan 2 here ("mods" and "options.txt") and fail on the first. Three individual
     * file links is the only plan that can actually succeed.
     */
    [Fact]
    public void HardLinksIgnoreTheDepthLimitBecauseADirectoryCannotBeHardLinked()
    {
        Make("mods/a.jar", "mods/b.jar", "options.txt");

        var link = new FileLink(_source, _destination)
            .UseHardLinks(true)
            .LinkRecursively(false)
            .SetMaxDepth(0);

        Assert.Equal(3, PlanCount(link));
    }

    /// <summary>The same combination, run for real: it has to actually work, not just plan.</summary>
    [SkippableFact]
    public void HardLinkingWithoutRecursionSucceeds()
    {
        Make("mods/a.jar", "options.txt");

        var link = new FileLink(_source, _destination)
            .UseHardLinks(true)
            .LinkRecursively(false)
            .SetMaxDepth(0);

        Skip.IfNot(link.Run(), "Hard links unavailable here: " + link.FailReason);

        Assert.Equal(2, link.TotalLinked);
        Assert.True(File.Exists(Path.Combine(_destination, "mods", "a.jar")));
    }

    // ================================================================== filtering

    [Fact]
    public void ABlacklistedPathIsNotPlanned()
    {
        Make("mods/a.jar", "saves/World/level.dat");

        var link = new FileLink(_source, _destination)
            .LinkRecursively(true)
            .SetMaxDepth(-1)
            .Matcher(new RegexpMatcher("^saves/"))
            .Whitelist(false);

        Assert.Equal(1, PlanCount(link));
    }

    [Fact]
    public void AWhitelistPlansOnlyWhatItNames()
    {
        Make("mods/a.jar", "saves/World/level.dat");

        var link = new FileLink(_source, _destination)
            .LinkRecursively(true)
            .SetMaxDepth(-1)
            .Matcher(new RegexpMatcher("^saves/"))
            .Whitelist(true);

        Assert.Equal(1, PlanCount(link));
    }

    // ================================================================== running

    /// <summary>The plan is what makes the total known before anything happens — no separate pass.</summary>
    [Fact]
    public void ADryRunPlansWithoutCreatingAnything()
    {
        Make("mods/a.jar");

        var link = new FileLink(_source, _destination).LinkRecursively(true).SetMaxDepth(-1);

        Assert.True(link.Run(dryRun: true));
        Assert.Equal(1, link.TotalToLink);
        Assert.Equal(0, link.TotalLinked);
        Assert.False(Directory.Exists(_destination));
    }

    [Fact]
    public void CountersResetBetweenRuns()
    {
        Make("mods/a.jar", "mods/b.jar");

        var link = new FileLink(_source, _destination).LinkRecursively(true).SetMaxDepth(-1);

        link.Run(dryRun: true);
        link.Run(dryRun: true);

        Assert.Equal(2, link.TotalToLink);
    }

    /// <summary>Hard links need no privilege, so this one always runs.</summary>
    [SkippableFact]
    public void HardLinkingSharesTheFileContents()
    {
        Make("mods/a.jar");

        var link = new FileLink(_source, _destination).UseHardLinks(true);

        Skip.IfNot(link.Run(), "Hard links unavailable here: " + link.FailReason);

        var linked = Path.Combine(_destination, "mods", "a.jar");

        Assert.True(File.Exists(linked));
        Assert.Equal(1, link.TotalLinked);

        // The same inode: writing through one is visible through the other.
        File.WriteAllText(Path.Combine(_source, "mods", "a.jar"), "changed");
        Assert.Equal("changed", File.ReadAllText(linked));
    }

    /// <summary>A symlink needs a privilege on Windows, hence skippable rather than failing.</summary>
    [SkippableFact]
    public void SymlinkingADirectoryLinksTheFolderItself()
    {
        Make("mods/a.jar", "mods/b.jar");

        var link = new FileLink(_source, _destination).LinkRecursively(true).SetMaxDepth(0);

        Skip.IfNot(link.Run(), "Symlinks unavailable here: " + link.FailReason);

        var linked = Path.Combine(_destination, "mods");

        Assert.NotNull(new DirectoryInfo(linked).LinkTarget);

        // Through the link, both files are visible -- the folder is shared, not copied.
        Assert.Equal(2, Directory.GetFiles(linked).Length);
    }

    /*
     * FAILS FAST, unlike FileCopy which records every failure and carries on. The usual failure here is
     * a missing privilege, which fails identically for every remaining link -- so continuing would
     * produce hundreds of identical errors and a half-linked instance either way.
     */
    [Fact]
    public void AFailureStopsTheRunAndKeepsItsReason()
    {
        Make("mods/a.jar");

        // The destination already exists as a file, so linking over it cannot succeed.
        Directory.CreateDirectory(Path.Combine(_destination, "mods"));
        File.WriteAllText(Path.Combine(_destination, "mods", "a.jar"), "in the way");

        var link = new FileLink(_source, _destination).UseHardLinks(true);

        Assert.False(link.Run());
        Assert.NotEqual(string.Empty, link.FailReason);
        Assert.Equal(0, link.TotalLinked);
    }
}
