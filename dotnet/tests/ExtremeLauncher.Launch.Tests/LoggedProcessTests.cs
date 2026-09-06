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
 * Characterization tests for process logging. There is no upstream Qt test for LoggedProcess.
 *
 * The line-assembly and level-parsing tests are pure and run everywhere. The tests that spawn a real
 * process use the host's own shell, and report as skipped where that is not available rather than
 * pretending to pass.
 */

using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class MessageLevelTests
{
    [Theory]
    [InlineData("Launcher", MessageLevel.Launcher)]
    [InlineData("Debug", MessageLevel.Debug)]
    [InlineData("Info", MessageLevel.Info)]
    [InlineData("Warning", MessageLevel.Warning)]
    [InlineData("Error", MessageLevel.Error)]
    [InlineData("Fatal", MessageLevel.Fatal)]
    [InlineData("Nonsense", MessageLevel.Unknown)]
    public void LevelNamesParse(string name, MessageLevel expected)
        => Assert.Equal(expected, MessageLevels.FromName(name));

    [Theory]
    [InlineData("StdOut")]
    [InlineData("StdErr")]
    public void StreamNamesAreNotClaimableByAProcess(string name)
    {
        // These describe where a line came from, so a process must not be able to assert them.
        Assert.Equal(MessageLevel.Unknown, MessageLevels.FromName(name));
    }

    [Fact]
    public void APrefixedLineYieldsItsLevelAndLosesTheMarker()
    {
        var line = "!![Error]!Something went wrong";

        Assert.Equal(MessageLevel.Error, MessageLevels.FromLine(ref line));
        Assert.Equal("Something went wrong", line);
    }

    [Fact]
    public void AnUnprefixedLineIsUntouched()
    {
        var line = "[12:34:56] [main/INFO]: Setting user: Steve";

        Assert.Equal(MessageLevel.Unknown, MessageLevels.FromLine(ref line));
        Assert.Equal("[12:34:56] [main/INFO]: Setting user: Steve", line);
    }

    [Fact]
    public void AMalformedPrefixIsLeftAlone()
    {
        // Opens the marker but never closes it.
        var unterminated = "!![Error Something went wrong";
        Assert.Equal(MessageLevel.Unknown, MessageLevels.FromLine(ref unterminated));
        Assert.Equal("!![Error Something went wrong", unterminated);

        // Closes it but never opens it.
        var noOpen = "Error]! something";
        Assert.Equal(MessageLevel.Unknown, MessageLevels.FromLine(ref noOpen));
        Assert.Equal("Error]! something", noOpen);
    }

    [Fact]
    public void AWellFormedMarkerIsStrippedEvenWhenTheLevelIsUnrecognised()
    {
        var line = "!![Bogus]!text";

        // Upstream strips unconditionally once the marker parses; only the level comes back Unknown.
        // The marker is launcher-internal, so leaving it in the log would be noise either way.
        Assert.Equal(MessageLevel.Unknown, MessageLevels.FromLine(ref line));
        Assert.Equal("text", line);
    }
}

public sealed class LineAssemblerTests
{
    private static List<string> Feed(LoggedProcess.LineAssembler assembler, params string[] chunks)
    {
        var lines = new List<string>();

        foreach (var chunk in chunks)
        {
            lines.AddRange(assembler.Accept(chunk));
        }

        lines.AddRange(assembler.Flush());
        return lines;
    }

    [Fact]
    public void WholeLinesComeThroughIntact()
    {
        var assembler = new LoggedProcess.LineAssembler();

        Assert.Equal(["one", "two"], Feed(assembler, "one\ntwo"));
    }

    [Fact]
    public void ALineSplitAcrossReadsIsReassembled()
    {
        var assembler = new LoggedProcess.LineAssembler();

        // The level prefix sits at the start of a line, so a split there would lose the tag entirely.
        Assert.Equal(["!![Error]!broken pipe"], Feed(assembler, "!![Err", "or]!broken", " pipe"));
    }

    [Fact]
    public void CarriageReturnsAreStripped()
    {
        var assembler = new LoggedProcess.LineAssembler();

        Assert.Equal(["one", "two"], Feed(assembler, "one\r\ntwo\r\n"));
    }

    [Fact]
    public void AFinalLineWithoutANewlineIsNotLost()
    {
        var assembler = new LoggedProcess.LineAssembler();

        // Crash output frequently arrives with no trailing newline.
        Assert.Equal(["complete", "dangling"], Feed(assembler, "complete\ndangling"));
    }

    [Fact]
    public void FlushingTwiceYieldsNothingTheSecondTime()
    {
        var assembler = new LoggedProcess.LineAssembler();
        assembler.Accept("partial");

        Assert.Single(assembler.Flush());
        Assert.Empty(assembler.Flush());
    }
}

public sealed class LoggedProcessTests
{
    /// <summary>Runs a command through the host shell, or null when there isn't one to use.</summary>
    private static (string Program, string[] Args)? ShellCommand(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            return comspec is null ? null : (comspec, ["/c", command]);
        }

        return File.Exists("/bin/sh") ? ("/bin/sh", ["-c", command]) : null;
    }

    private static async Task<(LoggedProcess.ProcessState State, List<(string Line, MessageLevel Level)> Log, int ExitCode)>
        Run(string command)
    {
        var shell = ShellCommand(command);
        Assert.NotNull(shell);

        using var process = new LoggedProcess();

        var log = new List<(string, MessageLevel)>();
        process.Log += (_, e) =>
        {
            lock (log)
            {
                log.AddRange(e.Lines.Select(l => (l, e.Level)));
            }
        };

        process.Start(shell.Value.Program, shell.Value.Args, Path.GetTempPath());

        var state = await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        return (state, log, process.ExitCode);
    }

    [SkippableFact]
    public async Task StandardOutputIsCapturedAndTagged()
    {
        Skip.If(ShellCommand("echo") is null, "No host shell available.");

        var (state, log, exitCode) = await Run("echo hello from the game");

        Assert.Equal(LoggedProcess.ProcessState.Finished, state);
        Assert.Equal(0, exitCode);
        Assert.Contains(log, entry => entry.Line.Contains("hello from the game", StringComparison.Ordinal)
                                      && entry.Level == MessageLevel.StdOut);
    }

    [SkippableFact]
    public async Task AGameErrorOnStandardOutputIsGuessedAsAnError()
    {
        Skip.If(ShellCommand("echo") is null, "No host shell available.");

        // The game prints crashes to stdout, not stderr, and without the launcher's level markers. The
        // line is levelled by its content, so an exception on stdout still comes through as an Error.
        var (_, log, _) = await Run("echo Exception in thread main");

        Assert.Contains(log, entry => entry.Line.Contains("Exception in thread", StringComparison.Ordinal)
                                      && entry.Level == MessageLevel.Error);
    }

    [SkippableFact]
    public async Task ANonZeroExitIsReportedAsACrash()
    {
        Skip.If(ShellCommand("exit") is null, "No host shell available.");

        var (state, log, exitCode) = await Run("exit 1");

        // .NET cannot distinguish QProcess::NormalExit, so non-zero means crashed.
        Assert.Equal(LoggedProcess.ProcessState.Crashed, state);
        Assert.Equal(1, exitCode);
        Assert.Contains(log, entry => entry.Line.Contains("crashed", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task ACleanExitReportsItsCode()
    {
        Skip.If(ShellCommand("exit") is null, "No host shell available.");

        var (state, log, _) = await Run("exit 0");

        Assert.Equal(LoggedProcess.ProcessState.Finished, state);
        Assert.Contains(log, entry => entry.Line.Contains("exited with code 0", StringComparison.Ordinal));
    }

    [Fact]
    public void AProgramThatDoesNotExistFailsToStartRatherThanThrowing()
    {
        using var process = new LoggedProcess();

        var log = new List<string>();
        process.Log += (_, e) => log.AddRange(e.Lines);

        process.Start(
            Path.Combine(Path.GetTempPath(), "definitely-not-a-program-" + Guid.NewGuid().ToString("N")),
            [],
            Path.GetTempPath());

        Assert.Equal(LoggedProcess.ProcessState.FailedToStart, process.State);
        Assert.Contains(log, l => l.Contains("Failed to start", StringComparison.Ordinal));
    }

    [Fact]
    public void StateChangesAreObservable()
    {
        using var process = new LoggedProcess();

        var states = new List<LoggedProcess.ProcessState>();
        process.StateChanged += (_, s) => states.Add(s);

        process.Start(
            Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")),
            [],
            Path.GetTempPath());

        Assert.Equal(
            [LoggedProcess.ProcessState.Starting, LoggedProcess.ProcessState.FailedToStart],
            states);
    }

    [SkippableFact]
    public async Task KillingAProcessReportsItAsAborted()
    {
        Skip.If(ShellCommand("sleep") is null, "No host shell available.");
        Skip.If(OperatingSystem.IsWindows(), "No portable long-running shell command on Windows.");

        using var process = new LoggedProcess();
        process.Start("/bin/sh", ["-c", "sleep 30"], Path.GetTempPath());

        process.Kill();

        var state = await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(LoggedProcess.ProcessState.Aborted, state);
    }
}
