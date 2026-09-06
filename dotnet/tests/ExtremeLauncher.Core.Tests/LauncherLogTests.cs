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
 * A LOG IS ONLY WORTH HAVING IF IT SURVIVES THE CRASH IT DESCRIBES, so the tests that matter are the
 * ones about what is on disk while the process is still running -- not what the object thinks it
 * wrote.
 *
 * The rotation is the other half: five runs, shifted on each start. Walked the wrong way it overwrites
 * each file with the one before it and leaves five copies of the same run, which looks exactly like a
 * working rotation until you read them.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class LauncherLogTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-log-" + Guid.NewGuid().ToString("N"));

    private readonly string _logs;

    public LauncherLogTests()
    {
        Directory.CreateDirectory(_temp);
        _logs = Path.Combine(_temp, "logs");
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

    private string LogPath(int index)
        => Path.Combine(_logs, $"{BuildConfig.Instance.LauncherName}-{index}.log");

    // ================================================================== writing

    [Fact]
    public void LinesAreWrittenWithTheirLevel()
    {
        using (var log = LauncherLog.Open(_temp))
        {
            log.Info("Resolving version");
            log.Warning("Java major version is incompatible");
            log.Error("no compatible Java installation found");
        }

        var text = File.ReadAllText(LogPath(0));

        Assert.Contains("[INFO] Resolving version", text, StringComparison.Ordinal);
        Assert.Contains("[WARN] Java major version is incompatible", text, StringComparison.Ordinal);
        Assert.Contains("[ERROR] no compatible Java installation found", text, StringComparison.Ordinal);
    }

    /*
     * THE POINT OF THE WHOLE CLASS. A launcher that crashes with its last few lines still in a buffer
     * has written a log that stops just before the interesting part -- worse than no log, because it
     * looks complete. Read back WITHOUT disposing, which is the state a crash leaves behind.
     */
    [Fact]
    public void EveryLineIsOnDiskBeforeTheNextOne()
    {
        using var log = LauncherLog.Open(_temp);

        log.Info("first");
        log.Info("second");

        // Not disposed, not flushed by hand: exactly what a process that died here would leave.
        using var reader = new FileStream(LogPath(0), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader);

        var content = text.ReadToEnd();

        Assert.Contains("first", content, StringComparison.Ordinal);
        Assert.Contains("second", content, StringComparison.Ordinal);
    }

    /// <summary>Readable while it is being written, so the Other logs page can show it live.</summary>
    [Fact]
    public void TheLogCanBeReadWhileItIsOpen()
    {
        using var log = LauncherLog.Open(_temp);

        log.Info("hello");

        // No sharing violation: the launcher's own log is one of the files that page lists.
        Assert.Contains("hello", ReadShared(LogPath(0)), StringComparison.Ordinal);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    [Fact]
    public void TheLogSaysWhereItIs()
    {
        using var log = LauncherLog.Open(_temp);

        Assert.True(log.IsWritingToFile);
        Assert.Equal(FileSystem.CleanPath(LogPath(0)), FileSystem.CleanPath(log.Path));
    }

    // ================================================================== rotation

    /*
     * FIVE RUNS, EACH SHIFTED ONE PLACE OLDER. Walked the wrong way, the loop overwrites each file with
     * the one before it and leaves five copies of the newest run -- which looks like a working rotation
     * until somebody reads them.
     */
    [Fact]
    public void EachStartShiftsTheOlderRunsAlong()
    {
        for (var run = 1; run <= 3; run++)
        {
            using var log = LauncherLog.Open(_temp);

            log.Info($"run {run}");
        }

        // Newest first: run 3 is -0, run 2 is -1, run 1 is -2.
        Assert.Contains("run 3", File.ReadAllText(LogPath(0)), StringComparison.Ordinal);
        Assert.Contains("run 2", File.ReadAllText(LogPath(1)), StringComparison.Ordinal);
        Assert.Contains("run 1", File.ReadAllText(LogPath(2)), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyFiveRunsAreKept()
    {
        for (var run = 1; run <= 8; run++)
        {
            using var log = LauncherLog.Open(_temp);

            log.Info($"run {run}");
        }

        Assert.Equal(LauncherLog.KeptRuns, Directory.GetFiles(_logs, "*.log").Length);

        // The oldest kept is run 4; runs 1 to 3 have gone.
        Assert.Contains("run 8", File.ReadAllText(LogPath(0)), StringComparison.Ordinal);
        Assert.Contains("run 4", File.ReadAllText(LogPath(4)), StringComparison.Ordinal);
    }

    /// <summary>A first run has nothing to rotate and must not mind.</summary>
    [Fact]
    public void TheFirstRunRotatesNothing()
    {
        using (var log = LauncherLog.Open(_temp))
        {
            log.Info("hello");
        }

        Assert.Single(Directory.GetFiles(_logs, "*.log"));
    }

    // ================================================================== failing softly

    /*
     * A LOG THAT CANNOT BE OPENED IS NOT FATAL. Upstream refuses to start when the data folder is not
     * writable, which is defensible for it; here the caller may be the CLI pointed at somebody else's
     * read-only install, and refusing to run would be a worse answer than running without a log.
     */
    [Fact]
    public void AnUnwritableFolderStillGivesAWorkingLogger()
    {
        // A FILE where the logs directory should be, so creating the directory cannot succeed.
        File.WriteAllText(_logs, "in the way");

        var console = new StringWriter();

        using var log = LauncherLog.Open(_temp, console);

        log.Info("still running");

        Assert.False(log.IsWritingToFile);
        Assert.Equal(string.Empty, log.Path);

        // ...and it said so, rather than failing silently.
        Assert.Contains("without one", console.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still running", console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WritingAfterDisposeIsHarmless()
    {
        var log = LauncherLog.Open(_temp);

        log.Info("before");
        log.Dispose();

        // Shutdown races are real: a background task finishing after the log closed must not throw.
        log.Info("after");

        var text = File.ReadAllText(LogPath(0));

        Assert.Contains("before", text, StringComparison.Ordinal);
        Assert.DoesNotContain("after", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var log = LauncherLog.Open(_temp);

        log.Dispose();
        log.Dispose();
    }
}
