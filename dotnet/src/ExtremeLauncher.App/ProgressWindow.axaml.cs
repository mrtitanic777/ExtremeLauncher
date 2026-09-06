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
 * The window around TaskProgressViewModel, and the one place that gets a background task's events
 * onto the UI thread.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ExtremeLauncher.Tasks;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class ProgressWindow : Window
{
    private TaskProgressViewModel? _model;

    public ProgressWindow() => InitializeComponent();

    public ProgressWindow(TaskProgressViewModel model) : this()
    {
        _model = model;
        DataContext = model;

        // Closes itself the moment the work is over. Nobody wants to dismiss a dialog that is only
        // there to say "still going".
        model.Finished += (_, _) => Dispatcher.UIThread.Post(Close);
    }

    /// <summary>
    /// Runs a task with this window in front of <paramref name="owner"/>.
    /// </summary>
    /// <remarks>
    /// THE MARSHALLING LIVES HERE. A LauncherTask raises its events on whatever thread it happens to
    /// be running on, and touching a bound property from there is the classic way to get a crash that
    /// only happens on somebody else's machine. The adapter below posts every one to the UI thread.
    /// </remarks>
    public static async Task<bool> RunAsync(Window? owner, LauncherTask task, string title)
    {
        ArgumentNullException.ThrowIfNull(task);

        var model = new TaskProgressViewModel(new TaskAdapter(task), title);

        if (owner is null)
        {
            // No window to be modal to -- a headless or command-line caller. Still runs.
            return await model.RunAsync().ConfigureAwait(true);
        }

        var window = new ProgressWindow(model);

        var run = model.RunAsync();

        await window.ShowDialog(owner).ConfigureAwait(true);

        return await run.ConfigureAwait(true);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        /*
         * A task that cannot be aborted must not be abandoned: closing the window would leave it
         * writing files with nothing on screen, which is exactly what this window exists to prevent.
         */
        Closing += (_, e) =>
        {
            if (_model is { CanCloseWindow: false })
            {
                e.Cancel = true;
            }
        };
    }

    /// <summary>Bridges a LauncherTask to the view model, on the UI thread.</summary>
    private sealed class TaskAdapter(LauncherTask task) : IRunnableTask
    {
        public event EventHandler<string>? StatusChanged;

        public event EventHandler<(long Current, long Total)>? ProgressChanged;

        public bool CanAbort => task.CanAbort;

        public string FailReason => task.FailReason;

        public Task<bool> RunAsync(CancellationToken cancellationToken)
        {
            task.StatusChanged += (_, status) =>
                Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(this, status));

            task.ProgressChanged += (_, progress) =>
                Dispatcher.UIThread.Post(() => ProgressChanged?.Invoke(this, (progress.Current, progress.Total)));

            // Off the UI thread, or the window it is reporting to would never repaint.
            return Task.Run(() => task.RunAsync(cancellationToken), cancellationToken);
        }
    }
}
