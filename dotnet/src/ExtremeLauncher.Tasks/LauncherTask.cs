// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2022 flowln <flowlnlnln@gmail.com>
 *  Copyright (c) 2023 Rachel Powers <508861+Ryex@users.noreply.github.com>
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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Rewritten from launcher/tasks/Task.{h,cpp}.
 *
 * ============================ THIS IS A REWRITE, NOT A PORT ============================
 *
 * The C++ Task is a QObject + QRunnable driven by signals: start() runs executeTask(), which
 * eventually calls emitSucceeded()/emitFailed()/emitAborted(), and observers connect to the
 * started/progress/status/details/succeeded/failed/aborted/finished signals. Nearly every long-running
 * operation in the launcher derives from it, so this shape propagates everywhere.
 *
 * Here that becomes: an awaitable RunAsync() driven by ExecuteAsync(), with a CancellationToken for
 * aborts and events for observation. Three deliberate decisions, since they set the shape of waves
 * 3-10:
 *
 *  1. FAILURE IS A RETURN VALUE, NOT AN EXCEPTION AT THE BOUNDARY.
 *     RunAsync returns Task<bool>. Subclasses signal failure by throwing TaskFailedException from
 *     ExecuteAsync, which is idiomatic *inside* a task, but RunAsync catches it and records state.
 *     This is what makes composites cheap: ConcurrentTask must collect several failures without any
 *     one of them unwinding its loop, exactly as the C++ collects into m_failed.
 *
 *  2. CANCELLATION ALSO RETURNS FALSE INSTEAD OF THROWING.
 *     A deliberate divergence from the .NET convention that cancellation propagates as
 *     OperationCanceledException. The C++ models AbortedByUser as a third end state that callers
 *     inspect, and the UI depends on telling "aborted" apart from "failed". Callers read State.
 *
 *  3. STATE IS STILL INSPECTABLE AFTER THE FACT.
 *     State/FailReason/Warnings/WasSuccessful all survive the run, because the UI reads them after
 *     the task finishes rather than only reacting to signals.
 *
 * RENAME: `Task` would collide with System.Threading.Tasks.Task in every single file. Called
 * LauncherTask here; subclasses keep their upstream names (ConcurrentTask, SequentialTask, ...).
 *
 * THREAD SAFETY: the C++ notes that ConcurrentTask is not thread-safe -- it relied on everything
 * being marshalled onto one Qt event loop via QueuedConnection. Real concurrency here means shared
 * state genuinely needs locking; see ConcurrentTask.
 */

namespace ExtremeLauncher.Tasks;

public enum TaskState
{
    Inactive,
    Running,
    Succeeded,
    Failed,
    AbortedByUser,
}

public enum TaskStepState
{
    Waiting,
    Running,
    Failed,
    Succeeded,
}

/// <summary>Progress for one step of a multi-step task.</summary>
public sealed class TaskStepProgress
{
    public TaskStepProgress()
    {
    }

    public TaskStepProgress(Guid uid) => Uid = uid;

    public Guid Uid { get; init; } = Guid.NewGuid();

    public long Current { get; set; }

    public long Total { get; set; } = -1;

    public long OldCurrent { get; set; }

    public long OldTotal { get; set; } = -1;

    public string Status { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public TaskStepState State { get; set; } = TaskStepState.Waiting;

    public bool IsDone => State is TaskStepState.Failed or TaskStepState.Succeeded;

    public void Update(long newCurrent, long newTotal)
    {
        OldCurrent = Current;
        OldTotal = Total;

        Current = newCurrent;
        Total = newTotal;
        State = TaskStepState.Running;
    }

    public TaskStepProgress Clone() => (TaskStepProgress)MemberwiseClone();
}

/// <summary>Thrown from <see cref="LauncherTask.ExecuteAsync"/> to fail a task with a reason.</summary>
public sealed class TaskFailedException : Exception
{
    public TaskFailedException(string message) : base(message)
    {
    }

    public TaskFailedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public readonly record struct TaskProgress(long Current, long Total);

public abstract class LauncherTask
{
    private readonly List<string> _warnings = [];
    private readonly Lock _gate = new();

    protected LauncherTask(string? name = null) => Name = name ?? string.Empty;

    public event EventHandler? Started;

    public event EventHandler? Finished;

    public event EventHandler? Succeeded;

    public event EventHandler<string>? Failed;

    public event EventHandler? Aborted;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<string>? DetailsChanged;

    public event EventHandler<TaskProgress>? ProgressChanged;

    public event EventHandler<TaskStepProgress>? StepProgressChanged;

    public Guid Uid { get; } = Guid.NewGuid();

    public string Name { get; }

    public TaskState State { get; private set; } = TaskState.Inactive;

    public string Status { get; private set; } = string.Empty;

    public string Details { get; private set; } = string.Empty;

    public long Progress { get; private set; }

    public long TotalProgress { get; private set; } = 100;

    public string FailReason { get; private set; } = string.Empty;

    public bool IsRunning => State == TaskState.Running;

    public bool IsFinished => State is not (TaskState.Running or TaskState.Inactive);

    public bool WasSuccessful => State == TaskState.Succeeded;

    public virtual bool IsMultiStep => false;

    public virtual bool CanAbort => false;

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return [.. _warnings];
            }
        }
    }

    public virtual IReadOnlyList<TaskStepProgress> GetStepProgress() => [];

    /// <summary>Runs the task to completion.</summary>
    /// <returns>
    /// <see langword="true"/> on success; <see langword="false"/> on failure or cancellation. Inspect
    /// <see cref="State"/> to tell those apart, and <see cref="FailReason"/> for the message.
    /// </returns>
    /// <remarks>
    /// Re-runnable from any end state, matching the C++ <c>start()</c>. Calling it while already
    /// running is a no-op that returns <see langword="false"/>.
    /// </remarks>
    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        if (State == TaskState.Running)
        {
            // Upstream warns and returns rather than starting a second run.
            return false;
        }

        State = TaskState.Running;
        FailReason = string.Empty;
        Started?.Invoke(this, EventArgs.Empty);

        try
        {
            await ExecuteAsync(cancellationToken).ConfigureAwait(false);

            State = TaskState.Succeeded;
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            State = TaskState.AbortedByUser;
            FailReason = "Aborted.";
            Aborted?.Invoke(this, EventArgs.Empty);
        }
        catch (TaskFailedException e)
        {
            State = TaskState.Failed;
            FailReason = e.Message;
            Failed?.Invoke(this, e.Message);
        }
        catch (Exception e)
        {
            // An unexpected throw is a failure, not a crash: a single bad mod file should not take
            // down a whole download batch.
            State = TaskState.Failed;
            FailReason = e.Message;
            Failed?.Invoke(this, e.Message);
        }
        finally
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }

        return State == TaskState.Succeeded;
    }

    /// <summary>The actual work.</summary>
    /// <remarks>
    /// Return normally to succeed. Throw <see cref="TaskFailedException"/> to fail with a reason, or
    /// let an <see cref="OperationCanceledException"/> escape to abort.
    /// </remarks>
    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);

    /// <summary>Fires only when the value actually changes, matching the C++ setters.</summary>
    public void SetStatus(string status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Fires only when the value actually changes, matching the C++ setters.</summary>
    public void SetDetails(string details)
    {
        if (Details == details)
        {
            return;
        }

        Details = details;
        DetailsChanged?.Invoke(this, details);
    }

    /// <summary>Fires only when the value actually changes, matching the C++ setters.</summary>
    public void SetProgress(long current, long total)
    {
        if (Progress == current && TotalProgress == total)
        {
            return;
        }

        Progress = current;
        TotalProgress = total;
        ProgressChanged?.Invoke(this, new TaskProgress(current, total));
    }

    protected void LogWarning(string line)
    {
        lock (_gate)
        {
            _warnings.Add(line);
        }
    }

    protected void RaiseStepProgress(TaskStepProgress stepProgress)
        => StepProgressChanged?.Invoke(this, stepProgress);

    public override string ToString()
        => $"{GetType().Name}({(Name.Length == 0 ? Uid.ToString("N") : Name)} ID: {Uid:N})";
}
