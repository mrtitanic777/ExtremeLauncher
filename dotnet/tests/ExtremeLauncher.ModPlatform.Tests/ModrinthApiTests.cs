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
 * These assert whole URLs rather than fragments, deliberately.
 *
 * A malformed Modrinth facet does not fail -- it returns an empty result page, which is
 * indistinguishable from "nothing matches your search". There is no error to catch and no exception to
 * assert on, so the request itself is the only thing that can be checked, and checking it in pieces
 * would miss exactly the punctuation errors that break it.
 */

using ExtremeLauncher.Core;
using Xunit;

using Version = ExtremeLauncher.Core.Version;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class ModrinthApiTests
{
    private readonly ModrinthApi _api = new();

    private static SearchArgs Search(
        ResourceType type = ResourceType.Mod,
        int offset = 0,
        string? search = null,
        SortingMethod? sorting = null,
        ModLoaderTypes? loaders = null,
        IReadOnlyList<Version>? versions = null,
        string? side = null,
        IReadOnlyList<string>? categoryIds = null)
        => new()
        {
            Type = type,
            Offset = offset,
            Search = search,
            Sorting = sorting,
            Loaders = loaders,
            Versions = versions,
            Side = side,
            CategoryIds = categoryIds,
        };

    // ================================================================== searching

    /// <summary>The project type is always present, so a mod search never returns modpacks.</summary>
    [Fact]
    public void TheSimplestSearchStillConstrainsTheProjectType()
        => Assert.Equal(
            "https://api.modrinth.com/v2/search?offset=0&limit=25&facets=[[\"project_type:mod\"]]",
            _api.GetSearchUrl(Search()));

    [Fact]
    public void AFullSearchPutsEveryFilterInItsOwnOrGroup()
    {
        var url = _api.GetSearchUrl(Search(
            type: ResourceType.Mod,
            offset: 50,
            search: "jei",
            sorting: new SortingMethod(2, "downloads", "Sort by Downloads"),
            loaders: ModLoaderTypes.Fabric | ModLoaderTypes.Quilt,
            versions: [new Version("1.20.1")],
            side: "client",
            categoryIds: ["utility", "storage"]));

        Assert.Equal(
            "https://api.modrinth.com/v2/search?offset=50&limit=25&query=jei&index=downloads&facets="
            + "[[\"categories:fabric\",\"categories:quilt\"],"
            + "[\"versions:1.20.1\"],"
            + "[\"client_side:required\",\"client_side:optional\"],"
            + "[\"categories:utility\",\"categories:storage\"],"
            + "[\"project_type:mod\"]]",
            url);
    }

    /*
     * An empty OR group matches nothing, so an empty filter has to be dropped rather than sent. Each
     * of these would otherwise turn a perfectly good search into a blank page.
     */
    [Theory]
    [MemberData(nameof(EmptyFilters))]
    public void AnEmptyFilterIsDroppedRatherThanSentAsAnEmptyGroup(SearchArgs args)
        => Assert.Equal(
            "https://api.modrinth.com/v2/search?offset=0&limit=25&facets=[[\"project_type:mod\"]]",
            _api.GetSearchUrl(args));

    public static TheoryData<SearchArgs> EmptyFilters() =>
    [
        Search(loaders: ModLoaderTypes.None),
        Search(versions: []),
        Search(categoryIds: []),

        // "both" is not a filter: a mod usable on either side is usable on the one you asked about.
        Search(side: "both"),
        Search(side: ""),
        Search(side: "nonsense"),
    ];

    /// <summary>Optional counts as supported; only "unsupported" is filtered away.</summary>
    [Theory]
    [InlineData("client", "\"client_side:required\",\"client_side:optional\"")]
    [InlineData("server", "\"server_side:required\",\"server_side:optional\"")]
    public void SideFiltersAcceptRequiredAndOptional(string side, string expected)
        => Assert.Equal(expected, ModrinthApi.GetSideFilters(side));

    /*
     * Searching without a filter the user asked for is worse than not searching: a Cauldron query
     * answered with Fabric results looks like a successful search.
     */
    [Fact]
    public void ASearchForOnlyUnsupportedLoadersIsRefusedRatherThanBroadened()
        => Assert.Null(_api.GetSearchUrl(Search(loaders: ModLoaderTypes.Cauldron)));

    [Fact]
    public void ASearchIsAllowedIfAnyRequestedLoaderIsSupported()
    {
        var url = _api.GetSearchUrl(Search(loaders: ModLoaderTypes.Cauldron | ModLoaderTypes.Forge));

        Assert.NotNull(url);

        // Cauldron simply does not appear -- Modrinth has no category for it.
        Assert.Contains("[\"categories:forge\"]", url, StringComparison.Ordinal);
        Assert.DoesNotContain("cauldron", url, StringComparison.Ordinal);
    }

    /// <summary>The declared order, not the order the flags were combined in.</summary>
    [Fact]
    public void LoadersAlwaysComeOutInTheProvidersOrder()
    {
        var expected = new[] { "neoforge", "forge", "fabric", "quilt", "liteloader" };

        var all = ModLoaderTypes.Quilt | ModLoaderTypes.Forge | ModLoaderTypes.LiteLoader
            | ModLoaderTypes.NeoForge | ModLoaderTypes.Fabric;

        Assert.Equal(expected, ModrinthApi.GetModLoaderStrings(all));
    }

    [Fact]
    public void CauldronIsNeverSentBecauseModrinthHasNoCategoryForIt()
        => Assert.Empty(ModrinthApi.GetModLoaderStrings(ModLoaderTypes.Cauldron));

    [Theory]
    [InlineData(ResourceType.Mod, "mod")]
    [InlineData(ResourceType.ResourcePack, "resourcepack")]
    [InlineData(ResourceType.ShaderPack, "shader")]
    [InlineData(ResourceType.Modpack, "modpack")]
    public void ResourceTypesUseModrinthsSpelling(ResourceType type, string expected)
        => Assert.Equal(expected, ModrinthApi.ResourceTypeParameter(type));

    // ================================================================== projects and versions

    [Fact]
    public void ProjectUrlsAreBuiltFromTheId()
    {
        Assert.Equal("https://api.modrinth.com/v2/project/P7dR8mSH", ModrinthApi.GetInfoUrl("P7dR8mSH"));

        Assert.Equal(
            "https://api.modrinth.com/v2/projects?ids=[\"P7dR8mSH\",\"Ha28R6CL\"]",
            ModrinthApi.GetMultipleModInfoUrl(["P7dR8mSH", "Ha28R6CL"]));
    }

    [Fact]
    public void AVersionListWithNoFiltersHasNoQueryStringAtAll()
        => Assert.Equal(
            "https://api.modrinth.com/v2/project/P7dR8mSH/version",
            ModrinthApi.GetVersionsUrl(new VersionSearchArgs { Pack = new IndexedPack { AddonId = "P7dR8mSH" } }));

    [Fact]
    public void AVersionListCarriesBothFiltersWhenGivenBoth()
        => Assert.Equal(
            "https://api.modrinth.com/v2/project/P7dR8mSH/version"
            + "?game_versions=[\"1.20.1\",\"1.20\"]&loaders=[\"fabric\"]",
            ModrinthApi.GetVersionsUrl(new VersionSearchArgs
            {
                Pack = new IndexedPack { AddonId = "P7dR8mSH" },
                McVersions = [new Version("1.20.1"), new Version("1.20")],
                Loaders = ModLoaderTypes.Fabric,
            }));

    /*
     * CARRIED-OVER QUIRK, pinned so a change to it is a decision rather than an accident.
     *
     * The same empty loader set is dropped from a search but sent here as a filter matching nothing.
     * Upstream tests only whether the option is present, where the search also tests it is non-empty.
     * Which behaviour was intended is not recoverable from the code, so it is left as found.
     */
    [Fact]
    public void AnEmptyLoaderSetIsSentAsAnEmptyFilterHereButDroppedFromASearch()
    {
        Assert.Equal(
            "https://api.modrinth.com/v2/project/P7dR8mSH/version?loaders=[\"\"]",
            ModrinthApi.GetVersionsUrl(new VersionSearchArgs
            {
                Pack = new IndexedPack { AddonId = "P7dR8mSH" },
                Loaders = ModLoaderTypes.None,
            }));

        Assert.DoesNotContain(
            "categories",
            _api.GetSearchUrl(Search(loaders: ModLoaderTypes.None))!,
            StringComparison.Ordinal);
    }

    // ================================================================== dependencies

    /// <summary>A dependency naming an exact version needs no search.</summary>
    [Fact]
    public void ADependencyPinnedToAVersionIsFetchedDirectly()
        => Assert.Equal(
            "https://api.modrinth.com/v2/version/abcdef",
            ModrinthApi.GetDependencyUrl(new DependencySearchArgs
            {
                Dependency = new Dependency { AddonId = "P7dR8mSH", Version = "abcdef" },
                McVersion = new Version("1.20.1"),
                Loader = ModLoaderTypes.Fabric,
            }));

    [Fact]
    public void ADependencyNamingOnlyAProjectIsNarrowedToTheVersionAndLoaderInPlay()
        => Assert.Equal(
            "https://api.modrinth.com/v2/project/P7dR8mSH/version"
            + "?game_versions=[\"1.20.1\"]&loaders=[\"fabric\"]",
            ModrinthApi.GetDependencyUrl(new DependencySearchArgs
            {
                Dependency = new Dependency { AddonId = "P7dR8mSH" },
                McVersion = new Version("1.20.1"),
                Loader = ModLoaderTypes.Fabric,
            }));

    // ================================================================== the rest

    [Fact]
    public void SortingMethodsMatchModrinthsApiSpec()
        => Assert.Equal(
            ["relevance", "downloads", "follows", "newest", "updated"],
            _api.SortingMethods.Select(m => m.Name));

    [Fact]
    public void AuthorUrlsPointAtTheUserPage()
        => Assert.Equal("https://modrinth.com/user/flowln", ModrinthApi.GetAuthorUrl("flowln"));

    /// <summary>A URL-only instance says so rather than dereferencing a null client.</summary>
    [Fact]
    public async Task FetchingWithoutAnHttpClientIsRefusedClearly()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _api.GetProjectAsync("P7dR8mSH")).ConfigureAwait(true);

        Assert.Contains("URL construction", error.Message, StringComparison.Ordinal);
    }
}
