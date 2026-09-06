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
 * Something to look at while a task runs.
 *
 * THE INTERESTING CASES ARE THE EDGES, not the happy path: a task that never reports a total, one
 * that cannot be stopped, and one somebody presses Cancel on twice. All three are how a progress
 * dialog becomes worse than none.
 */

using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class TaskProgressViewModelTests
{
    private sealed class StubTask(bool canAbort = true, bool succeeds = true) : IRunnableTask
    {
        private readonly TaskCompletionSource<bool> _finish = new();

        public event EventHandler<string>? StatusChanged;

        public event EventHandler<(long Current, long Total)>? ProgressChanged;

        public bool CanAbort { get; } = canAbort;

        public string FailReason { get; set; } = string.Empty;

        public CancellationToken Token { get; private set; }

        public bool Started { get; private set; }

        public Task<bool> RunAsync(CancellationToken cancellationToken)
        {
            Started = true;
            Token = cancellationToken;

            return _finish.Task;
        }

        public void Report(string status) => StatusChanged?.Invoke(this, status);

        public void Report(long current, long total) => ProgressChanged?.Invoke(this, (current, total));

        public void Finish() => _finish.TrySetResult(succeeds);
    }

    [Fact]
    public async Task TheStatusFollowsTheTask()
    {
        var task = new StubTask();
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var run = model.RunAsync();

        task.Report("Downloading sodium");

        Assert.Equal("Downloading sodium", model.Status);

        task.Finish();

        await run;
    }

    [Fact]
    public async Task ABarWithNoTotalKeepsMoving()
    {
        /*
         * A bar sitting at 0% for the twenty seconds before the first byte count looks like a stuck
         * one, and a stuck bar is what makes people kill a launcher half way through writing files.
         */
        var task = new StubTask();
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var run = model.RunAsync();

        Assert.True(model.IsIndeterminate);
        Assert.Equal(string.Empty, model.ProgressText);

        task.Report(10, 100);

        Assert.False(model.IsIndeterminate);
        Assert.Equal("10%", model.ProgressText);

        task.Finish();

        await run;
    }

    [Fact]
    public async Task ThePercentageIsClampedToSomethingSensible()
    {
        // A job whose total shrinks mid-run -- a download that turned out smaller than announced --
        // must not report 140%.
        var task = new StubTask();
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var run = model.RunAsync();

        task.Report(140, 100);

        Assert.Equal(100, model.Percent);

        task.Report(-5, 100);

        Assert.Equal(0, model.Percent);

        task.Finish();

        await run;
    }

    [Fact]
    public async Task CancellingAsksTheTaskRatherThanJustClosing()
    {
        /*
         * Cancelling a download is not instant: the request in flight has to come back. A window that
         * vanished the moment somebody pressed Cancel would invite them to start the same thing again
         * while the first is still winding down.
         */
        var task = new StubTask(canAbort: true);
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var run = model.RunAsync();

        Assert.True(model.CanCancel);

        model.Cancel();

        Assert.True(task.Token.IsCancellationRequested);
        Assert.Contains("Stopping", model.Status, StringComparison.Ordinal);

        // And it cannot be pressed again while it winds down.
        Assert.False(model.CanCancel);

        task.Finish();

        await run;
    }

    [Fact]
    public async Task ATaskThatCannotBeStoppedOffersNoCancelButton()
    {
        // A Cancel button that does nothing is worse than none, because somebody will press it and
        // then press it again.
        var task = new StubTask(canAbort: false);
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var run = model.RunAsync();

        Assert.False(model.CanCancel);

        task.Finish();

        await run;
    }

    [Fact]
    public async Task ATaskThatCannotBeStoppedAlsoCannotBeAbandoned()
    {
        /*
         * Closing the window would leave it writing files with nothing on screen -- which is exactly
         * the situation this window exists to prevent.
         */
        var task = new StubTask(canAbort: false);
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var run = model.RunAsync();

        Assert.False(model.CanCloseWindow);

        task.Finish();

        await run;

        // Once it is over, of course it can.
        Assert.True(model.CanCloseWindow);
    }

    [Fact]
    public async Task AStoppableTaskCanBeClosedWhileItRuns()
    {
        var task = new StubTask(canAbort: true);
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var run = model.RunAsync();

        Assert.True(model.CanCloseWindow);

        task.Finish();

        await run;
    }

    [Fact]
    public async Task TheWindowIsToldWhenTheWorkIsOver()
    {
        // Nobody wants to dismiss a dialog whose only job was to say "still going".
        var task = new StubTask();
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var finished = 0;

        model.Finished += (_, _) => finished++;

        var run = model.RunAsync();

        Assert.Equal(0, finished);

        task.Finish();

        await run;

        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task AFailedTaskFinishesToo()
    {
        /*
         * Otherwise the window stays up forever on the one occasion somebody most needs to see the
         * reason -- which is the failure mode a naive "close on success" would have.
         */
        var task = new StubTask(succeeds: false) { FailReason = "the server hung up" };
        var model = new TaskProgressViewModel(task, "Doing a thing");

        var finished = false;

        model.Finished += (_, _) => finished = true;

        var run = model.RunAsync();

        task.Finish();

        Assert.False(await run);
        Assert.True(finished);
        Assert.Equal("the server hung up", model.FailReason);
    }

    [Fact]
    public async Task NothingIsCancellableBeforeItStarts()
    {
        var task = new StubTask();
        var model = new TaskProgressViewModel(task, "Doing a thing");

        Assert.False(model.CanCancel);

        model.Cancel();

        Assert.False(task.Token.IsCancellationRequested);

        var run = model.RunAsync();

        task.Finish();

        await run;
    }
}
