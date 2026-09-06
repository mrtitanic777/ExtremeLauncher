// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2022 flowln <flowlnlnln@gmail.com>
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
 * Rewritten from launcher/tasks/{ConcurrentTask,SequentialTask,MultipleOptionsTask}.{h,cpp}.
 *
 * The C++ drives these with a hand-rolled state machine: executeNextSubTask() starts work if there is
 * room, subTaskFinished() records a result and then re-invokes executeNextSubTask() through
 * QMetaObject::invokeMethod(..., Qt::QueuedConnection). That queued hop is not decoration -- it is
 * what stops the chain from recursing when subtasks finish synchronously. tests/Task_test.cpp pins
 * this with test_stackOverflowInConcurrentTask, which queues 4096 instantly-succeeding subtasks and
 * fails if the stack blows.
 *
 * The worker-loop form below is immune to that by construction. Each worker is a `while` loop, and
 * `await` on an already-completed Task resumes inline within the same state-machine invocation, so a
 * synchronously-completing queue drains in one stack frame no matter how long it is. The upstream
 * test is ported anyway, at a higher count, because the property is worth keeping pinned.
 *
 * All three upstream classes are one class plus two behaviour hooks:
 *   ConcurrentTask      max 6 at a time, run everything, fail at the end if anything failed.
 *   SequentialTask      max 1, fail-fast, and report the *subtask's* reason rather than a generic one.
 *   MultipleOptionsTask max 1, stop at the first success, fail only if every option failed.
 */

namespace ExtremeLauncher.Tasks;

public class ConcurrentTask : LauncherTask
{
    private readonly Queue<LauncherTask> _queue = new();
    private readonly List<LauncherTask> _doing = [];
    private readonly List<LauncherTask> _done = [];
    private readonly List<LauncherTask> _failed = [];
    private readonly List<LauncherTask> _succeeded = [];
    private readonly Dictionary<Guid, TaskStepProgress> _stepProgress = [];

    /// <remarks>
    /// The C++ has no equivalent: it relied on every mutation being marshalled onto a single Qt event
    /// loop, and its own header notes "This is not thread-safe". Genuine concurrency here means the
    /// bookkeeping has to be guarded.
    /// </remarks>
    private readonly Lock _gate = new();

    private bool _stopRequested;
    private string? _firstFailureReason;

    public ConcurrentTask(string name = "", int maxConcurrent = 6) : base(name)
        => MaxConcurrent = maxConcurrent;

    /// <summary>Safe to change before the task starts.</summary>
    public int MaxConcurrent { get; set; }

    public override bool CanAbort => true;

    public override bool IsMultiStep => TotalSize > 1;

    public int TotalSize
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count + _doing.Count + _done.Count;
            }
        }
    }

    public IReadOnlyList<LauncherTask> FailedTasks
    {
        get
        {
            lock (_gate)
            {
                return [.. _failed];
            }
        }
    }

    public IReadOnlyList<LauncherTask> SucceededTasks
    {
        get
        {
            lock (_gate)
            {
                return [.. _succeeded];
            }
        }
    }

    /// <summary>Reason reported by the first subtask that failed, if any.</summary>
    protected string? FirstFailureReason
    {
        get
        {
            lock (_gate)
            {
                return _firstFailureReason;
            }
        }
    }

    public void AddTask(LauncherTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            _queue.Enqueue(task);
        }
    }

    /// <summary>Resets internal state so the task can be reused.</summary>
    public void Clear()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Cannot clear a running task.");
        }

        lock (_gate)
        {
            _queue.Clear();
            _doing.Clear();
            _done.Clear();
            _failed.Clear();
            _succeeded.Clear();
            _stepProgress.Clear();
            _stopRequested = false;
            _firstFailureReason = null;
        }

        SetProgress(0, 100);
    }

    public override IReadOnlyList<TaskStepProgress> GetStepProgress()
    {
        lock (_gate)
        {
            return [.. _stepProgress.Values];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Loops so a subclass can refill the queue once it has drained -- NetJob uses this to retry
        // failed downloads. Without a hook here, retry would have to re-enter the whole task.
        while (true)
        {
            var workerCount = Math.Max(1, MaxConcurrent);
            var workers = new Task[workerCount];

            for (var i = 0; i < workerCount; i++)
            {
                workers[i] = RunWorkerAsync(cancellationToken);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!TryRefillQueue())
            {
                break;
            }
        }

        if (!IsSuccessful())
        {
            throw new TaskFailedException(GetFailureMessage());
        }
    }

    /// <summary>
    /// Called once the queue has drained. Return <see langword="true"/> to run another pass.
    /// </summary>
    protected virtual bool TryRefillQueue() => false;

    /// <summary>Moves every failed subtask back onto the queue. Returns how many were requeued.</summary>
    protected int RequeueFailed()
    {
        lock (_gate)
        {
            var count = _failed.Count;

            foreach (var task in _failed)
            {
                _done.Remove(task);
                _queue.Enqueue(task);
            }

            _failed.Clear();
            _stopRequested = false;
            _firstFailureReason = null;

            return count;
        }
    }

    /// <summary>
    /// Pulls from the shared queue until it is empty or a stop is requested. Iterative by design --
    /// see the note at the top of this file.
    /// </summary>
    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            LauncherTask next;

            lock (_gate)
            {
                if (_stopRequested || _queue.Count == 0)
                {
                    return;
                }

                next = _queue.Dequeue();
                _doing.Add(next);
                _stepProgress[next.Uid] = new TaskStepProgress(next.Uid) { State = TaskStepState.Running };
            }

            cancellationToken.ThrowIfCancellationRequested();

            UpdateState();

            var succeeded = await next.RunAsync(cancellationToken).ConfigureAwait(false);

            // An aborted subtask aborts the whole tree; without this it would be recorded as an
            // ordinary failure and the distinction the UI relies on would be lost.
            if (next.State == TaskState.AbortedByUser)
            {
                lock (_gate)
                {
                    _stopRequested = true;
                    _queue.Clear();
                }

                throw new OperationCanceledException(cancellationToken);
            }

            RecordFinished(next, succeeded);
            UpdateState();

            if (ShouldStopAfter(succeeded))
            {
                lock (_gate)
                {
                    _stopRequested = true;
                    _queue.Clear();
                }

                return;
            }
        }
    }

    private void RecordFinished(LauncherTask task, bool succeeded)
    {
        TaskStepProgress? step;

        lock (_gate)
        {
            _doing.Remove(task);
            _done.Add(task);
            (succeeded ? _succeeded : _failed).Add(task);

            if (!succeeded)
            {
                _firstFailureReason ??= task.FailReason;
            }

            if (_stepProgress.Remove(task.Uid, out step))
            {
                step.State = succeeded ? TaskStepState.Succeeded : TaskStepState.Failed;
            }
        }

        if (step is not null)
        {
            RaiseStepProgress(step);
        }
    }

    /// <summary>Whether the composite as a whole counts as successful once the queue has drained.</summary>
    protected virtual bool IsSuccessful()
    {
        lock (_gate)
        {
            return _failed.Count == 0;
        }
    }

    /// <summary>Whether to stop pulling work after a subtask finished with the given outcome.</summary>
    protected virtual bool ShouldStopAfter(bool succeeded) => false;

    protected virtual string GetFailureMessage() => "One or more subtasks failed";

    protected virtual void UpdateState()
    {
        int queued, doing, done, total;

        lock (_gate)
        {
            queued = _queue.Count;
            doing = _doing.Count;
            done = _done.Count;
            total = queued + doing + done;
        }

        if (total > 1)
        {
            SetProgress(done, total);
            SetStatus($"Executing {doing} task(s) ({done} out of {total} are done)");
            return;
        }

        SetStatus(queued > 0 ? "Waiting for a task to start..."
            : doing > 0 ? "Executing 1 task:"
            : done > 0 ? "Task finished."
            : "Please wait...");
    }

    private protected (int Queued, int Doing, int Done) Counts()
    {
        lock (_gate)
        {
            return (_queue.Count, _doing.Count, _done.Count);
        }
    }
}

/// <summary>Runs subtasks one at a time, stopping at the first failure.</summary>
public class SequentialTask : ConcurrentTask
{
    public SequentialTask(string name = "") : base(name, maxConcurrent: 1)
    {
    }

    /// <summary>Fail-fast: upstream calls emitFailed() the moment a subtask fails.</summary>
    protected override bool ShouldStopAfter(bool succeeded) => !succeeded;

    /// <summary>Upstream reports the failing subtask's own message, not a generic one.</summary>
    protected override string GetFailureMessage()
        => FirstFailureReason is { Length: > 0 } reason ? reason : base.GetFailureMessage();

    protected override void UpdateState()
    {
        var (queued, doing, done) = Counts();
        var total = queued + doing + done;

        SetProgress(done, total);
        SetStatus($"Executing task {doing + done} out of {total}");
    }
}

/// <summary>Tries subtasks one at a time until one succeeds.</summary>
public class MultipleOptionsTask : ConcurrentTask
{
    public MultipleOptionsTask(string name = "") : base(name, maxConcurrent: 1)
    {
    }

    /// <summary>The first success wins; remaining options are never attempted.</summary>
    protected override bool ShouldStopAfter(bool succeeded) => succeeded;

    /// <summary>Succeeds if any option worked, unlike the base class which requires all of them.</summary>
    protected override bool IsSuccessful() => SucceededTasks.Count > 0;

    protected override string GetFailureMessage() => "All attempts have failed!";

    protected override void UpdateState()
    {
        var (queued, doing, done) = Counts();
        var total = queued + doing + done;

        SetProgress(done, total);
        SetStatus($"Attempting task {doing + done} out of {total}");
    }
}
