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
 * Ported from tests/Task_test.cpp.
 */

using Xunit;

namespace ExtremeLauncher.Tasks.Tests;

/// <summary>Does nothing. Only used for testing. Mirrors the upstream BasicTask.</summary>
internal sealed class BasicTask : LauncherTask
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Fails immediately with a fixed reason.</summary>
internal sealed class FailingTask : LauncherTask
{
    private readonly string _reason;

    public FailingTask(string reason = "nope") => _reason = reason;

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
        => throw new TaskFailedException(_reason);
}

/// <summary>Records whether it ever ran, so "this option was skipped" is observable.</summary>
internal sealed class RecordingTask : LauncherTask
{
    public bool DidRun { get; private set; }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        DidRun = true;
        return Task.CompletedTask;
    }
}

internal sealed class MultiStepTask : LauncherTask
{
    public override bool IsMultiStep => true;

    protected override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TaskTests
{
    [Fact]
    public void SetStatusNoMultiStep()
    {
        var t = new BasicTask();

        t.SetStatus("test status");

        Assert.Equal("test status", t.Status);
        Assert.Empty(t.GetStepProgress());
    }

    [Fact]
    public void SetStatusMultiStep()
    {
        var t = new MultiStepTask();

        t.SetStatus("test status");

        Assert.Equal("test status", t.Status);

        // Multi-step, but it does not override GetStepProgress, so this stays empty.
        Assert.Empty(t.GetStepProgress());
    }

    [Fact]
    public void SetProgress()
    {
        var t = new BasicTask();

        t.SetProgress(42, 207);

        Assert.Equal(42, t.Progress);
        Assert.Equal(207, t.TotalProgress);
    }

    [Fact]
    public async Task BasicRun()
    {
        var t = new BasicTask();
        var finished = false;
        t.Finished += (_, _) => finished = true;

        Assert.True(await t.RunAsync());

        Assert.True(t.WasSuccessful);
        Assert.True(t.IsFinished);
        Assert.True(finished);
        Assert.Equal(TaskState.Succeeded, t.State);
    }

    [Fact]
    public async Task BasicConcurrentRun()
    {
        var subtasks = new[] { new BasicTask(), new BasicTask(), new BasicTask() };
        var t = new ConcurrentTask();

        foreach (var subtask in subtasks)
        {
            t.AddTask(subtask);
        }

        Assert.True(await t.RunAsync());
        Assert.All(subtasks, s => Assert.True(s.WasSuccessful));
    }

    /// <summary>Upstream: "Tests if starting new tasks after the 6 initial ones is working".</summary>
    [Fact]
    public async Task MoreConcurrentRunThanTheConcurrencyLimit()
    {
        var subtasks = Enumerable.Range(0, 9).Select(_ => new BasicTask()).ToArray();
        var t = new ConcurrentTask();

        foreach (var subtask in subtasks)
        {
            t.AddTask(subtask);
        }

        Assert.True(await t.RunAsync());
        Assert.All(subtasks, s => Assert.True(s.WasSuccessful));
    }

    [Fact]
    public async Task BasicSequentialRun()
    {
        var subtasks = new[] { new BasicTask(), new BasicTask(), new BasicTask() };
        var t = new SequentialTask();

        foreach (var subtask in subtasks)
        {
            t.AddTask(subtask);
        }

        Assert.True(await t.RunAsync());
        Assert.All(subtasks, s => Assert.True(s.WasSuccessful));
    }

    [Fact]
    public async Task BasicMultipleOptionsRun()
    {
        var t1 = new RecordingTask();
        var t2 = new RecordingTask();
        var t3 = new RecordingTask();

        var t = new MultipleOptionsTask();
        t.AddTask(t1);
        t.AddTask(t2);
        t.AddTask(t3);

        Assert.True(await t.RunAsync());

        // First success wins; the remaining options are never attempted.
        Assert.True(t1.WasSuccessful);
        Assert.False(t2.WasSuccessful);
        Assert.False(t3.WasSuccessful);
        Assert.True(t1.DidRun);
        Assert.False(t2.DidRun);
        Assert.False(t3.DidRun);
    }

    /// <remarks>
    /// The upstream test queues 4096 instantly-succeeding subtasks and fails if the recursive
    /// executeNextSubTask chain overflows the stack. The worker-loop rewrite is iterative, so this is
    /// structurally safe; the count is raised here to make the point.
    /// </remarks>
    [Fact]
    public async Task NoStackOverflowInConcurrentTask()
    {
        const int count = 50_000;

        var t = new ConcurrentTask();

        for (var i = 0; i < count; i++)
        {
            t.AddTask(new BasicTask());
        }

        Assert.True(await t.RunAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(count, t.SucceededTasks.Count);
    }

    // ------------------------------------------------------------ failure semantics

    [Fact]
    public async Task ConcurrentTaskRunsEverythingThenFailsIfAnyFailed()
    {
        var ok1 = new RecordingTask();
        var bad = new FailingTask();
        var ok2 = new RecordingTask();

        var t = new ConcurrentTask();
        t.AddTask(ok1);
        t.AddTask(bad);
        t.AddTask(ok2);

        Assert.False(await t.RunAsync());
        Assert.Equal(TaskState.Failed, t.State);
        Assert.Equal("One or more subtasks failed", t.FailReason);

        // Not fail-fast: everything still ran.
        Assert.True(ok1.DidRun);
        Assert.True(ok2.DidRun);
    }

    [Fact]
    public async Task SequentialTaskFailsFastAndReportsTheSubtaskReason()
    {
        var first = new RecordingTask();
        var bad = new FailingTask("disk on fire");
        var never = new RecordingTask();

        var t = new SequentialTask();
        t.AddTask(first);
        t.AddTask(bad);
        t.AddTask(never);

        Assert.False(await t.RunAsync());

        // Upstream reports the failing subtask's own message, not a generic one.
        Assert.Equal("disk on fire", t.FailReason);

        Assert.True(first.DidRun);
        Assert.False(never.DidRun);
    }

    [Fact]
    public async Task MultipleOptionsTaskFallsThroughFailuresToASuccess()
    {
        var bad1 = new FailingTask();
        var bad2 = new FailingTask();
        var good = new RecordingTask();

        var t = new MultipleOptionsTask();
        t.AddTask(bad1);
        t.AddTask(bad2);
        t.AddTask(good);

        Assert.True(await t.RunAsync());
        Assert.True(good.DidRun);
    }

    [Fact]
    public async Task MultipleOptionsTaskFailsWhenEveryOptionFails()
    {
        var t = new MultipleOptionsTask();
        t.AddTask(new FailingTask());
        t.AddTask(new FailingTask());

        Assert.False(await t.RunAsync());
        Assert.Equal("All attempts have failed!", t.FailReason);
    }

    [Fact]
    public async Task UnexpectedExceptionsBecomeFailuresNotCrashes()
    {
        var t = new ThrowingTask();

        Assert.False(await t.RunAsync());
        Assert.Equal(TaskState.Failed, t.State);
        Assert.Equal("boom", t.FailReason);
    }

    // ------------------------------------------------------------ cancellation

    [Fact]
    public async Task CancellationIsADistinctEndStateNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        var t = new BlockingTask();

        var run = t.RunAsync(cts.Token);
        await cts.CancelAsync();

        Assert.False(await run);

        // The deliberate divergence from .NET convention: cancellation returns false and is recorded
        // as its own state rather than propagating an OperationCanceledException.
        Assert.Equal(TaskState.AbortedByUser, t.State);
        Assert.False(t.WasSuccessful);
    }

    [Fact]
    public async Task CancellingAConcurrentTaskAbortsTheWholeTree()
    {
        using var cts = new CancellationTokenSource();

        var t = new ConcurrentTask();
        t.AddTask(new BlockingTask());

        var run = t.RunAsync(cts.Token);
        await cts.CancelAsync();

        Assert.False(await run);
        Assert.Equal(TaskState.AbortedByUser, t.State);
    }

    // ------------------------------------------------------------ lifecycle

    [Fact]
    public async Task StateSettersOnlyFireOnChange()
    {
        var t = new BasicTask();
        var statusEvents = 0;
        t.StatusChanged += (_, _) => statusEvents++;

        t.SetStatus("a");
        t.SetStatus("a");
        t.SetStatus("b");

        Assert.Equal(2, statusEvents);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task TaskCanBeRerunFromAnEndState()
    {
        var t = new BasicTask();

        Assert.True(await t.RunAsync());
        Assert.True(await t.RunAsync());
        Assert.Equal(TaskState.Succeeded, t.State);
    }

    [Fact]
    public async Task ClearAllowsAConcurrentTaskToBeReused()
    {
        var t = new ConcurrentTask();
        t.AddTask(new BasicTask());

        Assert.True(await t.RunAsync());
        Assert.Single(t.SucceededTasks);

        t.Clear();
        Assert.Empty(t.SucceededTasks);

        t.AddTask(new BasicTask());
        Assert.True(await t.RunAsync());
        Assert.Single(t.SucceededTasks);
    }

    [Fact]
    public async Task IsMultiStepFollowsSubtaskCount()
    {
        var single = new ConcurrentTask();
        single.AddTask(new BasicTask());
        Assert.False(single.IsMultiStep);

        var many = new ConcurrentTask();
        many.AddTask(new BasicTask());
        many.AddTask(new BasicTask());
        Assert.True(many.IsMultiStep);

        await Task.CompletedTask;
    }

    private sealed class ThrowingTask : LauncherTask
    {
        protected override Task ExecuteAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private sealed class BlockingTask : LauncherTask
    {
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
            => await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
}
