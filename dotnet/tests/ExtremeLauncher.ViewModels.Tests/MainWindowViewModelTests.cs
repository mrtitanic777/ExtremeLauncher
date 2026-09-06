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
 * WHAT A BUTTON MAY DO IS A PROPERTY, NOT A SEQUENCE OF setEnabled CALLS. Upstream re-derives it by
 * hand across selectionBad() and a scattering of enable/disable calls that each have to remember the
 * same rules; the bug that produces is a Launch button live for an instance that cannot start.
 *
 * These check the rules once, where they are written once — and check the command guards itself as
 * well, because a command reachable by keyboard, double-click and menu item will eventually be invoked
 * in a state the button was not in.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-mw-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public MainWindowViewModelTests()
    {
        _instances = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(_instances);
    }

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

    private void MakeInstance(string id, string name, string type = "OneSix")
    {
        var path = Path.Combine(_instances, id);
        Directory.CreateDirectory(path);

        File.WriteAllText(Path.Combine(path, "instance.cfg"), $"name={name}\nInstanceType={type}\n");
    }

    private MainWindowViewModel Load(IInstanceLauncher? launcher = null)
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        var viewModel = new MainWindowViewModel(launcher);
        viewModel.Instances.Load(list);

        return viewModel;
    }

    private sealed class FakeLauncher(Func<IProgressSink, CancellationToken, Task>? body = null) : IInstanceLauncher
    {
        public List<string> Launched { get; } = [];

        public Task LaunchAsync(string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
        {
            Launched.Add(instanceId);

            return body?.Invoke(progress, cancellationToken) ?? Task.CompletedTask;
        }
    }

    // ================================================================== what the buttons may do

    [Fact]
    public void NothingIsActionableWithoutASelection()
    {
        MakeInstance("a", "Alpha");

        var viewModel = Load();

        Assert.False(viewModel.HasSelection);
        Assert.False(viewModel.CanLaunch);
        Assert.False(viewModel.CanEdit);
        Assert.Equal("Extreme Launcher", viewModel.Title);
    }

    [Fact]
    public void SelectingAnInstanceEnablesTheButtonsAndNamesTheWindow()
    {
        MakeInstance("a", "Alpha");

        var viewModel = Load();
        viewModel.Select("a");

        Assert.True(viewModel.CanLaunch);
        Assert.True(viewModel.CanEdit);
        Assert.Equal("Extreme Launcher - Alpha", viewModel.Title);
    }

    /*
     * AN UNSUPPORTED INSTANCE IS EDITABLE BUT NOT LAUNCHABLE. Deleting or renaming one does not
     * require understanding it, and refusing would leave a user unable to clear up an instance the
     * launcher has already told them is broken.
     */
    [Fact]
    public void AnUnsupportedInstanceCanBeEditedButNotLaunched()
    {
        MakeInstance("broken", "Broken", type: "Nonsense");

        var viewModel = Load();
        viewModel.Select("broken");

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.CanEdit);
        Assert.False(viewModel.CanLaunch);
    }

    /// <summary>Nothing may be started or edited while a launch is under way.</summary>
    [Fact]
    public async Task EverythingIsDisabledWhileALaunchRuns()
    {
        MakeInstance("a", "Alpha");

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var viewModel = Load(new FakeLauncher(async (_, _) =>
        {
            started.SetResult();

            await release.Task.ConfigureAwait(false);
        }));

        viewModel.Select("a");

        var launch = viewModel.LaunchSelectedAsync();
        await started.Task.ConfigureAwait(true);

        Assert.False(viewModel.CanLaunch);
        Assert.False(viewModel.CanEdit);

        release.SetResult();
        await launch.ConfigureAwait(true);

        Assert.True(viewModel.CanLaunch);
    }

    // ================================================================== killing

    [Fact]
    public void KillIsOffWhenNothingIsRunning()
    {
        MakeInstance("a", "Alpha");

        var viewModel = Load();
        viewModel.Select("a");

        // A selected but idle instance can be launched, not killed.
        Assert.True(viewModel.CanLaunch);
        Assert.False(viewModel.CanKill);
    }

    [Fact]
    public async Task KillStopsTheRunningInstance()
    {
        MakeInstance("a", "Alpha");

        var started = new TaskCompletionSource();

        // The body ends only when its token is cancelled -- which is what Kill does.
        var viewModel = Load(new FakeLauncher(async (_, ct) =>
        {
            started.SetResult();

            await Task.Delay(-1, ct).ConfigureAwait(false);
        }));

        viewModel.Select("a");

        var launch = viewModel.LaunchSelectedAsync();
        await started.Task.ConfigureAwait(true);

        Assert.True(viewModel.CanKill);
        Assert.False(viewModel.CanLaunch);

        viewModel.KillSelectedCommand.Execute(null);

        // The launch unwinds because its token was cancelled; then the row is idle again.
        await launch.ConfigureAwait(true);

        Assert.False(viewModel.CanKill);
        Assert.True(viewModel.CanLaunch);
    }

    // ================================================================== the command guards itself

    /*
     * The command does not trust the button. A command reachable by keyboard, by a double-click and by
     * a menu item will eventually be invoked in a state the button was not in.
     */
    [Fact]
    public async Task LaunchingWithNoSelectionDoesNothing()
    {
        MakeInstance("a", "Alpha");

        var launcher = new FakeLauncher();
        var viewModel = Load(launcher);

        await viewModel.LaunchSelectedAsync().ConfigureAwait(true);

        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task LaunchingAnUnsupportedInstanceDoesNothing()
    {
        MakeInstance("broken", "Broken", type: "Nonsense");

        var launcher = new FakeLauncher();
        var viewModel = Load(launcher);

        viewModel.Select("broken");
        await viewModel.LaunchSelectedAsync().ConfigureAwait(true);

        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task LaunchingStartsTheSelectedInstance()
    {
        MakeInstance("a", "Alpha");

        var launcher = new FakeLauncher();
        var viewModel = Load(launcher);

        viewModel.Select("a");
        await viewModel.LaunchSelectedAsync().ConfigureAwait(true);

        Assert.Equal(["a"], launcher.Launched);
    }

    // ================================================================== what the window shows

    /*
     * Work with no known total gets a sweeping bar rather than one stuck at zero, which reads as
     * "nothing is happening" -- and resolving a version genuinely has no total until it is done.
     */
    [Fact]
    public async Task ProgressWithNoTotalIsIndeterminate()
    {
        MakeInstance("a", "Alpha");

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var viewModel = Load(new FakeLauncher(async (progress, _) =>
        {
            progress.SetStatus("Resolving version");
            started.SetResult();

            await release.Task.ConfigureAwait(false);

            progress.SetProgress(1, 2);
        }));

        viewModel.Select("a");

        var launch = viewModel.LaunchSelectedAsync();
        await started.Task.ConfigureAwait(true);

        Assert.True(viewModel.HasIndeterminateProgress);

        release.SetResult();
        await launch.ConfigureAwait(true);

        // Not indeterminate once it is finished, whatever the last progress said.
        Assert.False(viewModel.HasIndeterminateProgress);
    }

    /// <summary>A failure is shown after the run, not during it — during, the status bar is enough.</summary>
    [Fact]
    public async Task AFailureIsSurfacedOnceTheLaunchEnds()
    {
        MakeInstance("a", "Alpha");

        var viewModel = Load(new FakeLauncher((_, _) => throw new InvalidOperationException("no Java found")));

        viewModel.Select("a");

        Assert.False(viewModel.HasFailure);

        await viewModel.LaunchSelectedAsync().ConfigureAwait(true);

        Assert.True(viewModel.HasFailure);
        Assert.Equal("no Java found", viewModel.Launch.Failure);
    }

    /*
     * A Play button that silently does nothing is the worst of the three possible behaviours -- worse
     * than an error, and far worse than a disabled button -- because the user cannot tell it from a
     * hang. A build with no launcher wired up says so.
     */
    [Fact]
    public async Task ABuildWithNoLauncherSaysSoRatherThanDoingNothing()
    {
        MakeInstance("a", "Alpha");

        var viewModel = Load();

        viewModel.Select("a");
        await viewModel.LaunchSelectedAsync().ConfigureAwait(true);

        Assert.True(viewModel.HasFailure);
        Assert.Contains("not wired up", viewModel.Launch.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingNothingClearsTheTitleAndTheButtons()
    {
        MakeInstance("a", "Alpha");

        var viewModel = Load();

        viewModel.Select("a");
        viewModel.Select(null);

        Assert.False(viewModel.HasSelection);
        Assert.Equal("Extreme Launcher", viewModel.Title);
    }

    // ================================================================== one launch, not two

    /*
     * THE GAME STARTS EXACTLY ONCE. When the main window moved to per-instance coordinators, an
     * intermediate version kept its own coordinator alongside the registry's and awaited BOTH -- which
     * launched the game twice, from two processes writing into the same instance directory. It passed
     * every existing test, because all of them only checked that a launch happened.
     */
    [Fact]
    public async Task LaunchingStartsTheGameExactlyOnce()
    {
        MakeInstance("a", "Alpha");

        var launcher = new FakeLauncher();
        var viewModel = Load(launcher);

        viewModel.Select("a");
        await viewModel.LaunchSelectedAsync().ConfigureAwait(true);

        Assert.Equal(["a"], launcher.Launched);
    }

    /// <summary>The status strip follows the instance that was actually started.</summary>
    [Fact]
    public async Task TheStatusStripFollowsTheInstanceThatWasLaunched()
    {
        MakeInstance("a", "Alpha");

        var viewModel = Load(new FakeLauncher());

        viewModel.Select("a");
        await viewModel.LaunchSelectedAsync().ConfigureAwait(true);

        // The registry's own object, not a copy of it.
        Assert.Same(viewModel.Registry.For("a"), viewModel.Launch);
    }

    /*
     * A RUNNING INSTANCE CANNOT BE STARTED AGAIN, but a different one can -- the distinction the
     * registry exists for.
     */
    [Fact]
    public async Task ADifferentInstanceCanStartWhileOneIsRunning()
    {
        MakeInstance("a", "Alpha");
        MakeInstance("b", "Beta");

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var viewModel = Load(new FakeLauncher(async (_, _) =>
        {
            started.TrySetResult();

            await release.Task.ConfigureAwait(false);
        }));

        viewModel.Select("a");

        var running = viewModel.LaunchSelectedAsync();
        await started.Task.ConfigureAwait(true);

        // The one that is running cannot start again...
        viewModel.Select("a");
        Assert.False(viewModel.CanLaunch);

        // ...but its neighbour can.
        viewModel.Select("b");
        Assert.True(viewModel.CanLaunch);

        release.SetResult();
        await running.ConfigureAwait(true);
    }
}
