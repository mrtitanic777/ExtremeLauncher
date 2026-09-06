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
 * The progress window through its real visual tree.
 *
 * The view model tests pin the behaviour; these exist for the one thing they cannot see -- whether
 * the bar is actually bound to it. A ProgressBar wired to nothing renders perfectly and sits still,
 * which is indistinguishable from the hang this window exists to rule out.
 */

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class ProgressWindowTests : IDisposable
{
    private readonly List<Window> _windows = [];

    public void Dispose()
    {
        foreach (var window in _windows)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class StubTask(bool canAbort = true) : IRunnableTask
    {
        private readonly TaskCompletionSource<bool> _finish = new();

        public event EventHandler<string>? StatusChanged;

        public event EventHandler<(long Current, long Total)>? ProgressChanged;

        public bool CanAbort { get; } = canAbort;

        public string FailReason => string.Empty;

        public Task<bool> RunAsync(CancellationToken cancellationToken) => _finish.Task;

        public void Report(string status) => StatusChanged?.Invoke(this, status);

        public void Report(long current, long total) => ProgressChanged?.Invoke(this, (current, total));

        public void Finish() => _finish.TrySetResult(true);
    }

    private (ProgressWindow Window, TaskProgressViewModel Model) Show(StubTask task, string title = "Doing a thing")
    {
        var model = new TaskProgressViewModel(task, title);
        var window = new ProgressWindow(model);

        _windows.Add(window);

        window.Show();

        Settle(window);

        return (window, model);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static ProgressBar Bar(Window window)
        => window.GetLogicalDescendants().OfType<ProgressBar>().Single();

    [AvaloniaFact]
    public void TheBarFollowsTheTask()
    {
        /*
         * THE ONE THING ONLY A UI TEST CAN SEE. A ProgressBar bound to nothing renders perfectly and
         * sits at zero, which looks exactly like the hang this window exists to rule out -- and every
         * view model assertion still passes.
         */
        var task = new StubTask();
        var (window, model) = Show(task);

        var run = model.RunAsync();

        task.Report(25, 100);

        Settle(window);

        var bar = Bar(window);

        Assert.False(bar.IsIndeterminate);
        Assert.Equal(25, bar.Value);
        Assert.Equal(100, bar.Maximum);

        task.Finish();

        _ = run;
    }

    [AvaloniaFact]
    public void TheBarCyclesUntilThereIsATotal()
    {
        var task = new StubTask();
        var (window, model) = Show(task);

        var run = model.RunAsync();

        Settle(window);

        Assert.True(Bar(window).IsIndeterminate);

        task.Finish();

        _ = run;
    }

    [AvaloniaFact]
    public void TheStatusLineIsOnScreen()
    {
        // The useful half: a bar says it is alive, the status says what it is doing.
        var task = new StubTask();
        var (window, model) = Show(task);

        var run = model.RunAsync();

        task.Report("Downloading sodium");

        Settle(window);

        var texts = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        Assert.Contains("Downloading sodium", texts);
        Assert.Contains("Doing a thing", texts);

        task.Finish();

        _ = run;
    }

    [AvaloniaFact]
    public void CancelIsOnlyThereForWorkThatCanBeStopped()
    {
        var stoppable = new StubTask(canAbort: true);
        var (window, model) = Show(stoppable);

        var run = model.RunAsync();

        Settle(window);

        var button = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel");

        Assert.True(button.IsVisible);

        stoppable.Finish();

        _ = run;
    }

    [AvaloniaFact]
    public void CancelIsHiddenForWorkThatCannotBeStopped()
    {
        // A Cancel button that does nothing is worse than none.
        var fixedTask = new StubTask(canAbort: false);
        var (window, model) = Show(fixedTask);

        var run = model.RunAsync();

        Settle(window);

        var button = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel");

        Assert.False(button.IsVisible);

        fixedTask.Finish();

        _ = run;
    }
}
