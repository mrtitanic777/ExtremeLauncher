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
 * WHAT THESE DO AND DO NOT PROVE.
 *
 * They cover the traversal, the filter, the same-filesystem precondition and the refusal — everything
 * that decides WHETHER a clone happens. They do not cover the clone itself: this port has only ever run
 * on Windows with NTFS, where reflinks do not exist, so the three syscall paths are unexercised code
 * written from platform documentation.
 *
 * The clone test below is skippable and will skip on any machine without a copy-on-write filesystem,
 * which is every machine this has run on so far. That is stated rather than hidden: a green suite here
 * means the refusal works, not that cloning does.
 */

using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class FileCloneTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-clone-" + Guid.NewGuid().ToString("N"));

    private readonly string _source;
    private readonly string _destination;

    public FileCloneTests()
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

    // ================================================================== the precondition

    /*
     * CHECKED ONCE, UP FRONT, rather than per file: a reflink cannot cross filesystems, so an
     * unsuitable pair is unsuitable for every file. Finding out on file one of three hundred is the
     * same answer sooner.
     */
    [Fact]
    public void AnUnsupportedFilesystemIsRefusedBeforeAnythingIsWritten()
    {
        Skip.If(FileSystem.CanClone(_temp, _temp), "This filesystem does support reflinks.");

        Make("mods/a.jar");

        var clone = new FileClone(_source, _destination);

        Assert.False(clone.Run());
        Assert.Equal(0, clone.TotalCloned);

        // Nothing was created, not even partially.
        Assert.False(Directory.Exists(_destination));
    }

    /// <summary>The message has to distinguish "wrong disk" from "wrong filesystem".</summary>
    [Fact]
    public void TheRefusalNamesBothFilesystems()
    {
        Skip.If(FileSystem.CanClone(_temp, _temp), "This filesystem does support reflinks.");

        Make("mods/a.jar");

        var clone = new FileClone(_source, _destination);
        clone.Run();

        Assert.Contains("copy-on-write", clone.FailReason, StringComparison.Ordinal);
        Assert.Contains(FileSystem.StatFs(_temp).FsType.ToString(), clone.FailReason, StringComparison.Ordinal);
    }

    /// <summary>A staging directory is created empty and cloned into, so its parent is what to ask.</summary>
    [Fact]
    public void TheDestinationNeedNotExistYet()
    {
        Assert.False(Directory.Exists(_destination));

        // Whatever the answer, it comes from the nearest existing ancestor rather than throwing.
        var canClone = FileClone.CanCloneBetween(_source, _destination, out var reason);

        Assert.Equal(canClone, reason.Length == 0);
    }

    [Fact]
    public void ADestinationWithNoExistingAncestorIsRefusedClearly()
    {
        var unreachable = OperatingSystem.IsWindows()
            ? @"Q:\no\such\volume\dst"
            : "/no/such/mount/dst";

        Assert.False(FileClone.CanCloneBetween(_source, unreachable, out var reason));
        Assert.NotEqual(string.Empty, reason);
    }

    // ================================================================== the plan

    /*
     * The dry run skips the precondition on purpose: it is how the progress bar gets a denominator,
     * and a caller that has not yet decided between clone, copy and link still needs the count.
     */
    [Fact]
    public void ADryRunCountsEvenWhereCloningIsImpossible()
    {
        Make("mods/a.jar", "mods/b.jar", "options.txt");

        var clone = new FileClone(_source, _destination);

        Assert.True(clone.Run(dryRun: true));
        Assert.Equal(3, clone.TotalCloned);
        Assert.False(Directory.Exists(_destination));
    }

    [Fact]
    public void TheFilterAppliesToThePlan()
    {
        Make("mods/a.jar", "saves/World/level.dat");

        var clone = new FileClone(_source, _destination)
            .Matcher(new RegexpMatcher("^saves/"))
            .Whitelist(false);

        Assert.True(clone.Run(dryRun: true));
        Assert.Equal(1, clone.TotalCloned);
    }

    [Fact]
    public void AWhitelistPlansOnlyWhatItNames()
    {
        Make("mods/a.jar", "saves/World/level.dat");

        var clone = new FileClone(_source, _destination)
            .Matcher(new RegexpMatcher("^saves/"))
            .Whitelist(true);

        clone.Run(dryRun: true);

        Assert.Equal(1, clone.TotalCloned);
    }

    [Fact]
    public void AMissingSourcePlansNothing()
    {
        var clone = new FileClone(Path.Combine(_temp, "nope"), _destination);

        Assert.True(clone.Run(dryRun: true));
        Assert.Equal(0, clone.TotalCloned);
    }

    // ================================================================== the clone itself

    /*
     * SKIPS ON EVERY MACHINE THIS HAS RUN ON, which is the point of writing it as a skippable test
     * rather than not writing it: when someone runs the suite on btrfs, XFS, APFS or ReFS, this is
     * what tells them whether the syscall port is right. Until then the code is unverified and the
     * suite says so instead of implying otherwise.
     */
    [SkippableFact]
    public void CloningSharesBlocksAndStillMakesAnIndependentFile()
    {
        Skip.IfNot(FileSystem.CanClone(_temp, _temp), "No copy-on-write filesystem here.");

        Make("mods/a.jar");

        var clone = new FileClone(_source, _destination);

        Assert.True(clone.Run());
        Assert.Equal(1, clone.TotalCloned);

        var cloned = Path.Combine(_destination, "mods", "a.jar");

        Assert.Equal("content of mods/a.jar", File.ReadAllText(cloned));

        // Independent, unlike a link: writing to the clone must not touch the original.
        File.WriteAllText(cloned, "changed");
        Assert.Equal("content of mods/a.jar", File.ReadAllText(Path.Combine(_source, "mods", "a.jar")));
    }

    /// <summary>Windows ReFS is not implemented; the refusal must say so rather than fail obscurely.</summary>
    [Fact]
    public void CloningOnAnUnimplementedPlatformSaysSo()
    {
        Skip.If(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Implemented on this platform.");

        Make("a.jar");

        var error = Assert.Throws<PlatformNotSupportedException>(
            () => FileClone.CloneFile(Path.Combine(_source, "a.jar"), Path.Combine(_temp, "out.jar")));

        Assert.Contains("copy or a link", error.Message, StringComparison.Ordinal);
    }
}
