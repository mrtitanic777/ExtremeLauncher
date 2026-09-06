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
 * Searching for mods and building up a basket of them.
 *
 * The basket is the part worth testing hardest. Nobody adds exactly one mod, so a selection that does
 * not survive the next search is the difference between a usable dialog and one that makes you install
 * mods one at a time.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ModDownloadViewModelTests
{
    private static IndexedPack Pack(string id, string name, ResourceProvider provider = ResourceProvider.Modrinth)
    {
        var pack = new IndexedPack { AddonId = id, Name = name, Provider = provider };

        pack.Authors.Add(new ModpackAuthor { Name = "somebody" });

        return pack;
    }

    private static IndexedVersion Build(string version, string file)
        => new() { Version = version, FileName = file, DownloadUrl = "https://example.invalid/" + file };

    private sealed class StubSearch : IResourceSearch
    {
        public Dictionary<string, IReadOnlyList<IndexedPack>> Answers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<IndexedVersion>> VersionsFor { get; } = new(StringComparer.Ordinal);

        public HashSet<ResourceProvider> Unavailable { get; } = [];

        public List<string> Queries { get; } = [];

        public Func<string, Task>? BeforeAnswering { get; set; }

        public bool IsAvailable(ResourceProvider provider) => !Unavailable.Contains(provider);

        public string UnavailableReason(ResourceProvider provider)
            => IsAvailable(provider) ? string.Empty : $"{provider} needs an API key.";

        public async Task<IReadOnlyList<IndexedPack>> SearchAsync(
            ResourceProvider provider,
            string query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);

            if (BeforeAnswering is not null)
            {
                await BeforeAnswering(query).ConfigureAwait(false);
            }

            return Answers.TryGetValue(query, out var answer) ? answer : [];
        }

        public Task LoadVersionsAsync(IndexedPack pack, CancellationToken cancellationToken)
        {
            pack.Versions.Clear();

            if (VersionsFor.TryGetValue(pack.AddonId, out var versions))
            {
                pack.Versions.AddRange(versions);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingSearch : IResourceSearch
    {
        public bool IsAvailable(ResourceProvider provider) => true;

        public string UnavailableReason(ResourceProvider provider) => string.Empty;

        public Task<IReadOnlyList<IndexedPack>> SearchAsync(
            ResourceProvider provider,
            string query,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("api.modrinth.com could not be resolved");

        public Task LoadVersionsAsync(IndexedPack pack, CancellationToken cancellationToken)
            => throw new HttpRequestException("api.modrinth.com could not be resolved");
    }

    private static ModDownloadViewModel New(StubSearch search)
        => new(search, "1.20.1", "Fabric");

    [Fact]
    public async Task SearchingShowsResults()
    {
        var search = new StubSearch();

        search.Answers["sodium"] = [Pack("AANobbMI", "Sodium"), Pack("PtjYWJkn", "Sodium Extra")];

        var vm = New(search);

        vm.SearchText = "sodium";

        await vm.SearchAsync();

        Assert.Equal(2, vm.Results.Count);
        Assert.Equal("Sodium", vm.Results[0].Name);
        Assert.Equal("Modrinth", vm.Results[0].ProviderLabel);
        Assert.Equal("somebody", vm.Results[0].Authors);
    }

    [Fact]
    public async Task AnEmptyResultSaysSoRatherThanShowingNothing()
    {
        var vm = New(new StubSearch());

        vm.SearchText = "nothingmatchesthis";

        await vm.SearchAsync();

        Assert.Empty(vm.Results);
        Assert.Contains("No mods matched", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedSearchSaysWhy()
    {
        var vm = new ModDownloadViewModel(new FailingSearch(), "1.20.1", "Fabric");

        await vm.SearchAsync();

        Assert.Contains("Search failed", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task AddingAModPutsItInTheBasket()
    {
        var search = new StubSearch();

        search.Answers["sodium"] = [Pack("AANobbMI", "Sodium")];
        search.VersionsFor["AANobbMI"] = [Build("0.5.13", "sodium-fabric-0.5.13.jar")];

        var vm = New(search);

        vm.SearchText = "sodium";

        await vm.SearchAsync();

        vm.Highlighted = vm.Results[0];

        await vm.ToggleSelectedAsync();

        Assert.Single(vm.Selection);
        Assert.True(vm.CanInstall);
        Assert.Equal("1 mod selected", vm.SelectionSummary);
    }

    [Fact]
    public async Task ASelectedModSurvivesSearchingForSomethingElse()
    {
        /*
         * THE WHOLE REASON THE BASKET EXISTS. Nobody adds exactly one mod, so a selection cleared by
         * the next search would mean installing them one at a time -- which is what a "pick one"
         * dialog would force and what upstream deliberately does not do.
         */
        var search = new StubSearch();

        search.Answers["sodium"] = [Pack("AANobbMI", "Sodium")];
        search.Answers["lithium"] = [Pack("gvQqBUqZ", "Lithium")];
        search.VersionsFor["AANobbMI"] = [Build("0.5.13", "sodium.jar")];
        search.VersionsFor["gvQqBUqZ"] = [Build("0.11.2", "lithium.jar")];

        var vm = New(search);

        vm.SearchText = "sodium";
        await vm.SearchAsync();

        vm.Highlighted = vm.Results[0];
        await vm.ToggleSelectedAsync();

        vm.SearchText = "lithium";
        await vm.SearchAsync();

        vm.Highlighted = vm.Results[0];
        await vm.ToggleSelectedAsync();

        Assert.Equal(2, vm.Selection.Count);

        vm.Accept();

        Assert.Equal(2, vm.Chosen.Count);
        Assert.Contains(vm.Chosen, c => c.Version.FileName == "sodium.jar");
        Assert.Contains(vm.Chosen, c => c.Version.FileName == "lithium.jar");
    }

    [Fact]
    public async Task AModAlreadyInTheBasketComesBackTickedWhenItReappears()
    {
        /*
         * A new search builds NEW row objects for the same mods, so "is this one already added" has to
         * be matched on the addon id. Matching by reference would show it unticked and let the same
         * mod be added twice.
         */
        var search = new StubSearch();

        search.Answers["sodium"] = [Pack("AANobbMI", "Sodium")];
        search.VersionsFor["AANobbMI"] = [Build("0.5.13", "sodium.jar")];

        var vm = New(search);

        vm.SearchText = "sodium";
        await vm.SearchAsync();

        vm.Highlighted = vm.Results[0];
        await vm.ToggleSelectedAsync();

        // The same search again: a different row object for the same mod.
        await vm.SearchAsync();

        Assert.NotSame(vm.Selection[0], vm.Results[0]);
        Assert.True(vm.Results[0].IsSelected);
    }

    [Fact]
    public async Task AddingTheSameModTwiceRemovesItInstead()
    {
        var search = new StubSearch();

        search.Answers["sodium"] = [Pack("AANobbMI", "Sodium")];
        search.VersionsFor["AANobbMI"] = [Build("0.5.13", "sodium.jar")];

        var vm = New(search);

        vm.SearchText = "sodium";
        await vm.SearchAsync();

        vm.Highlighted = vm.Results[0];

        await vm.ToggleSelectedAsync();
        await vm.ToggleSelectedAsync();

        Assert.Empty(vm.Selection);
        Assert.False(vm.CanInstall);
        Assert.False(vm.Results[0].IsSelected);
    }

    [Fact]
    public async Task AModWithNoSuitableBuildCannotBeAddedAndSaysWhy()
    {
        /*
         * Real and common: the mod exists but has nothing for this instance's Minecraft version or
         * loader. Adding it anyway would put a basket entry with no file in it, and the install would
         * fail later with nothing to point at.
         */
        var search = new StubSearch();

        search.Answers["ancient"] = [Pack("xxxx", "Ancient Mod")];

        var vm = New(search);

        vm.SearchText = "ancient";
        await vm.SearchAsync();

        vm.Highlighted = vm.Results[0];
        await vm.ToggleSelectedAsync();

        Assert.Empty(vm.Selection);
        Assert.Contains("no build", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheNewestBuildIsChosenByDefault()
    {
        var search = new StubSearch();

        search.Answers["sodium"] = [Pack("AANobbMI", "Sodium")];
        search.VersionsFor["AANobbMI"] =
        [
            Build("0.5.13", "newest.jar"),
            Build("0.5.11", "older.jar"),
        ];

        var vm = New(search);

        vm.SearchText = "sodium";
        await vm.SearchAsync();

        await vm.LoadVersionsAsync(vm.Results[0]);

        // The list arrives newest-first from both providers; the default follows it rather than
        // re-sorting, because their version strings are not comparable across providers.
        Assert.Equal("newest.jar", vm.Results[0].SelectedVersion?.FileName);
    }

    [Fact]
    public async Task VersionsAreFetchedOnlyOnce()
    {
        var search = new StubSearch();

        search.Answers["sodium"] = [Pack("AANobbMI", "Sodium")];
        search.VersionsFor["AANobbMI"] = [Build("0.5.13", "sodium.jar")];

        var vm = New(search);

        vm.SearchText = "sodium";
        await vm.SearchAsync();

        var row = vm.Results[0];

        await vm.LoadVersionsAsync(row);
        await vm.LoadVersionsAsync(row);

        Assert.True(row.VersionsLoaded);
        Assert.Single(row.Versions);
    }

    [Fact]
    public async Task AStaleSearchResultIsDiscarded()
    {
        /*
         * Typing "sodium" a letter at a time makes several requests. The shorter query matches more
         * and can take longer, so its answer can land AFTER the longer one's -- leaving the results
         * for "sod" on screen under the query "sodium".
         *
         * The slow search is released only after the fast one has finished, which is the ordering
         * that actually causes the bug.
         */
        var search = new StubSearch();

        search.Answers["sod"] = [Pack("1", "Stale result")];
        search.Answers["sodium"] = [Pack("2", "Fresh result")];

        var release = new TaskCompletionSource();

        search.BeforeAnswering = async query =>
        {
            if (query == "sod")
            {
                await release.Task.ConfigureAwait(false);
            }
        };

        var vm = New(search);

        vm.SearchText = "sod";

        var slow = vm.SearchAsync();

        vm.SearchText = "sodium";

        await vm.SearchAsync();

        release.SetResult();

        await slow;

        Assert.Single(vm.Results);
        Assert.Equal("Fresh result", vm.Results[0].Name);
    }

    [Fact]
    public void AnUnavailableProviderSaysSoAsSoonAsItIsPicked()
    {
        // Rather than after a 403, which reads as "the search is broken" instead of "no key".
        var search = new StubSearch();

        search.Unavailable.Add(ResourceProvider.Flame);

        var vm = New(search);

        vm.Provider = ResourceProvider.Flame;

        Assert.Contains("API key", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchingProviderClearsResultsThatCameFromTheOtherOne()
    {
        var search = new StubSearch();

        search.Answers["sodium"] = [Pack("AANobbMI", "Sodium")];

        var vm = New(search);

        vm.SearchText = "sodium";
        await vm.SearchAsync();

        Assert.Single(vm.Results);

        vm.Provider = ResourceProvider.Flame;

        // They are no longer an answer to anything on screen.
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task SearchingAnUnavailableProviderIsRefusedWithoutAsking()
    {
        var search = new StubSearch();

        search.Unavailable.Add(ResourceProvider.Flame);

        var vm = New(search);

        vm.Provider = ResourceProvider.Flame;
        vm.SearchText = "sodium";

        await vm.SearchAsync();

        Assert.Empty(search.Queries);
    }

    [Fact]
    public void TheFilterIsStatedSoItIsObviousWhatIsBeingShown()
    {
        // A search that silently hides everything for other versions looks like a thin catalogue.
        var vm = new ModDownloadViewModel(new StubSearch(), "1.20.1", "Fabric");

        Assert.Equal("Showing Fabric mods for Minecraft 1.20.1", vm.FilterSummary);
    }
}
