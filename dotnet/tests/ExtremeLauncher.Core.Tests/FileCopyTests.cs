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
 * Real files, because the whole point of this class is which ones end up where. The dry run is tested
 * beside the real run for every filtering case: they have to agree exactly, or the progress bar counts
 * a different set from the one being copied and finishes at the wrong number.
 */

using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class FileCopyTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-copy-" + Guid.NewGuid().ToString("N"));

    private readonly string _source;
    private readonly string _destination;

    public FileCopyTests()
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

    private List<string> Copied()
        => !Directory.Exists(_destination)
            ? []
            : [.. Directory.EnumerateFiles(_destination, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_destination, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)];

    // ================================================================== the whole tree

    [Fact]
    public void EverythingIsCopiedWhenThereIsNoFilter()
    {
        Make("options.txt", "mods/a.jar", "config/deep/x.cfg");

        var copy = new FileCopy(_source, _destination);

        Assert.True(copy.Run());
        Assert.Equal(["config/deep/x.cfg", "mods/a.jar", "options.txt"], Copied());
        Assert.Equal(3, copy.TotalCopied);
    }

    [Fact]
    public void FileContentsSurvive()
    {
        Make("mods/a.jar");

        new FileCopy(_source, _destination).Run();

        Assert.Equal("content of mods/a.jar", File.ReadAllText(Path.Combine(_destination, "mods", "a.jar")));
    }

    /*
     * FILES ONLY. Directories come into existence because a file inside them needed a parent, so an
     * EMPTY directory is not copied at all -- upstream's behaviour, and occasionally surprising: an
     * instance with an empty "shaderpacks" folder arrives without one.
     */
    [Fact]
    public void AnEmptyDirectoryIsNotCopied()
    {
        Make("mods/a.jar");
        Directory.CreateDirectory(Path.Combine(_source, "shaderpacks"));

        new FileCopy(_source, _destination).Run();

        Assert.False(Directory.Exists(Path.Combine(_destination, "shaderpacks")));
    }

    /// <summary>Hidden files are included — a .fabric or .index folder is not optional.</summary>
    [Fact]
    public void HiddenFilesAreCopied()
    {
        Make("mods/.index/a.pw.toml");

        new FileCopy(_source, _destination).Run();

        Assert.Contains("mods/.index/a.pw.toml", Copied());
    }

    /// <summary>Upstream's fallback for when the source is one file rather than a tree.</summary>
    [Fact]
    public void ASingleFileSourceIsCopied()
    {
        Make("lonely.txt");

        var copy = new FileCopy(Path.Combine(_source, "lonely.txt"), Path.Combine(_destination, "lonely.txt"));

        Assert.True(copy.Run());
        Assert.Equal(1, copy.TotalCopied);
        Assert.True(File.Exists(Path.Combine(_destination, "lonely.txt")));
    }

    [Fact]
    public void AMissingSourceCopiesNothingAndDoesNotFail()
    {
        var copy = new FileCopy(Path.Combine(_temp, "nope"), _destination);

        Assert.True(copy.Run());
        Assert.Equal(0, copy.TotalCopied);
    }

    // ================================================================== filtering

    /// <summary>A blacklist: what the matcher names is skipped.</summary>
    [Fact]
    public void ABlacklistSkipsWhatItMatches()
    {
        Make("options.txt", "saves/World/level.dat", "mods/a.jar");

        var copy = new FileCopy(_source, _destination)
            .Matcher(new RegexpMatcher("^saves/"))
            .Whitelist(false);

        Assert.True(copy.Run());
        Assert.Equal(["mods/a.jar", "options.txt"], Copied());
    }

    /// <summary>The same matcher, read the other way: only what it names is kept.</summary>
    [Fact]
    public void AWhitelistKeepsOnlyWhatItMatches()
    {
        Make("options.txt", "saves/World/level.dat", "mods/a.jar");

        var copy = new FileCopy(_source, _destination)
            .Matcher(new RegexpMatcher("^saves/"))
            .Whitelist(true);

        Assert.True(copy.Run());
        Assert.Equal(["saves/World/level.dat"], Copied());
    }

    /// <summary>Patterns are written against the tree, not against wherever it happens to live.</summary>
    [Fact]
    public void TheMatcherSeesRelativePaths()
    {
        Make("mods/a.jar");

        var seen = new List<string>();
        var copy = new FileCopy(_source, _destination).Matcher(new RecordingMatcher(seen)).Whitelist(true);

        copy.Run(dryRun: true);

        Assert.Equal(["mods/a.jar"], seen);
    }

    // ================================================================== the dry run

    /*
     * The dry run is how the progress bar gets its denominator, so it has to count EXACTLY the set the
     * real run copies -- filter included. A mismatch leaves the bar finishing at the wrong number.
     */
    [Fact]
    public void TheDryRunCountsWhatTheRealRunCopies()
    {
        Make("options.txt", "saves/World/level.dat", "mods/a.jar", "mods/b.jar");

        var matcher = new RegexpMatcher("^saves/");

        var dry = new FileCopy(_source, _destination).Matcher(matcher).Whitelist(false);
        Assert.True(dry.Run(dryRun: true));

        var real = new FileCopy(_source, _destination).Matcher(matcher).Whitelist(false);
        Assert.True(real.Run());

        Assert.Equal(dry.TotalCopied, real.TotalCopied);
        Assert.Equal(3, real.TotalCopied);
    }

    [Fact]
    public void TheDryRunWritesNothing()
    {
        Make("options.txt");

        Assert.True(new FileCopy(_source, _destination).Run(dryRun: true));
        Assert.False(Directory.Exists(_destination));
    }

    // ================================================================== overwriting and reporting

    [Fact]
    public void OverwriteReplacesAnExistingFile()
    {
        Make("options.txt");

        Directory.CreateDirectory(_destination);
        File.WriteAllText(Path.Combine(_destination, "options.txt"), "old");

        Assert.True(new FileCopy(_source, _destination).Overwrite(true).Run());
        Assert.Equal("content of options.txt", File.ReadAllText(Path.Combine(_destination, "options.txt")));
    }

    /// <summary>Without overwrite, an existing file is a failure rather than a silent skip.</summary>
    [Fact]
    public void WithoutOverwriteAnExistingFileIsReportedFailed()
    {
        Make("options.txt");

        Directory.CreateDirectory(_destination);
        File.WriteAllText(Path.Combine(_destination, "options.txt"), "old");

        var copy = new FileCopy(_source, _destination);

        Assert.False(copy.Run());
        Assert.Single(copy.Failed);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_destination, "options.txt")));
    }

    /*
     * UPSTREAM BUG, fixed. Its return is an error code shared by every iteration and overwritten by
     * each, so it reports only whether the LAST file succeeded -- a run where an early file fails and
     * a later one succeeds returns true, and DataMigrationTask takes that as "everything copied". The
     * failures are recorded either way, which is what makes the wrong answer so quiet.
     */
    [Fact]
    public void AFailureFollowedBySuccessStillReportsFailure()
    {
        Make("a-fails.txt", "z-succeeds.txt");

        Directory.CreateDirectory(_destination);

        // Only the alphabetically first file collides, so a later one succeeds after it.
        File.WriteAllText(Path.Combine(_destination, "a-fails.txt"), "old");

        var copy = new FileCopy(_source, _destination);

        Assert.False(copy.Run());
        Assert.Single(copy.Failed);

        // The later file really did copy -- this is not "it stopped at the failure".
        Assert.True(File.Exists(Path.Combine(_destination, "z-succeeds.txt")));
        Assert.Equal(1, copy.TotalCopied);
    }

    [Fact]
    public void EveryCopiedFileIsReported()
    {
        Make("options.txt", "mods/a.jar");

        var reported = new List<string>();
        var copy = new FileCopy(_source, _destination);

        copy.FileCopied += (_, relative) => reported.Add(relative.Replace('\\', '/'));
        copy.Run();

        Assert.Equal(["mods/a.jar", "options.txt"], reported.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CountersResetBetweenRuns()
    {
        Make("options.txt");

        var copy = new FileCopy(_source, _destination).Overwrite(true);

        copy.Run();
        copy.Run();

        Assert.Equal(1, copy.TotalCopied);
        Assert.Empty(copy.Failed);
    }

    private sealed class RecordingMatcher(List<string> seen) : IPathMatcher
    {
        public bool Matches(string path)
        {
            seen.Add(path);

            return true;
        }
    }
}
