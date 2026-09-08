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
 * Browsing for a modpack.
 *
 * THE VERSION LIST IS THE HARD PART, and the live data is why. Fabulously Optimized publishes 463
 * versions and its four newest are all betas for a Minecraft snapshot, so an unfiltered list opens on
 * exactly the releases almost nobody wants. Most of these tests are about that list behaving.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class PackBrowserViewModelTests
{
    private sealed class StubSearch : IResourceSearch
    {
        public List<IndexedPack> Results { get; } = [];

        public Dictionary<string, List<IndexedVersion>> Versions { get; } = [];

        public Exception? SearchThrows { get; set; }

        public int VersionLoads { get; private set; }

        /// <summary>The provider the last search was made against.</summary>
        public ResourceProvider? LastProvider { get; private set; }

        /// <summary>Providers to report as unavailable (e.g. CurseForge with no API key).</summary>
        public HashSet<ResourceProvider> Unavailable { get; } = [];

        public bool IsAvailable(ResourceProvider provider) => !Unavailable.Contains(provider);

        public string UnavailableReason(ResourceProvider provider)
            => Unavailable.Contains(provider) ? $"{provider} needs an API key." : string.Empty;

        public Task<IReadOnlyList<IndexedPack>> SearchAsync(
            ResourceProvider provider,
            string query,
            CancellationToken cancellationToken)
        {
            LastProvider = provider;

            return SearchThrows is not null
                ? Task.FromException<IReadOnlyList<IndexedPack>>(SearchThrows)
                : Task.FromResult<IReadOnlyList<IndexedPack>>(Results);
        }

        public Task LoadVersionsAsync(IndexedPack pack, CancellationToken cancellationToken)
        {
            VersionLoads++;

            pack.Versions.Clear();

            if (Versions.TryGetValue(pack.AddonId, out var versions))
            {
                pack.Versions.AddRange(versions);
            }

            return Task.CompletedTask;
        }
    }

    private static IndexedPack Pack(string id, string name)
    {
        var pack = new IndexedPack { Provider = ResourceProvider.Modrinth, AddonId = id, Name = name };

        return pack;
    }

    private static IndexedVersion Version(string fileId, string name, VersionType type, string mc = "1.20.1")
    {
        var version = new IndexedVersion
        {
            FileId = fileId,
            Version = name,
            VersionType = type,
            DownloadUrl = $"https://cdn.example.invalid/{fileId}.mrpack",
            FileName = $"{name}.mrpack",
        };

        version.McVersion.Add(mc);

        return version;
    }

    private static async Task<(PackBrowserViewModel Vm, StubSearch Search)> BrowsingAsync(
        params (string Id, string Name, IndexedVersion[] Versions)[] packs)
    {
        var search = new StubSearch();

        foreach (var (id, name, versions) in packs)
        {
            search.Results.Add(Pack(id, name));
            search.Versions[id] = [.. versions];
        }

        var vm = new PackBrowserViewModel(search);

        await vm.SearchAsync();

        return (vm, search);
    }

    [Fact]
    public async Task SearchingListsPacks()
    {
        var (vm, _) = await BrowsingAsync(("abc", "Fabulously Optimized", []));

        Assert.Equal(["Fabulously Optimized"], vm.Results.Select(r => r.Name));
    }

    [Fact]
    public async Task NothingFoundSaysSoRatherThanLookingBroken()
    {
        var (vm, _) = await BrowsingAsync();

        Assert.Contains("No modpacks matched", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickingAPackLoadsItsVersionsOnce()
    {
        var (vm, search) = await BrowsingAsync(
            ("abc", "A Pack", [Version("v1", "1.0.0", VersionType.Release)]));

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);
        await vm.LoadVersionsAsync(vm.Results[0]);

        // Twice through the property change and twice explicitly; the fetch still happens once.
        Assert.Equal(1, search.VersionLoads);
        Assert.Single(vm.Selection);
    }

    [Fact]
    public async Task BetasAreHiddenByDefault()
    {
        /*
         * WHAT THE LIVE DATA LOOKS LIKE. Fabulously Optimized's newest four are betas for a snapshot,
         * so an unfiltered list opens on versions almost nobody wants and the stable release somebody
         * is actually looking for is well down the page.
         */
        var (vm, _) = await BrowsingAsync((
            "abc",
            "A Pack",
            [
                Version("v3", "2.0.0-beta.1", VersionType.Beta, "26.2"),
                Version("v2", "1.1.0", VersionType.Release),
                Version("v1", "1.0.0", VersionType.Release),
            ]));

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);

        Assert.True(vm.ReleasesOnly);
        Assert.Equal(["1.1.0", "1.0.0"], vm.Selection.Select(v => v.Version.Version));
    }

    [Fact]
    public async Task TurningTheFilterOffShowsEverythingAgain()
    {
        var (vm, _) = await BrowsingAsync((
            "abc",
            "A Pack",
            [Version("v2", "2.0.0-beta.1", VersionType.Beta), Version("v1", "1.0.0", VersionType.Release)]));

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);

        Assert.Single(vm.Selection);

        vm.ReleasesOnly = false;

        Assert.Equal(2, vm.Selection.Count);
    }

    [Fact]
    public async Task APackWithNoStableReleaseStillShowsItsVersions()
    {
        /*
         * A pack still in development. An empty list under a ticked box reads as "this pack has no
         * versions", which is a different and much more alarming statement than "they are all betas".
         */
        var (vm, _) = await BrowsingAsync((
            "abc",
            "Work In Progress",
            [Version("v2", "0.2.0-alpha", VersionType.Alpha), Version("v1", "0.1.0-beta", VersionType.Beta)]));

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);

        Assert.True(vm.ReleasesOnly);
        Assert.Equal(2, vm.Selection.Count);
    }

    [Fact]
    public async Task TheNewestVersionIsPickedForYou()
    {
        // Modrinth returns newest first, and the newest stable is what somebody installing a pack for
        // the first time almost always wants.
        var (vm, _) = await BrowsingAsync((
            "abc",
            "A Pack",
            [Version("v2", "1.1.0", VersionType.Release), Version("v1", "1.0.0", VersionType.Release)]));

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);

        Assert.Equal("1.1.0", vm.SelectedVersion?.Version.Version);
    }

    [Fact]
    public async Task AVersionSurvivingTheFilterStaysPicked()
    {
        // Otherwise ticking the box to peek at the betas silently moves the choice somebody made.
        var (vm, _) = await BrowsingAsync((
            "abc",
            "A Pack",
            [
                Version("v3", "2.0.0-beta", VersionType.Beta),
                Version("v2", "1.1.0", VersionType.Release),
                Version("v1", "1.0.0", VersionType.Release),
            ]));

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);

        vm.SelectedVersion = vm.Selection.Single(v => v.Version.Version == "1.0.0");

        vm.ReleasesOnly = false;

        Assert.Equal("1.0.0", vm.SelectedVersion?.Version.Version);
    }

    [Fact]
    public async Task TheRowSaysWhichMinecraftItIsFor()
    {
        /*
         * THE ONLY QUESTION MOST PEOPLE ARE ASKING. "14.0.0-beta.6" tells nobody which Minecraft it
         * runs on, and the pack's own numbering has no relationship to it.
         */
        var (vm, _) = await BrowsingAsync((
            "abc",
            "A Pack",
            [Version("v1", "14.0.0-beta.6", VersionType.Beta, "26.2")]));

        vm.SelectedPack = vm.Results[0];
        vm.ReleasesOnly = false;

        await vm.LoadVersionsAsync(vm.Results[0]);

        var label = vm.Selection[0].Label;

        Assert.Contains("14.0.0-beta.6", label, StringComparison.Ordinal);
        Assert.Contains("Minecraft 26.2", label, StringComparison.Ordinal);
        Assert.Contains("beta", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallIsOfferedOnlyOnceThereIsSomethingToInstall()
    {
        var (vm, _) = await BrowsingAsync((
            "abc",
            "A Pack",
            [Version("v1", "1.0.0", VersionType.Release)]));

        Assert.False(vm.CanInstall);

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);

        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task AcceptingRecordsBothThePackAndTheVersion()
    {
        // Both, because the install task needs the project id AND the file id -- that pair is the
        // whole reason for browsing rather than importing a file.
        var (vm, _) = await BrowsingAsync((
            "abc",
            "A Pack",
            [Version("v1", "1.0.0", VersionType.Release)]));

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);

        vm.Accept();

        Assert.Equal("abc", vm.Chosen?.Pack.AddonId);
        Assert.Equal("v1", vm.Chosen?.Version.FileId);
    }

    [Fact]
    public async Task AcceptingWithNothingChosenChoosesNothing()
    {
        var (vm, _) = await BrowsingAsync(("abc", "A Pack", []));

        vm.Accept();

        Assert.Null(vm.Chosen);
    }

    [Fact]
    public async Task ThePacksNameIsTheDefaultInstanceName()
    {
        var (vm, _) = await BrowsingAsync((
            "abc",
            "Fabulously Optimized",
            [Version("v1", "1.0.0", VersionType.Release)]));

        vm.SelectedPack = vm.Results[0];

        Assert.Equal("Fabulously Optimized", vm.EffectiveInstanceName);

        // And a typed name wins, which is how somebody installs a second copy of a pack they have.
        vm.InstanceName = "  FO test  ";

        Assert.Equal("FO test", vm.EffectiveInstanceName);
    }

    [Fact]
    public async Task AFailedSearchSaysWhy()
    {
        var search = new StubSearch { SearchThrows = new HttpRequestException("the server hung up") };

        var vm = new PackBrowserViewModel(search);

        await vm.SearchAsync();

        Assert.Contains("the server hung up", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public void WithNoSearchServiceTheWindowDoesNotOfferToSearch()
    {
        Assert.False(new PackBrowserViewModel().CanSearch);
    }

    [Fact]
    public async Task ANewSearchClearsTheOldChoice()
    {
        /*
         * Otherwise Install stays enabled pointing at a pack that is no longer on screen -- and the
         * thing it would install is one the user can no longer see.
         */
        var (vm, _) = await BrowsingAsync((
            "abc",
            "A Pack",
            [Version("v1", "1.0.0", VersionType.Release)]));

        vm.SelectedPack = vm.Results[0];

        await vm.LoadVersionsAsync(vm.Results[0]);

        Assert.True(vm.CanInstall);

        await vm.SearchAsync();

        Assert.False(vm.CanInstall);
        Assert.Empty(vm.Selection);
    }

    // ================================================================== provider selection

    [Fact]
    public void BothProvidersAreOffered()
    {
        var vm = new PackBrowserViewModel(new StubSearch());

        Assert.Equal([ResourceProvider.Modrinth, ResourceProvider.Flame], vm.AvailableProviders);
        Assert.Equal(ResourceProvider.Modrinth, vm.Provider);
    }

    [Fact]
    public async Task SearchUsesTheSelectedProvider()
    {
        var search = new StubSearch();
        var vm = new PackBrowserViewModel(search) { Provider = ResourceProvider.Flame };

        await vm.SearchAsync();

        Assert.Equal(ResourceProvider.Flame, search.LastProvider);
    }

    [Fact]
    public async Task SwitchingProviderClearsTheOldResults()
    {
        var (vm, _) = await BrowsingAsync(("abc", "Fabulously Optimized", []));
        Assert.NotEmpty(vm.Results);

        vm.Provider = ResourceProvider.Flame;

        Assert.Empty(vm.Results);
        Assert.Null(vm.SelectedPack);
        Assert.Null(vm.SelectedVersion);
    }

    [Fact]
    public void AnUnavailableProviderCannotBeSearchedAndSaysWhy()
    {
        var search = new StubSearch();
        search.Unavailable.Add(ResourceProvider.Flame);

        var vm = new PackBrowserViewModel(search);
        Assert.True(vm.CanSearch);

        vm.Provider = ResourceProvider.Flame;

        Assert.False(vm.CanSearch);
        Assert.Equal("Flame needs an API key.", vm.Status);
    }

    [Fact]
    public void ReturningToAnAvailableProviderClearsTheUnavailableNotice()
    {
        var search = new StubSearch();
        search.Unavailable.Add(ResourceProvider.Flame);

        var vm = new PackBrowserViewModel(search) { Provider = ResourceProvider.Flame };
        Assert.False(vm.CanSearch);

        vm.Provider = ResourceProvider.Modrinth;

        Assert.True(vm.CanSearch);
        Assert.Equal(string.Empty, vm.Status);
    }
}
