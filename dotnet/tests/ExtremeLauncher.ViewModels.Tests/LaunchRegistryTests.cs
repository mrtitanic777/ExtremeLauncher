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
 * "ONE AT A TIME" IS PER INSTANCE, NOT PER LAUNCHER. LaunchCoordinator refuses a second launch while
 * one is running, and its reason is that "both would write to the same instance directory". That is
 * about ONE instance -- with a single coordinator on the main window it applied to the whole launcher,
 * refusing an unrelated pack for a conflict that could not happen.
 *
 * The tests below are that distinction, from both sides: the same instance twice is still refused, two
 * different ones are not.
 */

using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class LaunchRegistryTests
{
    /// <summary>A launcher whose games run until the test lets them stop.</summary>
    private sealed class HeldLauncher : IInstanceLauncher
    {
        private readonly Dictionary<string, TaskCompletionSource> _release = new(StringComparer.Ordinal);

        public List<string> Launched { get; } = [];

        public Task Started(string id) => Gate(id).Task;

        public void Release()
        {
            foreach (var gate in _release.Values.ToList())
            {
                gate.TrySetResult();
            }
        }

        private TaskCompletionSource Gate(string id)
        {
            lock (_release)
            {
                if (!_release.TryGetValue(id, out var gate))
                {
                    _release[id] = gate = new TaskCompletionSource();
                }

                return gate;
            }
        }

        public async Task LaunchAsync(string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
        {
            lock (Launched)
            {
                Launched.Add(instanceId);
            }

            await Gate("release").Task.ConfigureAwait(false);
        }
    }

    // ================================================================== one coordinator per instance

    [Fact]
    public void TheSameInstanceAlwaysGetsTheSameCoordinator()
    {
        var registry = new LaunchRegistry();

        Assert.Same(registry.For("MyPack"), registry.For("MyPack"));
        Assert.NotSame(registry.For("MyPack"), registry.For("OtherPack"));
    }

    /*
     * Asking whether something is running must NOT create a coordinator: a loop over the whole instance
     * list would otherwise fill the dictionary with empties for packs nobody has ever launched.
     */
    [Fact]
    public void AskingAboutAnInstanceDoesNotCreateAnything()
    {
        var registry = new LaunchRegistry();

        Assert.False(registry.IsRunning("NeverLaunched"));
        Assert.Empty(registry.Running);
        Assert.False(registry.AnythingRunning);
    }

    // ================================================================== the distinction that matters

    /*
     * TWO DIFFERENT INSTANCES RUN AT ONCE, which the single-coordinator arrangement refused. Upstream
     * has no such limit -- BaseInstance::isRunning() is per instance, and running two packs together is
     * an ordinary thing to do.
     */
    [Fact]
    public async Task TwoDifferentInstancesCanRunTogether()
    {
        var launcher = new HeldLauncher();
        var registry = new LaunchRegistry(launcher);

        var first = registry.For("Alpha").LaunchAsync("Alpha");
        var second = registry.For("Beta").LaunchAsync("Beta");

        Assert.True(registry.IsRunning("Alpha"));
        Assert.True(registry.IsRunning("Beta"));
        Assert.Equal(2, registry.Running.Count);

        launcher.Release();

        await first.ConfigureAwait(true);
        await second.ConfigureAwait(true);

        Assert.Equal(["Alpha", "Beta"], launcher.Launched.Order(StringComparer.Ordinal));
        Assert.False(registry.AnythingRunning);
    }

    /*
     * ...AND THE SAME ONE TWICE IS STILL REFUSED, which is the half the original reasoning was right
     * about: the second launch would extract natives into the folder the first is reading.
     */
    [Fact]
    public async Task TheSameInstanceTwiceIsStillRefused()
    {
        var launcher = new HeldLauncher();
        var registry = new LaunchRegistry(launcher);

        var coordinator = registry.For("Alpha");

        var first = coordinator.LaunchAsync("Alpha");

        await coordinator.LaunchAsync("Alpha").ConfigureAwait(true);

        launcher.Release();
        await first.ConfigureAwait(true);

        Assert.Equal(["Alpha"], launcher.Launched);
    }

    // ================================================================== forgetting

    [Fact]
    public void AnIdleInstanceCanBeForgotten()
    {
        var registry = new LaunchRegistry();

        var coordinator = registry.For("Alpha");

        Assert.True(registry.Forget("Alpha"));
        Assert.NotSame(coordinator, registry.For("Alpha"));
    }

    /*
     * A RUNNING INSTANCE IS NOT FORGOTTEN. Dropping the coordinator would leave the game running with
     * nothing holding its log or its cancellation -- no way to see it, and no way to stop it.
     */
    [Fact]
    public async Task ARunningInstanceIsNotForgotten()
    {
        var launcher = new HeldLauncher();
        var registry = new LaunchRegistry(launcher);

        var running = registry.For("Alpha").LaunchAsync("Alpha");

        Assert.False(registry.Forget("Alpha"));

        launcher.Release();
        await running.ConfigureAwait(true);

        Assert.True(registry.Forget("Alpha"));
    }
}
