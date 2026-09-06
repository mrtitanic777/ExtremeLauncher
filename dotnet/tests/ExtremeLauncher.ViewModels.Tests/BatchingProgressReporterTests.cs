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
 * This is threading code whose failure mode is a crash under load, which is the worst kind to leave
 * unchecked: it works on every machine that never launches a modded pack.
 *
 * The dispatcher is a delegate here, so a test can hold the "UI thread" still and look at what the
 * worker did in the meantime -- which is the only way to check that a burst really did collapse into
 * one post rather than merely appearing to.
 */

using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class BatchingProgressReporterTests
{
    /// <summary>A stand-in for the UI thread that only runs when the test says so.</summary>
    private sealed class ManualDispatcher
    {
        private readonly List<Action> _posted = [];

        public int PostCount { get; private set; }

        public void Post(Action action)
        {
            lock (_posted)
            {
                _posted.Add(action);
                PostCount++;
            }
        }

        /// <summary>Runs everything currently queued.</summary>
        public void Run()
        {
            Action[] due;

            lock (_posted)
            {
                due = [.. _posted];
                _posted.Clear();
            }

            foreach (var action in due)
            {
                action();
            }
        }
    }

    /// <summary>Records what reached the window.</summary>
    private sealed class RecordingProgress : IProgressSink
    {
        public List<string> Statuses { get; } = [];

        public List<(long Current, long Total)> Progresses { get; } = [];

        public List<LaunchLogLine> Lines { get; } = [];

        public void SetStatus(string status) => Statuses.Add(status);

        public void SetProgress(long current, long total) => Progresses.Add((current, total));

        public void Log(string line, bool isError = false) => Lines.Add(new LaunchLogLine(line, isError));
    }

    // ================================================================== nothing happens off-thread

    /*
     * THE POINT OF THE WHOLE CLASS. If a worker's Line() reached the coordinator directly, it would be
     * mutating an ObservableCollection from a background thread -- an Avalonia crash, and one that only
     * shows up when a real game is logging.
     */
    [Fact]
    public void NothingReachesTheWindowUntilTheDispatcherRuns()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        using var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        reporter.Status("Downloading libraries");
        reporter.Progress(1, 2);
        reporter.Line("hello");

        Assert.Empty(progress.Statuses);
        Assert.Empty(progress.Progresses);
        Assert.Empty(progress.Lines);

        dispatcher.Run();

        Assert.Equal(["Downloading libraries"], progress.Statuses);
        Assert.Equal([(1L, 2L)], progress.Progresses);
        Assert.Equal(["hello"], progress.Lines.Select(l => l.Text));
    }

    // ================================================================== batching

    /*
     * A thousand lines must not become a thousand posts. This is the difference between a window that
     * repaints during Forge's startup and one that does not.
     */
    [Fact]
    public void ABurstCollapsesIntoASinglePost()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        using var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        for (var i = 0; i < 1000; i++)
        {
            reporter.Line($"line {i}");
        }

        Assert.Equal(1, dispatcher.PostCount);

        dispatcher.Run();

        // ...and every one of them still arrives, in order.
        Assert.Equal(1000, progress.Lines.Count);
        Assert.Equal("line 0", progress.Lines[0].Text);
        Assert.Equal("line 999", progress.Lines[999].Text);
    }

    [Fact]
    public void AFurtherEventAfterADrainSchedulesANewPost()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        using var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        reporter.Line("first");
        dispatcher.Run();

        reporter.Line("second");

        Assert.Equal(2, dispatcher.PostCount);

        dispatcher.Run();

        Assert.Equal(["first", "second"], progress.Lines.Select(l => l.Text));
    }

    /// <summary>Only the latest status and progress survive — an intermediate one is not worth a frame.</summary>
    [Fact]
    public void StatusAndProgressCoalesceToTheLatest()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        using var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        reporter.Status("Resolving");
        reporter.Status("Downloading");
        reporter.Progress(1, 100);
        reporter.Progress(99, 100);

        dispatcher.Run();

        Assert.Equal(["Downloading"], progress.Statuses);
        Assert.Equal([(99L, 100L)], progress.Progresses);
    }

    /// <summary>A drain leaves nothing behind, so an empty one delivers nothing twice.</summary>
    [Fact]
    public void ASecondDrainWithNothingBufferedDeliversNothing()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        using var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        reporter.Status("Resolving");
        reporter.Line("hello");

        dispatcher.Run();
        reporter.Flush();

        Assert.Single(progress.Statuses);
        Assert.Single(progress.Lines);
    }

    // ================================================================== the buffer is bounded

    /*
     * Without a cap, a game logging faster than the UI thread drains grows this buffer without limit --
     * the same leak the coordinator's own cap exists to prevent, one layer earlier.
     */
    [Fact]
    public void TheBufferIsBoundedAndDropsAreCounted()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        using var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        // The dispatcher never runs, so nothing drains: this is a UI thread that has stopped keeping up.
        for (var i = 0; i < BatchingProgressReporter.MaxBuffered + 500; i++)
        {
            reporter.Line($"line {i}");
        }

        Assert.Equal(500, reporter.DroppedLines);

        dispatcher.Run();

        Assert.Equal(BatchingProgressReporter.MaxBuffered, progress.Lines.Count);

        // The OLDEST were kept here, unlike the coordinator's log, because these have not been seen at
        // all yet -- dropping the newest keeps the start of the failure, which is where the cause is.
        Assert.Equal("line 0", progress.Lines[0].Text);
    }

    // ================================================================== finishing

    [Fact]
    public void FlushDeliversWithoutADispatcherRun()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        using var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        reporter.Line("the last thing the game said");
        reporter.Flush();

        Assert.Equal(["the last thing the game said"], progress.Lines.Select(l => l.Text));
    }

    /*
     * A drain posted after the launch ends would run against a coordinator that has already reported
     * itself finished, appending the tail of a dead launch's log to whatever came next.
     */
    [Fact]
    public void DisposingStopsFurtherPosts()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        reporter.Line("during");
        dispatcher.Run();
        reporter.Dispose();

        reporter.Line("after");

        Assert.Equal(1, dispatcher.PostCount);

        dispatcher.Run();

        Assert.Equal(["during"], progress.Lines.Select(l => l.Text));
    }

    [Fact]
    public void ErrorLinesKeepTheirFlag()
    {
        var dispatcher = new ManualDispatcher();
        var progress = new RecordingProgress();

        using var reporter = new BatchingProgressReporter(progress, dispatcher.Post);

        reporter.Line("ordinary");
        reporter.Line("broken", isError: true);

        dispatcher.Run();

        Assert.False(progress.Lines[0].IsError);
        Assert.True(progress.Lines[1].IsError);
    }

    // ================================================================== under contention

    /*
     * The real shape of the problem: several LauncherTasks reporting at once while the UI thread drains
     * whenever it gets a slot. Checks that nothing is lost, nothing is duplicated and nothing throws --
     * a lock held across the progress callbacks would deadlock here instead.
     */
    [Fact]
    public async Task ConcurrentReportersLoseNothing()
    {
        var progress = new RecordingProgress();
        var gate = new Lock();
        var pending = new List<Action>();

        // Posts arrive from worker threads, so the queue itself needs a lock.
        void Post(Action action)
        {
            lock (gate)
            {
                pending.Add(action);
            }
        }

        using var reporter = new BatchingProgressReporter(progress, Post);

        const int Workers = 8;
        const int PerWorker = 500;

        var drain = true;

        // One "UI thread", draining whenever there is something to drain.
        var ui = Task.Run(() =>
        {
            while (Volatile.Read(ref drain))
            {
                RunPending();
            }
        });

        void RunPending()
        {
            Action[] due;

            lock (gate)
            {
                due = [.. pending];
                pending.Clear();
            }

            foreach (var action in due)
            {
                action();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                reporter.Line($"{worker}:{i}");
            }
        }))).ConfigureAwait(true);

        Volatile.Write(ref drain, false);
        await ui.ConfigureAwait(true);

        // Anything the last drain missed.
        RunPending();
        reporter.Flush();

        Assert.Equal(0, reporter.DroppedLines);
        Assert.Equal(Workers * PerWorker, progress.Lines.Count);
        Assert.Equal(Workers * PerWorker, progress.Lines.Select(l => l.Text).Distinct().Count());

        // Each worker's own lines keep their order, which is what a log has to preserve to be readable.
        for (var worker = 0; worker < Workers; worker++)
        {
            var prefix = $"{worker}:";
            var mine = progress.Lines.Where(l => l.Text.StartsWith(prefix, StringComparison.Ordinal))
                .Select(l => int.Parse(l.Text[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture))
                .ToList();

            Assert.Equal(Enumerable.Range(0, PerWorker), mine);
        }
    }
}
