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
 * Ported from the parts of launcher/LaunchController.cpp that decide things, less the dialogs.
 *
 * WHAT THE WINDOW DOES WHEN SOMEONE PRESSES PLAY. The launch itself is already ported and already
 * proven -- the headless CLI starts a game with it. What is here is the half that belongs to the
 * screen: what the button is allowed to do, what the user is told while it happens, and what happens
 * when it fails.
 *
 * THE LAUNCH IS BEHIND AN INTERFACE so this can be tested. Not for purity: a real launch needs a
 * metadata server, a JVM and several hundred megabytes of downloads, none of which belong in a unit
 * test -- while the decisions here (is the button enabled, does a failure clear the busy state, does
 * cancelling say so) are exactly the ones that break and are cheap to check.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ExtremeLauncher.ViewModels;

/// <summary>
/// How a long-running job reports itself back to the screen.
/// </summary>
/// <remarks>
/// Shared by launching and by copying rather than named for either. Both run a LauncherTask off the UI
/// thread and both feed something the window is bound to, so both need the same three things said.
/// </remarks>
public interface IProgressSink
{
    void SetStatus(string status);

    void SetProgress(long current, long total);

    void Log(string line, bool isError = false);
}

/// <summary>Starting an instance. Implemented for real by the app, faked by the tests.</summary>
public interface IInstanceLauncher
{
    /// <param name="server">
    /// A server to join on start, as "host" or "host:port", or null to launch normally. Used by the
    /// servers page's Join; the command-line launch supplies its own.
    /// </param>
    Task LaunchAsync(
        string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null);
}

/// <summary>Launches an instance straight into a server. Implemented by the app over a coordinator.</summary>
public interface IServerJoiner
{
    Task JoinAsync(string address);
}

/// <summary>Joins a server by asking one instance's coordinator to launch into it.</summary>
public sealed class CoordinatorServerJoiner(LaunchCoordinator coordinator, string instanceId) : IServerJoiner
{
    public Task JoinAsync(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return coordinator.LaunchAsync(instanceId, address);
    }
}

/// <summary>One line of the launch log.</summary>
/// <param name="IsError">Whether it should be shown as a failure rather than as progress.</param>
public sealed record LaunchLogLine(string Text, bool IsError);

public sealed partial class LaunchCoordinator : ObservableObject, IProgressSink
{
    private readonly IInstanceLauncher _launcher;

    private CancellationTokenSource? _cancellation;

    public LaunchCoordinator(IInstanceLauncher launcher) => _launcher = launcher;

    /// <summary>Whether a launch is under way. The window binds most of its state to this.</summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>0 to 1, or null when the work has no known total.</summary>
    [ObservableProperty]
    private double? _progress;

    /// <summary>Set when a launch failed, and cleared when the next one starts.</summary>
    [ObservableProperty]
    private string _failure = string.Empty;

    /// <summary>The launch log, capped. See <see cref="Log"/>.</summary>
    public ObservableCollection<LaunchLogLine> LogLines { get; } = [];

    /// <summary>The instance being launched, or empty.</summary>
    [ObservableProperty]
    private string _instanceId = string.Empty;

    /// <summary>
    /// Starts an instance.
    /// </summary>
    /// <remarks>
    /// ONE AT A TIME. A second launch while one is running is refused rather than queued or run in
    /// parallel: both would write to the same instance directory, and the second would be extracting
    /// natives into a folder the first is reading.
    /// </remarks>
    /// <param name="server">A server to join on start, or null to launch normally.</param>
    public async Task LaunchAsync(string instanceId, string? server = null)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        if (IsBusy || instanceId.Length == 0)
        {
            return;
        }

        // Cleared at the START of a launch, not the end: a failure message has to outlive the run that
        // produced it, or the user never sees why the last attempt stopped.
        Failure = string.Empty;
        LogLines.Clear();
        Progress = null;
        InstanceId = instanceId;
        IsBusy = true;

        _cancellation = new CancellationTokenSource();

        try
        {
            await _launcher.LaunchAsync(instanceId, this, _cancellation.Token, server).ConfigureAwait(true);

            SetStatus("Game closed");
        }
        catch (OperationCanceledException)
        {
            // Not a failure: the user asked for this, and telling them it went wrong would be a lie.
            SetStatus("Cancelled");
            Log("Launch cancelled.");
        }
        catch (Exception e)
        {
            /*
             * Every exception, not a chosen few. This is the last place a launch failure can be caught
             * before it reaches a UI thread and takes the window with it -- and a launcher that
             * vanishes when a mod pack is broken is worse than one that says what happened.
             */
            Failure = e.Message;
            SetStatus("Launch failed");
            Log(e.Message, isError: true);
        }
        finally
        {
            IsBusy = false;

            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Asks the running launch to stop.</summary>
    [RelayCommand]
    public void Cancel() => _cancellation?.Cancel();

    public void SetStatus(string status) => Status = status;

    public void SetProgress(long current, long total)
        => Progress = total > 0 ? Math.Clamp((double)current / total, 0, 1) : null;

    public void Log(string line, bool isError = false)
    {
        /*
         * A bound cap rather than an unbounded list. A modded game writes tens of thousands of lines
         * to standard output, and a launcher that keeps every one of them in a UI collection has a
         * memory leak with a progress bar on it.
         */
        const int MaxLines = 5000;

        if (LogLines.Count >= MaxLines)
        {
            LogLines.RemoveAt(0);
        }

        LogLines.Add(new LaunchLogLine(line, isError));
    }
}
