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
 * Ported in behaviour from launcher/ui/dialogs/ProgressDialog.cpp.
 *
 * SOMETHING TO LOOK AT WHILE A TASK RUNS. Four things in this launcher take minutes and show nothing
 * at all: importing a pack, installing one from the browser, updating one, and downloading a Java
 * runtime. A 170 MB download behind a window that does not move is indistinguishable from a launcher
 * that has hung, and the usual response to that is to kill it -- half way through writing files.
 *
 * ONE VIEW MODEL FOR ALL OF THEM, because the alternative is four progress bars that behave slightly
 * differently, and the one that behaves worst is the one somebody hits first.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ExtremeLauncher.ViewModels;

/// <summary>What the progress window needs from a running task.</summary>
/// <remarks>
/// An interface rather than LauncherTask itself so this project does not depend on the tasks one,
/// and so a test can drive the window without a real task to run.
/// </remarks>
public interface IRunnableTask
{
    event EventHandler<string>? StatusChanged;

    event EventHandler<(long Current, long Total)>? ProgressChanged;

    /// <summary>Whether stopping it half way is something it can survive.</summary>
    bool CanAbort { get; }

    string FailReason { get; }

    Task<bool> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Runs a task where the user can see it. Implemented by the app.</summary>
/// <remarks>
/// THE VIEW-MODEL-SIDE DOOR TO THE PROGRESS WINDOW. Wave 44 gave three app-level callers a window to
/// run long work behind; a view model cannot use it, because it has no Window to be modal to and no
/// business knowing about one.
///
/// A view model that does slow work and cannot reach this ends up doing what the world copy did:
/// awaiting inline with nothing on screen, which for a several-hundred-megabyte world is the same
/// silence the progress window was built to end.
/// </remarks>
public interface ITaskRunner
{
    /// <param name="title">What the window says it is doing.</param>
    /// <returns>False when the task failed or was cancelled.</returns>
    Task<bool> RunAsync(IRunnableTask task, string title);
}

public sealed partial class TaskProgressViewModel : ObservableObject
{
    private readonly IRunnableTask _task;

    private readonly CancellationTokenSource _cancellation = new();

    public TaskProgressViewModel(IRunnableTask task, string title)
    {
        ArgumentNullException.ThrowIfNull(task);

        _task = task;
        Title = title;

        _task.StatusChanged += (_, status) => Status = status;
        _task.ProgressChanged += (_, progress) => SetProgress(progress.Current, progress.Total);
    }

    public string Title { get; }

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private double _current;

    [ObservableProperty]
    private double _total;

    /// <summary>
    /// Whether the bar can show a real proportion, as opposed to just moving.
    /// </summary>
    /// <remarks>
    /// INDETERMINATE UNTIL A TOTAL ARRIVES. A bar sitting at 0% for the twenty seconds before the
    /// first byte count looks like a stuck one; a bar that is visibly cycling does not.
    /// </remarks>
    public bool IsIndeterminate => Total <= 0;

    /// <summary>What the numbers say, or empty while there are none.</summary>
    public string ProgressText => Total <= 0 ? string.Empty : $"{Percent}%";

    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Math.Round(Current / Total * 100), 0, 100);

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isCancelling;

    /// <summary>Whether this task can be stopped part way.</summary>
    public bool CanCancel => _task.CanAbort && IsRunning && !IsCancelling;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanCancel));

    partial void OnIsCancellingChanged(bool value) => OnPropertyChanged(nameof(CanCancel));

    /// <summary>True when the task finished successfully.</summary>
    public bool Succeeded { get; private set; }

    public string FailReason => _task.FailReason;

    /// <summary>Raised when the task is over, so the window can close itself.</summary>
    public event EventHandler? Finished;

    private void SetProgress(long current, long total)
    {
        Current = current;
        Total = total;

        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(ProgressText));
    }

    /// <summary>Runs the task, reporting as it goes.</summary>
    public async Task<bool> RunAsync()
    {
        IsRunning = true;

        try
        {
            Succeeded = await _task.RunAsync(_cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;

            Finished?.Invoke(this, EventArgs.Empty);
        }

        return Succeeded;
    }

    /// <summary>Asks the task to stop.</summary>
    /// <remarks>
    /// SAYS SO RATHER THAN CLOSING. Cancelling a download is not instant -- the request in flight has
    /// to come back -- and a window that vanishes the moment somebody presses Cancel invites them to
    /// start the same thing again while the first one is still winding down.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (!CanCancel)
        {
            return;
        }

        IsCancelling = true;
        Status = "Stopping…";

        _cancellation.Cancel();
    }

    /// <summary>Whether closing the window should be allowed.</summary>
    /// <remarks>
    /// A TASK THAT CANNOT BE ABORTED MUST NOT BE ABANDONED. Closing the window would leave it writing
    /// files with nothing on screen -- which is the exact situation this window exists to prevent.
    /// </remarks>
    public bool CanCloseWindow => !IsRunning || _task.CanAbort;
}
