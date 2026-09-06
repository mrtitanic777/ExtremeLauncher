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
 * Lets a view model run a task behind the progress window.
 */

using Avalonia.Controls;
using Avalonia.Threading;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppTaskRunner(Func<Window?> owner) : ITaskRunner
{
    public async Task<bool> RunAsync(IRunnableTask task, string title)
    {
        ArgumentNullException.ThrowIfNull(task);

        var model = new TaskProgressViewModel(new OnUiThread(task), title);

        if (owner() is not { } parent)
        {
            return await model.RunAsync().ConfigureAwait(true);
        }

        var window = new ProgressWindow(model);

        var run = model.RunAsync();

        await window.ShowDialog(parent).ConfigureAwait(true);

        return await run.ConfigureAwait(true);
    }

    /// <summary>Posts a task's events to the UI thread, and runs it off it.</summary>
    /// <remarks>
    /// THE SAME MARSHALLING ProgressWindow.RunAsync DOES for a LauncherTask, and for the same reason:
    /// a task reports from whatever thread it is running on, and touching a bound property from there
    /// is the classic crash that only happens on somebody else's machine.
    /// </remarks>
    private sealed class OnUiThread(IRunnableTask inner) : IRunnableTask
    {
        public event EventHandler<string>? StatusChanged;

        public event EventHandler<(long Current, long Total)>? ProgressChanged;

        public bool CanAbort => inner.CanAbort;

        public string FailReason => inner.FailReason;

        public Task<bool> RunAsync(CancellationToken cancellationToken)
        {
            inner.StatusChanged += (_, status) =>
                Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(this, status));

            inner.ProgressChanged += (_, progress) =>
                Dispatcher.UIThread.Post(() => ProgressChanged?.Invoke(this, progress));

            // Off the UI thread, or the window it is reporting to would never repaint.
            return Task.Run(() => inner.RunAsync(cancellationToken), cancellationToken);
        }
    }
}
