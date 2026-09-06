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
 * The retry loop is the interesting part and it exists for a cause that cannot be reproduced on
 * demand — a virus scanner holding files open. So the delay is injected and the commit is made to fail
 * deliberately, which tests the loop's shape: how many attempts, how long between them, and what
 * survives when it gives up.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstanceStagingTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-stage-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;
    private readonly InstanceList _list;

    public InstanceStagingTests()
    {
        _instances = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(_instances);

        _list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));
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

    private sealed class FakeInstanceTask : IInstanceTask
    {
        public string StagingPath { get; set; } = string.Empty;

        public string Name { get; init; } = "My Pack";

        public string Group { get; init; } = string.Empty;

        public bool ShouldOverride { get; init; }

        public string OriginalInstanceId { get; init; } = string.Empty;
    }

    /// <summary>A task that writes one file into its staging path, or fails.</summary>
    private sealed class BuildTask(IInstanceTask description, bool succeed = true) : LauncherTask("build")
    {
        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            if (!succeed)
            {
                throw new TaskFailedException("the build failed");
            }

            File.WriteAllText(Path.Combine(description.StagingPath, "instance.cfg"), "name=Built\n");

            return Task.CompletedTask;
        }
    }

    // ================================================================== the staging directory

    /*
     * INSIDE the instances folder, not the system temp directory. The commit is a MOVE, and a move
     * across volumes is a copy -- staging next to the destination keeps a ten-gigabyte pack a rename
     * rather than a second full write of everything just downloaded.
     */
    [Fact]
    public void StagingHappensBesideTheDestination()
    {
        var staging = _list.CreateStagingPath();

        Assert.StartsWith(Path.GetFullPath(_instances), Path.GetFullPath(staging), StringComparison.Ordinal);
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void EachStagingPathIsItsOwn()
    {
        var first = _list.CreateStagingPath();
        var second = _list.CreateStagingPath();

        Assert.NotEqual(first, second);
    }

    // ================================================================== committing

    [Fact]
    public async Task ASuccessfulBuildIsMovedIntoPlace()
    {
        var description = new FakeInstanceTask { Name = "My Pack" };
        var task = new InstanceStagingTask(_list, new BuildTask(description), description);

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        Assert.True(File.Exists(Path.Combine(_instances, "My Pack", "instance.cfg")));

        // Nothing half-built is left behind.
        Assert.False(Directory.Exists(task.StagingPath));
        Assert.Equal(1, task.CommitAttempts);
    }

    [Fact]
    public async Task ANewInstanceJoinsItsGroup()
    {
        var description = new FakeInstanceTask { Name = "My Pack", Group = "Modded" };
        var task = new InstanceStagingTask(_list, new BuildTask(description), description);

        Assert.True(await task.RunAsync().ConfigureAwait(true));
        Assert.Equal("Modded", _list.GetInstanceGroup("My Pack"));
    }

    /*
     * A half-extracted pack left in the instances folder would be picked up as an instance on the next
     * scan, so a failed build takes its staging directory with it.
     */
    [Fact]
    public async Task AFailedBuildLeavesNothingBehind()
    {
        var description = new FakeInstanceTask();
        var task = new InstanceStagingTask(_list, new BuildTask(description, succeed: false), description);

        Assert.False(await task.RunAsync().ConfigureAwait(true));

        Assert.False(Directory.Exists(task.StagingPath));

        // Nothing but the hidden staging root, which is expected to survive.
        Assert.DoesNotContain(
            Directory.GetDirectories(_instances, "*", SearchOption.TopDirectoryOnly),
            d => !Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public async Task TheChildsFailureReasonSurvives()
    {
        var description = new FakeInstanceTask();
        var task = new InstanceStagingTask(_list, new BuildTask(description, succeed: false), description);

        await task.RunAsync().ConfigureAwait(true);

        Assert.Contains("the build failed", task.FailReason, StringComparison.Ordinal);
    }

    /// <summary>An update keeps the instance's id, and so its group and everything else keyed by it.</summary>
    [Fact]
    public async Task OverridingReplacesInPlaceAndKeepsTheId()
    {
        Directory.CreateDirectory(Path.Combine(_instances, "Existing"));
        File.WriteAllText(Path.Combine(_instances, "Existing", "instance.cfg"), "name=Old\n");
        File.WriteAllText(Path.Combine(_instances, "Existing", "keep-me.txt"), "still here");

        _list.SetInstanceGroup("Existing", "Modded");

        var description = new FakeInstanceTask
        {
            Name = "Renamed",
            ShouldOverride = true,
            OriginalInstanceId = "Existing",
        };

        var task = new InstanceStagingTask(_list, new BuildTask(description), description);

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        // Same directory, updated contents, untouched group -- and no "Renamed" directory created.
        Assert.Equal("name=Built\n", File.ReadAllText(Path.Combine(_instances, "Existing", "instance.cfg")));
        Assert.Equal("Modded", _list.GetInstanceGroup("Existing"));
        Assert.False(Directory.Exists(Path.Combine(_instances, "Renamed")));
    }

    /*
     * A commit that cannot succeed, deterministically and on every platform.
     *
     * A NEW instance cannot be made to fail this way: DirNameFromString de-duplicates against what is
     * already there, so occupying the destination merely picks another name. An OVERRIDE has a fixed
     * destination, and pointing it at a path that is a FILE makes the folder merge fail every time --
     * standing in for the antivirus lock, which cannot be produced on demand.
     */
    /// <remarks>
    /// A METHOD, not a property. As a property returning a fresh object it handed the build task a
    /// different description than the staging task got, so the build wrote its file relative to an
    /// empty staging path -- and one of these tests passed for entirely the wrong reason.
    /// </remarks>
    private FakeInstanceTask CreateBlocked()
    {
        var destination = Path.Combine(_instances, "Blocked");

        if (!File.Exists(destination))
        {
            File.WriteAllText(destination, "not a directory");
        }

        return new FakeInstanceTask
        {
            Name = "Blocked",
            ShouldOverride = true,
            OriginalInstanceId = "Blocked",
        };
    }

    // ================================================================== the retry loop

    /*
     * UPSTREAM NAMES THE CAUSE EXACTLY: "the whole reason why this uses an exponential backoff retry
     * scheme is antivirus on Windows... causes that horrible failure that is NTFS to lock files in
     * place because they are open." There is nothing to fix and nothing to ask the user -- waiting is
     * the whole remedy.
     */
    [Fact]
    public async Task ABlockedCommitIsRetriedWithGrowingDelays()
    {
        var delays = new List<uint>();
        var blocked = CreateBlocked();

        var task = new InstanceStagingTask(
            _list,
            new BuildTask(blocked),
            blocked,
            delay: (halfSeconds, _) =>
            {
                delays.Add(halfSeconds);

                return Task.CompletedTask;
            });

        Assert.False(await task.RunAsync().ConfigureAwait(true));

        // 1, 2, 4, 8 half-seconds, then the ceiling is reached and it gives up.
        Assert.Equal([1u, 2u, 4u, 8u], delays);
        Assert.Equal(5, task.CommitAttempts);
        Assert.Contains("blocked by something", task.FailReason, StringComparison.Ordinal);
    }

    /*
     * The staging path is NOT destroyed when the commit gives up, unlike when the build fails. The
     * instance is fully built and only the move was blocked -- throwing it away would discard a
     * completed download because a scanner was slow.
     */
    [Fact]
    public async Task AnUncommittableInstanceIsKeptRatherThanDiscarded()
    {
        var blocked = CreateBlocked();

        var task = new InstanceStagingTask(
            _list, new BuildTask(blocked), blocked, delay: (_, _) => Task.CompletedTask);

        await task.RunAsync().ConfigureAwait(true);

        Assert.True(Directory.Exists(task.StagingPath));
        Assert.True(File.Exists(Path.Combine(task.StagingPath, "instance.cfg")));
    }

    // ================================================================== the series itself

    /// <summary>Returns the value BEFORE growing, so the first wait is the minimum.</summary>
    [Fact]
    public void TheSeriesYieldsItsMinimumFirst()
    {
        var series = new ExponentialSeries(1, 16);

        Assert.Equal([1u, 2u, 4u, 8u, 16u, 16u, 16u], Enumerable.Range(0, 7).Select(_ => series.Next()));
    }

    [Fact]
    public void TheSeriesCanBeReset()
    {
        var series = new ExponentialSeries(1, 16);

        series.Next();
        series.Next();
        series.Reset();

        Assert.Equal(1u, series.Next());
    }

    [Fact]
    public void TheSeriesRespectsItsExponent()
    {
        var series = new ExponentialSeries(1, 100, exponent: 10);

        Assert.Equal([1u, 10u, 100u, 100u], Enumerable.Range(0, 4).Select(_ => series.Next()));
    }
}
