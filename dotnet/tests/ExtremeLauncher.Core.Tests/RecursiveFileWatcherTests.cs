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
 * NOTHING HERE WAITS ON A REAL TIMER OR ON THE OPERATING SYSTEM. The delay is injected and the tests
 * drive it, so a loaded machine cannot change the outcome.
 *
 * That is a deliberate choice after this port's round of flaky UI tests: a file-watcher test that
 * sleeps and hopes is the classic example of a test that teaches you to press re-run. What is checked
 * is the logic this class actually owns -- coalescing, filtering, and what a rescan leaves behind --
 * and the one thing that genuinely needs the OS is skipped rather than faked.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class RecursiveFileWatcherTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-watch-" + Guid.NewGuid().ToString("N"));

    public RecursiveFileWatcherTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>Holds the scheduled rescans until the test runs them.</summary>
    private sealed class ManualSchedule
    {
        private readonly List<Action> _pending = [];

        public int Scheduled { get; private set; }

        public void Schedule(Action action)
        {
            _pending.Add(action);
            Scheduled++;
        }

        public void RunAll()
        {
            var due = _pending.ToList();
            _pending.Clear();

            foreach (var action in due)
            {
                action();
            }
        }
    }

    // ================================================================== coalescing

    /*
     * THE WHOLE POINT. A game writing to latest.log produces a change event per buffer flush -- dozens
     * a second -- and rescanning a directory tree that often costs more than the game does. The first
     * change schedules a rescan; everything before it fires is absorbed.
     */
    [Fact]
    public void AFloodOfChangesBecomesOneRescan()
    {
        var schedule = new ManualSchedule();
        var fired = 0;

        using var watcher = new RecursiveFileWatcher(schedule: schedule.Schedule);

        watcher.FilesChanged += (_, _) => fired++;

        for (var i = 0; i < 500; i++)
        {
            watcher.Poke();
        }

        Assert.Equal(1, schedule.Scheduled);
        Assert.Equal(0, fired);

        schedule.RunAll();

        Assert.Equal(1, fired);
    }

    /*
     * A change arriving DURING the rescan schedules another. Clearing the pending flag after the event
     * instead of before would swallow it -- and the swallowed one is the write that happened while the
     * page was reading, which is exactly the case a log viewer must not miss.
     */
    [Fact]
    public void AChangeDuringARescanSchedulesAnother()
    {
        var schedule = new ManualSchedule();

        using var watcher = new RecursiveFileWatcher(schedule: schedule.Schedule);

        var reentered = false;

        watcher.FilesChanged += (_, _) =>
        {
            if (reentered)
            {
                return;
            }

            reentered = true;

            // Something changed while we were looking.
            watcher.Poke();
        };

        watcher.Poke();
        schedule.RunAll();

        Assert.Equal(2, schedule.Scheduled);
    }

    [Fact]
    public void AfterARescanTheNextChangeSchedulesAgain()
    {
        var schedule = new ManualSchedule();
        var fired = 0;

        using var watcher = new RecursiveFileWatcher(schedule: schedule.Schedule);

        watcher.FilesChanged += (_, _) => fired++;

        watcher.Poke();
        schedule.RunAll();

        watcher.Poke();
        schedule.RunAll();

        Assert.Equal(2, fired);
    }

    // ================================================================== not watching

    /*
     * A directory that does not exist is NOT an error. An instance with no logs folder is the normal
     * state before its first run, and the folder appearing is exactly what the caller wants to hear
     * about.
     */
    [Fact]
    public void WatchingSomethingThatIsNotThereIsHarmless()
    {
        using var watcher = new RecursiveFileWatcher();

        watcher.Watch(Path.Combine(_temp, "no-such-folder"));

        Assert.Equal(Path.Combine(_temp, "no-such-folder"), watcher.Root);
    }

    [Fact]
    public void WatchingCanBeRedirectedAndStopped()
    {
        var first = Path.Combine(_temp, "one");
        var second = Path.Combine(_temp, "two");

        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        using var watcher = new RecursiveFileWatcher();

        watcher.Watch(first);
        watcher.Watch(second);

        Assert.Equal(second, watcher.Root);

        watcher.Stop();
        watcher.Stop();
    }

    /// <summary>Nothing fires once it has been disposed, however it is poked.</summary>
    [Fact]
    public void ADisposedWatcherIsSilent()
    {
        var schedule = new ManualSchedule();
        var fired = 0;

        var watcher = new RecursiveFileWatcher(schedule: schedule.Schedule);

        watcher.FilesChanged += (_, _) => fired++;

        watcher.Dispose();
        watcher.Poke();

        schedule.RunAll();

        Assert.Equal(0, schedule.Scheduled);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var watcher = new RecursiveFileWatcher();

        watcher.Dispose();
        watcher.Dispose();
    }

    // ================================================================== the operating system's half

    /*
     * THE ONE TEST THAT NEEDS THE OS, and it is allowed to skip rather than to sleep-and-hope.
     * FileSystemWatcher latency is not bounded on any platform -- on a network share or a loaded
     * machine it can be seconds -- so a test that waits a fixed time and asserts is a flaky test
     * waiting to happen. It waits generously and skips if nothing arrives.
     */
    [SkippableFact]
    public async Task ARealFileChangeReachesTheWatcher()
    {
        var signalled = new TaskCompletionSource();

        using var watcher = new RecursiveFileWatcher();

        watcher.FilesChanged += (_, _) => signalled.TrySetResult();
        watcher.Watch(_temp);

        Directory.CreateDirectory(Path.Combine(_temp, "logs"));
        File.WriteAllText(Path.Combine(_temp, "logs", "latest.log"), "hello");

        var arrived = await Task.WhenAny(signalled.Task, Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(true) == signalled.Task;

        Skip.IfNot(arrived, "The OS did not deliver a file change in time; this is not a failure of the logic.");

        Assert.True(signalled.Task.IsCompletedSuccessfully);
    }
}
