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
 * The launch itself is already proven — the headless CLI starts a game with it. What these check is
 * the half that belongs to the screen: what the button is allowed to do, what the user is told while
 * it happens, and what happens when it fails.
 *
 * A real launch needs a metadata server, a JVM and several hundred megabytes of downloads. None of
 * that belongs in a unit test, while the decisions here are exactly the ones that break.
 */

using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class LaunchCoordinatorTests
{
    /// <summary>A launch that does whatever the test tells it to.</summary>
    private sealed class FakeLauncher(Func<IProgressSink, CancellationToken, Task>? body = null) : IInstanceLauncher
    {
        public List<string> Launched { get; } = [];

        public string? LastServer { get; private set; }

        public Task LaunchAsync(string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
        {
            Launched.Add(instanceId);
            LastServer = server;

            return body?.Invoke(progress, cancellationToken) ?? Task.CompletedTask;
        }
    }

    // ================================================================== the happy path

    [Fact]
    public async Task ASuccessfulLaunchReportsItselfDone()
    {
        var launcher = new FakeLauncher((progress, _) =>
        {
            progress.SetStatus("Downloading libraries");
            progress.SetProgress(30, 60);
            progress.Log("started");

            return Task.CompletedTask;
        });

        var coordinator = new LaunchCoordinator(launcher);

        await coordinator.LaunchAsync("MyPack").ConfigureAwait(true);

        Assert.Equal(["MyPack"], launcher.Launched);
        Assert.False(coordinator.IsBusy);
        Assert.Equal("Game closed", coordinator.Status);
        Assert.Equal(string.Empty, coordinator.Failure);
        Assert.Contains(coordinator.LogLines, l => l.Text == "started");
    }

    [Fact]
    public async Task ProgressIsReportedAsAFraction()
    {
        var coordinator = new LaunchCoordinator(new FakeLauncher((progress, _) =>
        {
            progress.SetProgress(30, 60);

            return Task.CompletedTask;
        }));

        await coordinator.LaunchAsync("MyPack").ConfigureAwait(true);

        Assert.Equal(0.5, coordinator.Progress);
    }

    /// <summary>Work with no known total has no fraction, rather than a misleading zero.</summary>
    [Fact]
    public async Task WorkWithNoTotalHasNoProgress()
    {
        var coordinator = new LaunchCoordinator(new FakeLauncher((progress, _) =>
        {
            progress.SetProgress(5, 0);

            return Task.CompletedTask;
        }));

        await coordinator.LaunchAsync("MyPack").ConfigureAwait(true);

        Assert.Null(coordinator.Progress);
    }

    // ================================================================== failing

    /*
     * EVERY EXCEPTION IS CAUGHT, not a chosen few. This is the last place a launch failure can be
     * caught before it reaches a UI thread and takes the window with it -- and a launcher that
     * vanishes when a mod pack is broken is worse than one that says what happened.
     */
    [Fact]
    public async Task AFailureIsReportedAndTheCoordinatorRecovers()
    {
        var coordinator = new LaunchCoordinator(
            new FakeLauncher((_, _) => throw new InvalidOperationException("no Java found")));

        await coordinator.LaunchAsync("MyPack").ConfigureAwait(true);

        Assert.False(coordinator.IsBusy);
        Assert.Equal("no Java found", coordinator.Failure);
        Assert.Equal("Launch failed", coordinator.Status);
        Assert.Contains(coordinator.LogLines, l => l.IsError && l.Text == "no Java found");
    }

    /// <summary>The failure has to outlive the run that produced it, or nobody sees why it stopped.</summary>
    [Fact]
    public async Task AFailureIsClearedOnlyWhenTheNextLaunchStarts()
    {
        var failing = new LaunchCoordinator(new FakeLauncher((_, _) => throw new InvalidOperationException("bad")));

        await failing.LaunchAsync("A").ConfigureAwait(true);

        Assert.Equal("bad", failing.Failure);

        // Still there after the run ended; the window can show it for as long as it likes.
        Assert.False(failing.IsBusy);
        Assert.Equal("bad", failing.Failure);
    }

    /// <summary>Cancelling is not a failure — telling the user it went wrong would be a lie.</summary>
    [Fact]
    public async Task CancellingIsNotReportedAsAFailure()
    {
        var started = new TaskCompletionSource();

        var coordinator = new LaunchCoordinator(new FakeLauncher(async (_, token) =>
        {
            started.SetResult();

            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }));

        var launch = coordinator.LaunchAsync("MyPack");

        await started.Task.ConfigureAwait(true);
        coordinator.Cancel();
        await launch.ConfigureAwait(true);

        Assert.Equal("Cancelled", coordinator.Status);
        Assert.Equal(string.Empty, coordinator.Failure);
        Assert.DoesNotContain(coordinator.LogLines, l => l.IsError);
    }

    // ================================================================== one at a time

    /*
     * A second launch while one is running would write to the same instance directory -- extracting
     * natives into a folder the first is reading. Refused rather than queued or run in parallel.
     */
    [Fact]
    public async Task ASecondLaunchIsRefusedWhileOneIsRunning()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var launcher = new FakeLauncher(async (_, _) =>
        {
            started.SetResult();

            await release.Task.ConfigureAwait(false);
        });

        var coordinator = new LaunchCoordinator(launcher);

        var first = coordinator.LaunchAsync("A");
        await started.Task.ConfigureAwait(true);

        await coordinator.LaunchAsync("B").ConfigureAwait(true);

        release.SetResult();
        await first.ConfigureAwait(true);

        Assert.Equal(["A"], launcher.Launched);
    }

    [Fact]
    public async Task LaunchingNothingDoesNothing()
    {
        var launcher = new FakeLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        await coordinator.LaunchAsync(string.Empty).ConfigureAwait(true);

        Assert.Empty(launcher.Launched);
        Assert.False(coordinator.IsBusy);
    }

    // ================================================================== the log

    /*
     * A modded game writes tens of thousands of lines to standard output. A launcher that keeps every
     * one of them in a UI collection has a memory leak with a progress bar on it.
     */
    [Fact]
    public async Task TheLogIsCappedAndKeepsTheMostRecentLines()
    {
        var coordinator = new LaunchCoordinator(new FakeLauncher((progress, _) =>
        {
            for (var i = 0; i < 5100; i++)
            {
                progress.Log($"line {i}");
            }

            return Task.CompletedTask;
        }));

        await coordinator.LaunchAsync("MyPack").ConfigureAwait(true);

        Assert.Equal(5000, coordinator.LogLines.Count);

        // The oldest went, the newest stayed -- the wrong end would make the log useless.
        Assert.DoesNotContain(coordinator.LogLines, l => l.Text == "line 0");
        Assert.Contains(coordinator.LogLines, l => l.Text == "line 5099");
    }

    [Fact]
    public async Task EachLaunchStartsWithAnEmptyLog()
    {
        var coordinator = new LaunchCoordinator(new FakeLauncher((progress, _) =>
        {
            progress.Log("hello");

            return Task.CompletedTask;
        }));

        await coordinator.LaunchAsync("A").ConfigureAwait(true);
        await coordinator.LaunchAsync("B").ConfigureAwait(true);

        Assert.Single(coordinator.LogLines);
    }

    // ================================================================== joining a server

    [Fact]
    public async Task TheServerJoinerLaunchesTheInstanceIntoThatServer()
    {
        var launcher = new FakeLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        var joiner = new CoordinatorServerJoiner(coordinator, "pack");

        await joiner.JoinAsync("play.example.net:25566").ConfigureAwait(true);

        Assert.Equal(["pack"], launcher.Launched);
        Assert.Equal("play.example.net:25566", launcher.LastServer);
    }

    [Fact]
    public async Task APlainLaunchCarriesNoServer()
    {
        var launcher = new FakeLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        await coordinator.LaunchAsync("pack").ConfigureAwait(true);

        Assert.Null(launcher.LastServer);
    }
}
