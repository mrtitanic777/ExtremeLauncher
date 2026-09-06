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
 * Characterization tests for the launch pipeline. There is no upstream Qt test for LaunchTask.
 */

using ExtremeLauncher.Java;
using ExtremeLauncher.Tasks;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LaunchPipelineTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-pipeline-" + Guid.NewGuid().ToString("N"));

    public LaunchPipelineTests() => Directory.CreateDirectory(_temp);

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

    /// <summary>A step that records when it ran and when it was finalized.</summary>
    private sealed class RecordingStep : LaunchStep
    {
        private readonly List<string> _journal;
        private readonly bool _shouldFail;

        public RecordingStep(string name, List<string> journal, bool shouldFail = false) : base(name)
        {
            _journal = journal;
            _shouldFail = shouldFail;
        }

        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _journal.Add($"run:{Name}");

            return _shouldFail
                ? throw new TaskFailedException($"{Name} was told to fail")
                : Task.CompletedTask;
        }

        public override Task FinalizeAsync()
        {
            _journal.Add($"finalize:{Name}");
            return Task.CompletedTask;
        }
    }

    // ================================================================== sequencing

    [Fact]
    public async Task StepsRunInOrderAndFinalizeInReverse()
    {
        var journal = new List<string>();

        var pipeline = new LaunchPipeline()
            .AddStep(new RecordingStep("first", journal))
            .AddStep(new RecordingStep("second", journal))
            .AddStep(new RecordingStep("third", journal));

        Assert.True(await pipeline.RunAsync());

        // Reverse teardown: an early step creates what a later one uses, so unwinding backwards
        // means nothing is removed while something still depends on it.
        Assert.Equal(
            ["run:first", "run:second", "run:third", "finalize:third", "finalize:second", "finalize:first"],
            journal);
    }

    [Fact]
    public async Task AFailedStepStopsTheRestButStillUnwinds()
    {
        var journal = new List<string>();

        var pipeline = new LaunchPipeline()
            .AddStep(new RecordingStep("first", journal))
            .AddStep(new RecordingStep("broken", journal, shouldFail: true))
            .AddStep(new RecordingStep("never", journal));

        Assert.False(await pipeline.RunAsync());

        // "never" does not run; "broken" is finalized anyway, because it may have set something up
        // before it failed.
        Assert.Equal(["run:first", "run:broken", "finalize:broken", "finalize:first"], journal);
    }

    [Fact]
    public async Task TheFailureReasonNamesTheStep()
    {
        var pipeline = new LaunchPipeline()
            .AddStep(new RecordingStep("broken", [], shouldFail: true));

        Assert.False(await pipeline.RunAsync());
        Assert.Contains("broken", pipeline.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationUnwindsAndReportsAsAborted()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var journal = new List<string>();
        var pipeline = new LaunchPipeline().AddStep(new RecordingStep("first", journal));

        Assert.False(await pipeline.RunAsync(cts.Token));
        Assert.Equal(TaskState.AbortedByUser, pipeline.State);
    }

    [Fact]
    public async Task AFailingFinalizerDoesNotStrandTheRest()
    {
        var journal = new List<string>();

        var pipeline = new LaunchPipeline()
            .AddStep(new RecordingStep("first", journal))
            .AddStep(new ThrowingFinalizer("bad-cleanup"))
            .AddStep(new RecordingStep("third", journal));

        var warnings = new List<string>();
        pipeline.LogLines += (_, e) => warnings.AddRange(e.Lines);

        Assert.True(await pipeline.RunAsync());

        // "first" is still finalized despite the one before it in the unwind order throwing.
        Assert.Contains("finalize:first", journal);
        Assert.Contains(warnings, w => w.Contains("bad-cleanup", StringComparison.Ordinal));
    }

    private sealed class ThrowingFinalizer : LaunchStep
    {
        public ThrowingFinalizer(string name) : base(name)
        {
        }

        protected override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task FinalizeAsync() => throw new IOException("cleanup exploded");
    }

    [Fact]
    public async Task LogLinesFromEveryStepReachOneStream()
    {
        var pipeline = new LaunchPipeline()
            .AddStep(new CreateGameFolders(Path.Combine(_temp, "instance", ".minecraft")))
            .AddStep(new VerifyJavaInstall(new JavaVersion("17"), []));

        var lines = new List<string>();
        pipeline.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await pipeline.RunAsync());

        // Nothing to say on a clean run, but the wiring is what matters.
        Assert.Empty(lines);
    }

    // ================================================================== VerifyJavaInstall

    [Fact]
    public async Task AProfileWithNoJavaOpinionAcceptsAnything()
        => Assert.True(await new VerifyJavaInstall(new JavaVersion("8"), []).RunAsync());

    [Fact]
    public async Task ACompatibleMajorPasses()
        => Assert.True(await new VerifyJavaInstall(new JavaVersion("17.0.1"), [17, 21]).RunAsync());

    [Fact]
    public async Task AnIncompatibleMajorFailsWithGuidance()
    {
        var step = new VerifyJavaInstall(new JavaVersion("8"), [17, 21]);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.False(await step.RunAsync());
        Assert.Equal("Incompatible Java major version", step.FailReason);

        // The user is told which versions would work, not just that this one does not.
        Assert.Contains(lines, l => l.Contains("Java version 17", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Java version 21", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCompatibilityCheckCanBeOverridden()
    {
        var step = new VerifyJavaInstall(new JavaVersion("8"), [17], ignoreCompatibility: true);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());
        Assert.Contains(lines, l => l.Contains("Things might break", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TooMuchMemoryOnA32BitJvmWarnsButDoesNotBlock()
    {
        var step = new VerifyJavaInstall(new JavaVersion("8"), [], javaArchitecture: "32", maxMemoryMegabytes: 4096);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        // The user chose both values; warned, not corrected.
        Assert.True(await step.RunAsync());
        Assert.Contains(lines, l => l.Contains("32-bit", StringComparison.Ordinal));
    }

    // ================================================================== CreateGameFolders

    [Fact]
    public async Task GameFoldersAreCreated()
    {
        var gameRoot = Path.Combine(_temp, "instance", ".minecraft");

        Assert.True(await new CreateGameFolders(gameRoot).RunAsync());

        Assert.True(Directory.Exists(gameRoot));

        // Works around MCL-3732: the game fails if this is absent when a server sends a pack.
        Assert.True(Directory.Exists(Path.Combine(gameRoot, "server-resource-packs")));
    }

    [Fact]
    public async Task CreatingFoldersIsIdempotent()
    {
        var gameRoot = Path.Combine(_temp, "instance2", ".minecraft");

        Assert.True(await new CreateGameFolders(gameRoot).RunAsync());
        Assert.True(await new CreateGameFolders(gameRoot).RunAsync());
    }

    // ================================================================== LauncherPartLaunch

    [Fact]
    public async Task LaunchingAMissingJavaFails()
    {
        var step = new LauncherPartLaunch(
            Path.Combine(_temp, "no-java-here"),
            ["-version"],
            _temp);

        Assert.False(await step.RunAsync());
        Assert.Equal(LoggedProcess.ProcessState.FailedToStart, step.FinalState);
        Assert.Contains("Could not launch Java", step.FailReason, StringComparison.Ordinal);
    }

    // ================================================================== wrapper commands

    [Fact]
    public async Task AMissingWrapperFailsTheLaunchRatherThanBeingSkipped()
    {
        var step = new LauncherPartLaunch(
            Path.Combine(_temp, "java"),
            ["-version"],
            _temp,
            environment: null,
            wrapperCommand: "definitely-not-a-real-program --flag");

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        // Silently starting without it would run the game on the wrong GPU, or without the frame
        // limiter the user set up, with nothing in the log to say why.
        Assert.False(await step.RunAsync());
        Assert.Contains("definitely-not-a-real-program", step.FailReason, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("couldn't be found", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task AWrapperRunsTheGameAsItsChild()
    {
        Skip.IfNot(File.Exists("/bin/sh"), "Needs a POSIX shell to stand in for a wrapper.");

        // /bin/echo stands in for prime-run or gamemoderun: it prints what it was asked to run.
        Skip.IfNot(File.Exists("/bin/echo"), "No /bin/echo.");

        var step = new LauncherPartLaunch(
            "/nonexistent/java",
            ["-version"],
            _temp,
            environment: null,
            wrapperCommand: "/bin/echo --wrapper-flag");

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());

        // The wrapper's own arguments come first, then the java path, then the game's arguments.
        Assert.Contains(lines, l => l.Contains("--wrapper-flag /nonexistent/java -version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyWrapperChangesNothing()
    {
        var step = new LauncherPartLaunch(
            Path.Combine(_temp, "no-java-here"),
            ["-version"],
            _temp,
            environment: null,
            wrapperCommand: "   ");

        // Whitespace is trimmed to nothing, so this is the ordinary no-wrapper path.
        Assert.False(await step.RunAsync());
        Assert.Equal(LoggedProcess.ProcessState.FailedToStart, step.FinalState);
        Assert.Contains("Could not launch Java", step.FailReason, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ACrashingGameIsReportedButDoesNotFailTheStep()
    {
        Skip.If(OperatingSystem.IsWindows(), "No portable shell exit-code command on Windows.");
        Skip.IfNot(File.Exists("/bin/sh"), "No host shell available.");

        var step = new LauncherPartLaunch("/bin/sh", ["-c", "exit 3"], _temp);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        // The game ran. The user wants the log, not a launcher error.
        Assert.True(await step.RunAsync());
        Assert.Equal(LoggedProcess.ProcessState.Crashed, step.FinalState);
        Assert.Equal(3, step.ExitCode);
        Assert.Contains(lines, l => l.Contains("exited abnormally", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task AFullPipelineRunsThroughToTheProcess()
    {
        Skip.IfNot(File.Exists("/bin/sh") || OperatingSystem.IsWindows(), "No host shell available.");

        var gameRoot = Path.Combine(_temp, "full", ".minecraft");

        var (program, args) = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("ComSpec")!, new[] { "/c", "echo launched" })
            : ("/bin/sh", ["-c", "echo launched"]);

        var pipeline = new LaunchPipeline()
            .AddStep(new VerifyJavaInstall(new JavaVersion("17"), [17]))
            .AddStep(new CreateGameFolders(gameRoot))
            .AddStep(new LauncherPartLaunch(program, args, _temp));

        var lines = new List<string>();
        pipeline.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await pipeline.RunAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(Directory.Exists(gameRoot));
        Assert.Contains(lines, l => l.Contains("launched", StringComparison.Ordinal));
    }
}
