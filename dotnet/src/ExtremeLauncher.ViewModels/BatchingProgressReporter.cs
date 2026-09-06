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
 * CARRIES A LONG JOB'S PROGRESS FROM WORKER THREADS ONTO THE UI THREAD.
 *
 * Used by launching and by copying, which is why neither is in the name any more. Both run a
 * LauncherTask off the UI thread, and both feed something the window is bound to.
 *
 * LauncherTask events fire on whatever thread did the work, and what they feed here is bound to
 * controls. Touching an ObservableCollection from a worker thread is a crash in Avalonia, exactly as
 * it was in Qt and for the same reason.
 *
 * BATCHED, NOT POSTED PER EVENT. A modded game writes tens of thousands of lines to standard output
 * during startup, and a download reports progress per chunk. One dispatcher post each would queue work
 * faster than the UI thread can retire it and the window would stop repainting -- the classic way a
 * launcher appears to hang at exactly the moment it is busiest. So workers append under a lock, and a
 * single drain is posted only if one is not already pending; bursts collapse into one UI update.
 *
 * THE POST IS INJECTED rather than calling Dispatcher directly. Not for purity: this is threading code
 * whose failure mode is a crash under load, and against a real dispatcher it can only be tested by
 * starting a UI. With the post as a parameter, every rule below -- coalescing, ordering, the buffer
 * cap, what a drain leaves behind -- is checkable in a plain unit test.
 */

using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

public sealed class BatchingProgressReporter : ILaunchReporter, IDisposable
{
    private readonly IProgressSink _progress;

    private readonly Action<Action> _post;

    private readonly Lock _gate = new();

    private readonly List<LaunchLogLine> _lines = [];

    private string? _status;

    private (long Current, long Total)? _progressValue;

    private bool _drainPending;

    private bool _disposed;

    /// <param name="post">Runs an action on the UI thread. Expected to return immediately.</param>
    /// <remarks>
    /// Pass <c>a =&gt; a()</c> to run everything inline, which is what a test or a headless caller
    /// wants -- there is no UI thread to get onto.
    /// </remarks>
    public BatchingProgressReporter(IProgressSink progress, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(post);

        _progress = progress;
        _post = post;
    }

    /// <summary>
    /// How many lines may wait for the UI thread before further ones are dropped.
    /// </summary>
    /// <remarks>
    /// A bound on the BUFFER, separate from the coordinator's bound on the log itself. Without it a
    /// game logging faster than the UI thread drains would grow this list without limit, which is the
    /// same leak one layer earlier.
    /// </remarks>
    public const int MaxBuffered = 10_000;

    /// <summary>How many lines have been dropped because the buffer was full.</summary>
    /// <remarks>
    /// Counted rather than silently discarded. A log that is missing lines and does not say so is
    /// worse than one that is missing lines and does.
    /// </remarks>
    public int DroppedLines { get; private set; }

    public void Status(string status)
    {
        lock (_gate)
        {
            // Only the latest is kept: an intermediate status nobody saw is not worth a frame.
            _status = status;
        }

        Schedule();
    }

    public void Progress(long current, long total)
    {
        lock (_gate)
        {
            _progressValue = (current, total);
        }

        Schedule();
    }

    public void Line(string text, bool isError = false)
    {
        lock (_gate)
        {
            if (_lines.Count >= MaxBuffered)
            {
                DroppedLines++;
            }
            else
            {
                _lines.Add(new LaunchLogLine(text, isError));
            }
        }

        Schedule();
    }

    /// <summary>Delivers whatever is buffered, on the calling thread.</summary>
    /// <remarks>Called on the UI thread once the launch has finished, so nothing is left behind.</remarks>
    public void Flush() => Drain();

    /// <summary>Stops any further drains being scheduled.</summary>
    /// <remarks>
    /// A drain posted after the launch ends would run against a coordinator that has already reported
    /// itself finished, and append the tail of a dead launch's log to whatever came next.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_drainPending || _disposed)
            {
                return;
            }

            _drainPending = true;
        }

        _post(Drain);
    }

    private void Drain()
    {
        string? status;
        (long Current, long Total)? progressValue;
        LaunchLogLine[] lines;

        lock (_gate)
        {
            status = _status;
            progressValue = _progressValue;
            lines = [.. _lines];

            _status = null;
            _progressValue = null;
            _lines.Clear();
            _drainPending = false;
        }

        /*
         * Outside the lock: these run the window's code, and holding a lock across a callback that
         * can itself report progress is how a deadlock gets built.
         */
        if (status is not null)
        {
            _progress.SetStatus(status);
        }

        if (progressValue is { } value)
        {
            _progress.SetProgress(value.Current, value.Total);
        }

        foreach (var line in lines)
        {
            _progress.Log(line.Text, line.IsError);
        }
    }
}
