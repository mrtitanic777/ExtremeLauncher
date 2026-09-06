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
 * THE ASSERTIONS ARE ON THE FILE, not on a recording object. That is the lesson from the settings
 * page, where reading back through the same abstraction that wrote the value tested the abstraction
 * and hid a real bug for a whole round of work. A log's entire purpose is to be a file somebody else
 * opens.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LoggingLaunchReporterTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-lrep-" + Guid.NewGuid().ToString("N"));

    public LoggingLaunchReporterTests() => Directory.CreateDirectory(_temp);

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

    private string LogText()
        => File.ReadAllText(Path.Combine(_temp, "logs", $"{BuildConfig.Instance.LauncherName}-0.log"));

    /// <summary>Records what still reached the front-end.</summary>
    private sealed class Recording : ILaunchReporter
    {
        public List<string> Statuses { get; } = [];

        public List<(long Current, long Total)> Progresses { get; } = [];

        public List<(string Text, bool IsError)> Lines { get; } = [];

        public void Status(string status) => Statuses.Add(status);

        public void Progress(long current, long total) => Progresses.Add((current, total));

        public void Line(string text, bool isError = false) => Lines.Add((text, isError));
    }

    // ================================================================== what reaches the file

    [Fact]
    public void StatusesAndLinesAreWrittenToTheLog()
    {
        var inner = new Recording();

        using (var log = LauncherLog.Open(_temp))
        {
            var reporter = new LoggingLaunchReporter(inner, log);

            reporter.Status("Resolving version");
            reporter.Line("Java 21.0.9 (Oracle Corporation, 64-bit)");
            reporter.Line("no compatible Java installation found", isError: true);
        }

        var text = LogText();

        Assert.Contains("[INFO] Resolving version", text, StringComparison.Ordinal);
        Assert.Contains("[INFO] Java 21.0.9", text, StringComparison.Ordinal);
        Assert.Contains("[ERROR] no compatible Java installation found", text, StringComparison.Ordinal);
    }

    /// <summary>Everything still reaches the front-end: this tees, it does not divert.</summary>
    [Fact]
    public void TheFrontEndStillSeesEverything()
    {
        var inner = new Recording();

        using (var log = LauncherLog.Open(_temp))
        {
            var reporter = new LoggingLaunchReporter(inner, log);

            reporter.Status("Resolving version");
            reporter.Progress(37, 65);
            reporter.Line("hello");
        }

        Assert.Equal(["Resolving version"], inner.Statuses);
        Assert.Equal([(37L, 65L)], inner.Progresses);
        Assert.Equal([("hello", false)], inner.Lines);
    }

    // ================================================================== what does not

    /*
     * PROGRESS IS NOT LOGGED. A download reports per chunk, and a file with forty thousand lines of
     * "37 of 65" in it is not a log -- it is a denial-of-service on whoever opens it.
     */
    [Fact]
    public void ProgressNeverReachesTheFile()
    {
        var inner = new Recording();

        using (var log = LauncherLog.Open(_temp))
        {
            var reporter = new LoggingLaunchReporter(inner, log);

            for (var i = 0; i <= 1000; i++)
            {
                reporter.Progress(i, 1000);
            }
        }

        var text = LogText();

        Assert.DoesNotContain("1000", text, StringComparison.Ordinal);
        Assert.Equal(1001, inner.Progresses.Count);
    }

    /*
     * STATUSES ARE COMPARED WITH THE NUMBERS TAKEN OUT, which is not the obvious rule and is the one
     * that works: ConcurrentTask reports "Executing 5 task(s) (3 out of 65 are done)", so the progress
     * is INSIDE the text and no two are equal.
     *
     * A REAL DRY RUN IS WHAT SHOWED THIS. The first version compared exact strings, and a 65-library
     * instance produced 137 log lines, 130 of them that -- while the comment above it claimed the check
     * prevented exactly that. This test is that run, in miniature.
     */
    [Fact]
    public void StatusesThatDifferOnlyByANumberAreWrittenOnce()
    {
        var inner = new Recording();

        using (var log = LauncherLog.Open(_temp))
        {
            var reporter = new LoggingLaunchReporter(inner, log);

            for (var done = 0; done <= 65; done++)
            {
                reporter.Status($"Executing 5 task(s) ({done} out of 65 are done)");
            }

            // Different in a way that is not a number, so it is written.
            reporter.Status("Updating assets index...");
        }

        var lines = LogText().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines, l => l.Contains("Executing", StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("Updating assets index", StringComparison.Ordinal));

        // The front-end still saw every one, because a status bar wants them all.
        Assert.Equal(67, inner.Statuses.Count);
    }

    /// <summary>An identical status repeated is written once, and again after something else.</summary>
    [Fact]
    public void ARepeatedStatusIsWrittenOnce()
    {
        var inner = new Recording();

        using (var log = LauncherLog.Open(_temp))
        {
            var reporter = new LoggingLaunchReporter(inner, log);

            for (var i = 0; i < 50; i++)
            {
                reporter.Status("Executing 5 task(s)");
            }

            reporter.Status("Updating game files");
            reporter.Status("Executing 5 task(s)");
        }

        var lines = LogText().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Twice for the repeated one -- it came back after something else, which is a real transition.
        Assert.Equal(2, lines.Count(l => l.Contains("Executing 5 task(s)", StringComparison.Ordinal)));
        Assert.Single(lines, l => l.Contains("Updating game files", StringComparison.Ordinal));

        // ...and the front-end still saw all 52, because a status bar wants every one.
        Assert.Equal(52, inner.Statuses.Count);
    }

    // ================================================================== guards

    [Fact]
    public void NullArgumentsAreRefused()
    {
        using var log = LauncherLog.Open(_temp);

        Assert.Throws<ArgumentNullException>(() => new LoggingLaunchReporter(null!, log));
        Assert.Throws<ArgumentNullException>(() => new LoggingLaunchReporter(new Recording(), null!));
    }
}
