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
 * Ported from the copy/link half of tests/FileSystem_test.cpp: test_copy, test_copy_with_blacklist,
 * test_copy_with_whitelist, test_copy_with_dot_hidden, test_copy_single_file, test_link,
 * test_hard_link, test_link_with_blacklist, test_link_with_whitelist, test_link_with_dot_hidden,
 * test_link_single_file, test_link_with_max_depth, test_link_with_no_max_depth.
 *
 * Uses the same fixture tree as the Qt suite:
 *     test_folder/.secret_folder/.secret_file.txt
 *     test_folder/assets/minecraft/textures/blah.txt
 *     test_folder/pack.mcmeta
 *     test_folder/pack.nfo
 *
 * Symlink creation on Windows needs Developer Mode or elevation -- that is the entire reason the
 * `filelink` helper exists upstream. Tests that actually create symlinks are therefore gated on
 * capability, while the link-*planning* tests run everywhere via DryRun, which is where the matcher
 * and max-depth logic lives anyway.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Tasks.Tests;

public sealed class CopyLinkTaskTests : IDisposable
{
    private static readonly string Fixture =
        Path.Combine(AppContext.BaseDirectory, "testdata", "FileSystem", "test_folder");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-copylink-" + Guid.NewGuid().ToString("N"));

    // Hard links cannot cross volumes, and the temp folder is not always on the same one as the build
    // output the fixture sits in -- on CI the checkout is on D: while %TEMP% is on C:. So hard-link
    // tests use a scratch dir NEXT TO the fixture, guaranteed to share its volume.
    private readonly string _sameVolumeTemp =
        Path.Combine(AppContext.BaseDirectory, "el-copylink-" + Guid.NewGuid().ToString("N"));

    public CopyLinkTaskTests()
    {
        Directory.CreateDirectory(_temp);
        Directory.CreateDirectory(_sameVolumeTemp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
            Directory.Delete(_sameVolumeTemp, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort; symlinked trees can be awkward to tear down.
        }
    }

    private static bool SymlinksSupported()
    {
        var probe = Path.Combine(Path.GetTempPath(), "el-symlink-probe-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllText(probe + ".target", "x");
            File.CreateSymbolicLink(probe, probe + ".target");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe + ".target");
            }
            catch (IOException)
            {
                // Ignore.
            }
        }
    }

    private string Target(string name) => Path.Combine(_temp, name);

    // ================================================================== copy

    [Fact]
    public async Task CopyReproducesTheWholeTree()
    {
        var target = Target("test_folder");

        Assert.True(await new CopyTask(Fixture, target).RunAsync());

        Assert.True(File.Exists(Path.Combine(target, "pack.mcmeta")));
        Assert.True(File.Exists(Path.Combine(target, "pack.nfo")));
        Assert.True(File.Exists(Path.Combine(target, "assets", "minecraft", "textures", "blah.txt")));
        Assert.True(File.Exists(Path.Combine(target, ".secret_folder", ".secret_file.txt")));
    }

    /// <remarks>Upstream runs each copy case twice: once without a trailing slash, once with.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyWorksWithAndWithoutATrailingSlash(bool trailingSlash)
    {
        var source = trailingSlash ? Fixture + "/" : Fixture;
        var target = Target("test_folder_" + trailingSlash);

        Assert.True(await new CopyTask(source, target).RunAsync());
        Assert.True(File.Exists(Path.Combine(target, "pack.mcmeta")));
    }

    [Fact]
    public async Task CopyWithBlacklistSkipsMatches()
    {
        var target = Target("blacklist");

        var task = new CopyTask(Fixture, target).Matcher(new RegexpMatcher("[.]?mcmeta"));

        Assert.True(await task.RunAsync());

        Assert.False(File.Exists(Path.Combine(target, "pack.mcmeta")));
        Assert.True(Directory.Exists(Path.Combine(target, "assets")));
    }

    [Fact]
    public async Task CopyWithWhitelistKeepsOnlyMatches()
    {
        var target = Target("whitelist");

        var task = new CopyTask(Fixture, target)
            .Matcher(new RegexpMatcher("[.]?mcmeta"))
            .Whitelist(true);

        Assert.True(await task.RunAsync());

        Assert.True(File.Exists(Path.Combine(target, "pack.mcmeta")));
        Assert.False(Directory.Exists(Path.Combine(target, "assets")));
    }

    [Fact]
    public async Task CopyIncludesDotHiddenEntries()
    {
        var target = Target("dothidden");

        Assert.True(await new CopyTask(Fixture, target).RunAsync());

        // QDir::Filter::Hidden upstream; SearchOption.AllDirectories here.
        Assert.True(File.Exists(Path.Combine(target, ".secret_folder", ".secret_file.txt")));
    }

    [Fact]
    public async Task CopySingleFile()
    {
        var target = Path.Combine(Target("single"), "pack.mcmeta");

        Assert.True(await new CopyTask(Path.Combine(Fixture, "pack.mcmeta"), target).RunAsync());

        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task CopyReportsProgressAndCounts()
    {
        var copied = new List<string>();
        var task = new CopyTask(Fixture, Target("counted"));
        task.FileCopied += (_, relative) => copied.Add(relative);

        Assert.True(await task.RunAsync());

        Assert.Equal(4, task.TotalCopied);
        Assert.Equal(4, copied.Count);
        Assert.Equal(0, task.TotalFailed);
    }

    [Fact]
    public async Task CopyDryRunTouchesNothing()
    {
        var target = Target("dryrun");
        var task = new CopyTask(Fixture, target) { DryRun = true };

        Assert.True(await task.RunAsync());

        Assert.Equal(4, task.TotalCopied);
        Assert.False(Directory.Exists(target));
    }

    /// <remarks>
    /// DELIBERATE DIVERGENCE: upstream returns err.value() == 0 where err is reused across the whole
    /// walk, so an early failure followed by a later success reports overall success. Since
    /// FS::moveByCopy() deletes the source when copy() returns true, that can destroy data. Here any
    /// failure fails the task.
    /// </remarks>
    [Fact]
    public async Task CopyFailsWhenAnyFileFailsEvenIfLaterOnesSucceed()
    {
        var target = Target("conflict");

        // Pre-create one destination as a directory so copying a file over it must fail, while the
        // other three files still copy cleanly afterwards.
        Directory.CreateDirectory(Path.Combine(target, "pack.mcmeta"));

        var task = new CopyTask(Fixture, target);

        Assert.False(await task.RunAsync());
        Assert.Equal(TaskState.Failed, task.State);
        Assert.True(task.TotalFailed > 0);
        Assert.True(task.TotalCopied > 0);
    }

    // ================================================================== link planning (no privileges needed)

    [Fact]
    public async Task LinkListCoversEveryFileWhenRecursive()
    {
        var task = new CreateLinkTask(Fixture, Target("links")) { DryRun = true };

        Assert.True(await task.RunAsync());
        Assert.Equal(4, task.TotalToLink);
    }

    [Fact]
    public async Task LinkListRespectsABlacklist()
    {
        var task = new CreateLinkTask(Fixture, Target("links"))
        {
            DryRun = true,
        };
        task.Matcher(new RegexpMatcher("[.]?mcmeta"));

        Assert.True(await task.RunAsync());
        Assert.Equal(3, task.TotalToLink);
    }

    [Fact]
    public async Task LinkListRespectsAWhitelist()
    {
        var task = new CreateLinkTask(Fixture, Target("links")) { DryRun = true };
        task.Matcher(new RegexpMatcher("[.]?mcmeta")).Whitelist(true);

        Assert.True(await task.RunAsync());
        Assert.Equal(1, task.TotalToLink);
    }

    [Fact]
    public async Task LinkListWithMaxDepthZeroCollapsesToTopLevelEntries()
    {
        var task = new CreateLinkTask(Fixture, Target("links")) { DryRun = true };
        task.SetMaxDepth(0);

        Assert.True(await task.RunAsync());

        // .secret_folder and assets collapse to one directory link each; the two top-level files
        // stay as themselves.
        Assert.Equal(4, task.TotalToLink);
    }

    [Fact]
    public async Task LinkListWithNoMaxDepthLinksEveryFileIndividually()
    {
        var task = new CreateLinkTask(Fixture, Target("links")) { DryRun = true };
        task.SetMaxDepth(-1);

        Assert.True(await task.RunAsync());
        Assert.Equal(4, task.TotalToLink);
    }

    [Fact]
    public async Task NonRecursiveLinkPlansASingleEntry()
    {
        var task = new CreateLinkTask(Fixture, Target("links")) { DryRun = true };
        task.LinkRecursively(false);

        Assert.True(await task.RunAsync());
        Assert.Equal(1, task.TotalToLink);
    }

    [Fact]
    public async Task HardLinksForceRecursionBecauseDirectoriesCannotBeHardLinked()
    {
        var task = new CreateLinkTask(Fixture, Target("links")) { DryRun = true };
        task.LinkRecursively(false).UseHardLinks(true);

        Assert.True(await task.RunAsync());

        // LinkRecursively(false) is overridden by UseHardLinks(true).
        Assert.Equal(4, task.TotalToLink);
    }

    // ================================================================== link creation

    [Fact]
    public async Task HardLinkCreatesRealLinks()
    {
        // Same volume as the fixture, because hard links cannot span volumes.
        var target = Path.Combine(_sameVolumeTemp, "hardlinks");
        var task = new CreateLinkTask(Fixture, target).UseHardLinks(true);

        Assert.True(await task.RunAsync());
        Assert.Equal(4, task.TotalLinked);

        var linked = Path.Combine(target, "pack.mcmeta");
        Assert.True(File.Exists(linked));
        Assert.Equal(
            File.ReadAllText(Path.Combine(Fixture, "pack.mcmeta")),
            File.ReadAllText(linked));
    }

    [Fact]
    public async Task HardLinkSharesContentWithItsTarget()
    {
        var source = Path.Combine(_temp, "origin.txt");
        File.WriteAllText(source, "before");

        var target = Path.Combine(_temp, "linked", "origin.txt");
        var task = new CreateLinkTask(source, target).UseHardLinks(true);

        Assert.True(await task.RunAsync());

        // A hard link is the same inode: writing through one is visible through the other.
        File.WriteAllText(source, "after");
        Assert.Equal("after", File.ReadAllText(target));
    }

    /// <remarks>
    /// Reports as SKIPPED, not passed, when the platform will not create symlinks -- a silent
    /// early-return here would look like coverage that does not exist. On Windows without Developer
    /// Mode this is precisely the case upstream hands to the elevated `filelink` helper.
    /// </remarks>
    [SkippableFact]
    public async Task SymlinkCreatesRealLinks()
    {
        Skip.IfNot(SymlinksSupported(), "Symlink creation requires Developer Mode or elevation.");

        var target = Target("symlinks");
        var task = new CreateLinkTask(Fixture, target);

        Assert.True(await task.RunAsync());
        Assert.Equal(4, task.TotalLinked);

        var linked = Path.Combine(target, "pack.mcmeta");
        Assert.NotNull(File.ResolveLinkTarget(linked, returnFinalTarget: false));
    }

    [Fact]
    public async Task LinkFailureStopsAtTheFirstError()
    {
        var target = Target("linkfail");

        // Occupy one destination so the link cannot be created there.
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "pack.mcmeta"), "in the way");
        File.WriteAllText(Path.Combine(target, "pack.nfo"), "in the way");

        var task = new CreateLinkTask(Fixture, target).UseHardLinks(true);

        Assert.False(await task.RunAsync());
        Assert.Equal(TaskState.Failed, task.State);
        Assert.NotEmpty(task.Results);
    }

    // ================================================================== composition with the task tree

    [Fact]
    public async Task CopyTasksComposeIntoASequentialTask()
    {
        var sequential = new SequentialTask();
        sequential.AddTask(new CopyTask(Fixture, Target("seq-a")));
        sequential.AddTask(new CopyTask(Fixture, Target("seq-b")));

        Assert.True(await sequential.RunAsync());

        Assert.True(File.Exists(Path.Combine(Target("seq-a"), "pack.mcmeta")));
        Assert.True(File.Exists(Path.Combine(Target("seq-b"), "pack.mcmeta")));
    }

    [Fact]
    public async Task CopyTaskIsCancellable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var task = new CopyTask(Fixture, Target("cancelled"));

        Assert.False(await task.RunAsync(cts.Token));
        Assert.Equal(TaskState.AbortedByUser, task.State);
    }
}
