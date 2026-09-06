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
 * THE TEST THAT MATTERS IS THE ROUND TRIP: create an instance, then read it back with the SAME code
 * that reads a Prism or MultiMC one. Anything less tests the writer against itself, which is the trap
 * the settings page fell into -- an instance.cfg missing InstanceType writes and re-reads perfectly
 * through this class while listing as "unsupported" in the launcher.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class VanillaCreationTaskTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-new-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public VanillaCreationTaskTests()
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

    private static RuntimeContext Context() => new()
    {
        System = "windows",
        JavaArchitecture = "64",
        JavaRealArchitecture = "x86_64",
    };

    private InstanceList NewList()
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        return list;
    }

    /// <summary>Creates an instance the way the launcher would: staged, then committed.</summary>
    private async Task<(bool Succeeded, string Id, InstanceList List)> CreateAsync(
        string name,
        string version = "1.20.1",
        string loaderUid = "",
        string loaderVersion = "",
        string group = "")
    {
        var list = NewList();

        var creation = new VanillaCreationTask(name, version, Context(), loaderUid, loaderVersion, group);
        var staging = new InstanceStagingTask(list, creation, creation);

        var succeeded = await staging.RunAsync().ConfigureAwait(true);

        list.LoadList();

        return (succeeded, staging.CommittedId, list);
    }

    // ================================================================== the round trip

    /*
     * READ BACK THROUGH InstanceList, which is the same code that reads an instance Prism made. That is
     * the only assertion that proves the file is right rather than merely self-consistent.
     */
    [Fact]
    public async Task ACreatedInstanceIsOneTheLauncherCanRead()
    {
        var (succeeded, id, list) = await CreateAsync("My New Pack").ConfigureAwait(true);

        Assert.True(succeeded);

        var instance = list.GetInstanceById(id);

        Assert.NotNull(instance);
        Assert.Equal("My New Pack", instance!.Name);

        // The whole point of InstanceType: without it this reads as unsupported and will not launch.
        Assert.True(instance.IsSupported);
    }

    [Fact]
    public async Task TheComponentsNameTheChosenVersion()
    {
        var (_, id, list) = await CreateAsync("My New Pack", version: "1.21.4").ConfigureAwait(true);

        var instance = list.GetInstanceById(id)!;

        var profile = new PackProfile(Context());

        Assert.True(profile.Load(instance.Paths.PackProfilePath));
        Assert.Equal("1.21.4", profile.GetComponentVersion("net.minecraft"));
    }

    /*
     * MINECRAFT IS IMPORTANT, which is what makes it unremovable on the version page. Without the flag
     * a user can delete it and be left with an instance that is not one.
     */
    [Fact]
    public async Task MinecraftIsMarkedImportant()
    {
        var (_, id, list) = await CreateAsync("My New Pack").ConfigureAwait(true);

        var profile = new PackProfile(Context());
        profile.Load(list.GetInstanceById(id)!.Paths.PackProfilePath);

        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
    }

    [Fact]
    public async Task ALoaderIsAddedWhenAskedForAndIsNotImportant()
    {
        var (_, id, list) = await CreateAsync(
            "Modded",
            loaderUid: "net.fabricmc.fabric-loader",
            loaderVersion: "0.15.7").ConfigureAwait(true);

        var profile = new PackProfile(Context());
        profile.Load(list.GetInstanceById(id)!.Paths.PackProfilePath);

        Assert.Equal("0.15.7", profile.GetComponentVersion("net.fabricmc.fabric-loader"));

        // Removable, because a loader is exactly the thing somebody may want to take back off.
        Assert.False(profile.GetComponent("net.fabricmc.fabric-loader")!.IsImportant);
    }

    [Fact]
    public async Task AVanillaInstanceHasOnlyMinecraft()
    {
        var (_, id, list) = await CreateAsync("Vanilla").ConfigureAwait(true);

        var profile = new PackProfile(Context());
        profile.Load(list.GetInstanceById(id)!.Paths.PackProfilePath);

        Assert.Equal(["net.minecraft"], profile.Components.Select(c => c.Uid));
    }

    [Fact]
    public async Task TheInstanceLandsInItsGroup()
    {
        var (_, id, list) = await CreateAsync("Grouped", group: "Modded").ConfigureAwait(true);

        Assert.Equal("Modded", list.GetInstanceGroup(id));
    }

    // ================================================================== names and collisions

    /*
     * TWO INSTANCES MAY SHARE A NAME; only the directory has to be unique. DirNameFromString appends
     * "(1)", and the id that comes back is the directory -- which is why CommittedId exists.
     */
    [Fact]
    public async Task CreatingTwoWithTheSameNameKeepsBoth()
    {
        var first = await CreateAsync("Same Name").ConfigureAwait(true);
        var second = await CreateAsync("Same Name").ConfigureAwait(true);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(first.Id, second.Id);

        var list = NewList();

        Assert.Equal(2, list.Count);
        Assert.All(list.Instances, i => Assert.Equal("Same Name", i.Name));
    }

    /// <summary>A name with characters a filesystem will not take still produces a usable directory.</summary>
    [Fact]
    public async Task AnAwkwardNameStillMakesAnInstance()
    {
        var (succeeded, id, list) = await CreateAsync("My: Pack? <v2>").ConfigureAwait(true);

        Assert.True(succeeded);
        Assert.True(Directory.Exists(Path.Combine(_instances, id)));

        // The DISPLAY name is untouched; only the directory was sanitised.
        Assert.Equal("My: Pack? <v2>", list.GetInstanceById(id)!.Name);
    }

    // ================================================================== refusing

    [Theory]
    [InlineData("", "1.20.1")]
    [InlineData("Named", "")]
    public async Task AnIncompleteRequestFailsRatherThanMakingHalfAnInstance(string name, string version)
    {
        var list = NewList();

        var creation = new VanillaCreationTask(name, version, Context());
        var staging = new InstanceStagingTask(list, creation, creation);

        Assert.False(await staging.RunAsync().ConfigureAwait(true));

        // Nothing was left behind in the instances folder.
        list.LoadList();

        Assert.Equal(0, list.Count);
    }

    /*
     * NOT RESOLVED AT CREATION, deliberately -- upstream does not either. Creation writes what was
     * asked for and the first launch resolves it, which is what lets an instance be made with no
     * network. A version that does not exist therefore succeeds here and fails at launch, so the
     * caller is expected to offer a list rather than a text box.
     */
    [Fact]
    public async Task AVersionThatDoesNotExistIsStillWritten()
    {
        var (succeeded, id, list) = await CreateAsync("Typo", version: "1.99.9").ConfigureAwait(true);

        Assert.True(succeeded);

        var profile = new PackProfile(Context());
        profile.Load(list.GetInstanceById(id)!.Paths.PackProfilePath);

        Assert.Equal("1.99.9", profile.GetComponentVersion("net.minecraft"));
    }
}
