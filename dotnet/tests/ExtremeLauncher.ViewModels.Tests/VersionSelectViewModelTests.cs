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
 * Choosing a version, and changing an instance to use it.
 *
 * The filtering is the part worth testing hardest: a loader list that is not filtered by Minecraft
 * version offers hundreds of builds that cannot work with the instance in front of you, and picking
 * one produces a failure at launch naming a version the user never chose.
 */

using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class VersionSelectViewModelTests
{
    private static MetaVersion Version(
        string uid,
        string version,
        string type = "release",
        bool recommended = false,
        string requiresMinecraft = "")
    {
        var meta = new MetaVersion(uid, version) { Type = type, IsRecommended = recommended };

        if (requiresMinecraft.Length != 0)
        {
            meta.SetRequires([new Require("net.minecraft", requiresMinecraft)], []);
        }

        return meta;
    }

    private sealed class StubSource(params MetaVersion[] versions) : IVersionListSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<MetaVersion>> LoadAsync(string uid, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult<IReadOnlyList<MetaVersion>>(versions);
        }
    }

    private sealed class FailingSource : IVersionListSource
    {
        public Task<IReadOnlyList<MetaVersion>> LoadAsync(string uid, CancellationToken cancellationToken)
            => throw new HttpRequestException("meta.example.invalid could not be resolved");
    }

    [Fact]
    public async Task ReleasesOnlyByDefault()
    {
        /*
         * The full Minecraft list is over a thousand entries and all but about a hundred are
         * snapshots. Showing everything by default buries the version almost everybody wants.
         */
        var source = new StubSource(
            Version("net.minecraft", "1.20.1"),
            Version("net.minecraft", "23w31a", type: "snapshot"),
            Version("net.minecraft", "1.20.2"));

        var vm = new VersionSelectViewModel("net.minecraft", "Change version", source);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(2, vm.Versions.Count);
        Assert.DoesNotContain(vm.Versions, v => v.Version == "23w31a");
    }

    [Fact]
    public async Task ALoaderWithNoTypeAtAllIsNotHiddenByTheDefaultFilter()
    {
        /*
         * FOUND BY PROBING THE REAL METADATA SERVER, not by any test I wrote first. Forge and NeoForge
         * publish an EMPTY type on every build -- 5,028 and 1,729 of them respectively -- while
         * Minecraft uses release/snapshot/old_alpha and Fabric uses release throughout.
         *
         * The filter used to keep only type == "release", which hid every Forge version: "Add loader
         * -> Forge" opened on an empty list, and the only way to see anything was to tick "show
         * snapshots and old versions" for a component that has no snapshots.
         *
         * Every fixture in this file said type: "release", so every test passed.
         */
        var source = new StubSource(
            Version("net.minecraftforge", "47.1.0", type: string.Empty, requiresMinecraft: "1.20.1"),
            Version("net.minecraftforge", "47.2.0", type: string.Empty, requiresMinecraft: "1.20.1"));

        var vm = new VersionSelectViewModel(
            "net.minecraftforge",
            "Choose a loader version",
            source,
            minecraftVersion: "1.20.1");

        await vm.LoadAsync(CancellationToken.None);

        Assert.False(vm.ShowAllVersions);
        Assert.Equal(2, vm.Versions.Count);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("old_snapshot")]
    [InlineData("old_alpha")]
    [InlineData("old_beta")]
    [InlineData("experiment")]
    public async Task EveryPrereleaseTypeTheServerUsesIsHiddenByDefault(string type)
    {
        // The list is the real one: these are the types net.minecraft actually publishes.
        var source = new StubSource(
            Version("net.minecraft", "1.20.1"),
            Version("net.minecraft", "weird", type: type));

        var vm = new VersionSelectViewModel("net.minecraft", "Change version", source);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Single(vm.Versions);
        Assert.Equal("1.20.1", vm.Versions[0].Version);
    }

    [Fact]
    public async Task ShowAllVersionsBringsBackTheSnapshots()
    {
        var source = new StubSource(
            Version("net.minecraft", "1.20.1"),
            Version("net.minecraft", "23w31a", type: "snapshot"));

        var vm = new VersionSelectViewModel("net.minecraft", "Change version", source);

        await vm.LoadAsync(CancellationToken.None);

        vm.ShowAllVersions = true;

        Assert.Equal(2, vm.Versions.Count);

        // Re-filtered from the list already fetched -- not a second request to the server.
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task LoaderBuildsAreFilteredToTheInstancesMinecraftVersion()
    {
        /*
         * Forge publishes a build per Minecraft version and says so in `requires`. Offering the
         * 1.19.2 builds to a 1.20.1 instance is offering something that cannot work.
         */
        var source = new StubSource(
            Version("net.minecraftforge", "47.1.0", requiresMinecraft: "1.20.1"),
            Version("net.minecraftforge", "43.2.0", requiresMinecraft: "1.19.2"),
            Version("net.minecraftforge", "47.2.0", requiresMinecraft: "1.20.1"));

        var vm = new VersionSelectViewModel(
            "net.minecraftforge",
            "Choose a loader version",
            source,
            minecraftVersion: "1.20.1");

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(2, vm.Versions.Count);
        Assert.DoesNotContain(vm.Versions, v => v.Version == "43.2.0");
    }

    [Fact]
    public async Task ALoaderThatPinsNothingIsNeverFilteredOut()
    {
        /*
         * Fabric and Quilt are version-independent and say so by declaring no Minecraft requirement.
         * Filtering them out would leave the list empty and the loader uninstallable -- and because
         * Requirement is a struct, "not found" is a default with an empty version, which is exactly
         * the shape a careless check treats as a mismatch.
         */
        var source = new StubSource(
            Version("net.fabricmc.fabric-loader", "0.15.0"),
            Version("net.fabricmc.fabric-loader", "0.16.0"));

        var vm = new VersionSelectViewModel(
            "net.fabricmc.fabric-loader",
            "Choose a loader version",
            source,
            minecraftVersion: "1.20.1");

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(2, vm.Versions.Count);
    }

    [Fact]
    public async Task TheRecommendedVersionIsSelectedToBeginWith()
    {
        var source = new StubSource(
            Version("net.minecraft", "1.20.1"),
            Version("net.minecraft", "1.20.4", recommended: true));

        var vm = new VersionSelectViewModel("net.minecraft", "Change version", source);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("1.20.4", vm.Selected?.Version);
        Assert.True(vm.CanAccept);
    }

    [Fact]
    public async Task SearchingNarrowsTheList()
    {
        var source = new StubSource(
            Version("net.minecraft", "1.20.1"),
            Version("net.minecraft", "1.19.4"),
            Version("net.minecraft", "1.20.4"));

        var vm = new VersionSelectViewModel("net.minecraft", "Change version", source);

        await vm.LoadAsync(CancellationToken.None);

        vm.SearchText = "1.20";

        Assert.Equal(2, vm.Versions.Count);
        Assert.All(vm.Versions, v => Assert.StartsWith("1.20", v.Version, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSelectionSurvivesAFilterChangeWhereItStillExists()
    {
        // Ticking "show all" must not lose the row somebody had already highlighted.
        var source = new StubSource(
            Version("net.minecraft", "1.20.1"),
            Version("net.minecraft", "1.20.4"),
            Version("net.minecraft", "23w31a", type: "snapshot"));

        var vm = new VersionSelectViewModel("net.minecraft", "Change version", source);

        await vm.LoadAsync(CancellationToken.None);

        vm.Selected = vm.Versions.Single(v => v.Version == "1.20.1");

        vm.ShowAllVersions = true;

        Assert.Equal("1.20.1", vm.Selected?.Version);
    }

    [Fact]
    public async Task AFailedLoadSaysWhyRatherThanShowingAnEmptyList()
    {
        /*
         * An empty list with no explanation reads as "this component has no versions", which is a
         * different and far more alarming thing than "the metadata server is unreachable".
         */
        var vm = new VersionSelectViewModel("net.minecraft", "Change version", new FailingSource());

        await vm.LoadAsync(CancellationToken.None);

        Assert.Empty(vm.Versions);
        Assert.Contains("could not", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsLoading);
        Assert.False(vm.CanAccept);
    }

    [Fact]
    public async Task LoadingTwiceSharesTheOneRequest()
    {
        /*
         * The dialog awaits this on open and a filter change can ask again while it is still running.
         * Two concurrent rebuilds of the same collection is a crash this port has already had once,
         * and it only appeared against a real server.
         */
        var source = new StubSource(Version("net.minecraft", "1.20.1"));

        var vm = new VersionSelectViewModel("net.minecraft", "Change version", source);

        var first = vm.LoadAsync(CancellationToken.None);
        var second = vm.LoadAsync(CancellationToken.None);

        Assert.Same(first, second);

        await first;

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task AcceptingRecordsTheChosenVersion()
    {
        var source = new StubSource(Version("net.minecraft", "1.20.1"));

        var vm = new VersionSelectViewModel("net.minecraft", "Change version", source);

        await vm.LoadAsync(CancellationToken.None);

        vm.Accept();

        Assert.Equal("1.20.1", vm.ChosenVersion);
    }

    [Fact]
    public async Task WithNoSourceItSaysSoInsteadOfHangingOnASpinner()
    {
        var vm = new VersionSelectViewModel("net.minecraft", "Change version");

        await vm.LoadAsync(CancellationToken.None);

        Assert.False(vm.IsLoading);
        Assert.NotEqual(string.Empty, vm.Status);
    }
}
