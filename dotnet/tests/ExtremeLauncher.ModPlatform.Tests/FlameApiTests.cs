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
 * Whole URLs and whole bodies, for the same reason the Modrinth tests assert whole URLs: a request
 * CurseForge does not understand comes back as an empty page or a 400 with no detail, so the request
 * is the only artifact that can be checked before it goes out.
 *
 * The numbers are the API's, from CurseForge's own documentation. Renumbering a class id or a loader
 * id silently searches for the wrong thing, so they are pinned individually rather than trusted to a
 * round-trip.
 */

using Xunit;

using Version = ExtremeLauncher.Core.Version;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class FlameApiTests
{
    private readonly FlameApi _api = new();

    private static SearchArgs Search(
        ResourceType type = ResourceType.Mod,
        int offset = 0,
        string? search = null,
        SortingMethod? sorting = null,
        ModLoaderTypes? loaders = null,
        IReadOnlyList<Version>? versions = null,
        IReadOnlyList<string>? categoryIds = null)
        => new()
        {
            Type = type,
            Offset = offset,
            Search = search,
            Sorting = sorting,
            Loaders = loaders,
            Versions = versions,
            CategoryIds = categoryIds,
        };

    // ================================================================== the API's own numbering

    [Theory]
    [InlineData(ResourceType.Mod, 6)]
    [InlineData(ResourceType.ResourcePack, 12)]
    [InlineData(ResourceType.ShaderPack, 6552)]
    [InlineData(ResourceType.Modpack, 4471)]
    public void ClassIdsAreCurseforgesNumbering(ResourceType type, int expected)
        => Assert.Equal(expected, FlameApi.GetClassId(type));

    /// <summary>Upstream shares <c>default:</c> with MOD, so an unknown type searches mods.</summary>
    [Fact]
    public void AnUnrecognisedResourceTypeFallsBackToMods()
        => Assert.Equal(6, FlameApi.GetClassId((ResourceType)999));

    [Theory]
    [InlineData(ModLoaderTypes.Forge, 1)]
    [InlineData(ModLoaderTypes.Cauldron, 2)]
    [InlineData(ModLoaderTypes.LiteLoader, 3)]
    [InlineData(ModLoaderTypes.Fabric, 4)]
    [InlineData(ModLoaderTypes.Quilt, 5)]
    [InlineData(ModLoaderTypes.NeoForge, 6)]
    public void LoaderIdsMatchCurseforgesModLoaderType(ModLoaderTypes loader, int expected)
        => Assert.Equal(expected, FlameApi.GetMappedModLoader(loader));

    /// <summary>Zero reads as "any" to CurseForge, which is what a combination should mean.</summary>
    [Fact]
    public void ACombinationOfLoadersMapsToZero()
    {
        Assert.Equal(0, FlameApi.GetMappedModLoader(ModLoaderTypes.Forge | ModLoaderTypes.Fabric));
        Assert.Equal(0, FlameApi.GetMappedModLoader(ModLoaderTypes.None));
    }

    /*
     * Cauldron and LiteLoader HAVE ids but are never offered as search filters -- both are long dead
     * and CurseForge returns nothing for them. The mapping still needs them, because a file already
     * on disk can report one.
     */
    [Fact]
    public void DeadLoadersAreMappableButNotSearchable()
    {
        Assert.NotEqual(0, FlameApi.GetMappedModLoader(ModLoaderTypes.Cauldron));
        Assert.NotEqual(0, FlameApi.GetMappedModLoader(ModLoaderTypes.LiteLoader));

        Assert.Empty(FlameApi.GetModLoaderStrings(ModLoaderTypes.Cauldron | ModLoaderTypes.LiteLoader));
        Assert.False(FlameApi.ValidateModLoaders(ModLoaderTypes.Cauldron | ModLoaderTypes.LiteLoader));
    }

    /// <summary>The declared order, not the order the flags were combined in.</summary>
    [Fact]
    public void LoadersComeOutAsIdsInTheProvidersOrder()
        => Assert.Equal(
            ["6", "1", "4", "5"],
            FlameApi.GetModLoaderStrings(
                ModLoaderTypes.Quilt | ModLoaderTypes.Forge | ModLoaderTypes.NeoForge | ModLoaderTypes.Fabric));

    // ================================================================== searching

    [Fact]
    public void TheSimplestSearchIsScopedToMinecraftAndMods()
        => Assert.Equal(
            "https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&index=0&pageSize=25&sortOrder=desc",
            _api.GetSearchUrl(Search()));

    [Fact]
    public void AFullSearchCarriesEveryFilterAsAQueryParameter()
        => Assert.Equal(
            "https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&index=50&pageSize=25"
            + "&searchFilter=jei&sortField=6&sortOrder=desc&modLoaderTypes=[4,5]"
            + "&categoryIds=[423,435]&gameVersion=1.20.1",
            _api.GetSearchUrl(Search(
                offset: 50,
                search: "jei",
                sorting: new SortingMethod(6, "TotalDownloads", "Sort by Downloads"),
                loaders: ModLoaderTypes.Fabric | ModLoaderTypes.Quilt,
                versions: [new Version("1.20.1")],
                categoryIds: ["423", "435"])));

    /// <summary>Sorting goes out as the NUMBER, where Modrinth sends the name.</summary>
    [Fact]
    public void SortingIsSentAsItsIndexNotItsName()
    {
        var url = _api.GetSearchUrl(Search(sorting: new SortingMethod(3, "LastUpdated", "Sort by Last Updated")));

        Assert.Contains("sortField=3", url!, StringComparison.Ordinal);
        Assert.DoesNotContain("LastUpdated", url, StringComparison.Ordinal);
    }

    /*
     * CurseForge's search takes ONE game version. Upstream sends the first and drops the rest
     * silently; there is nowhere better to put them, but the narrowing is real and worth pinning.
     */
    [Fact]
    public void OnlyTheFirstGameVersionSurvivesASearch()
    {
        var url = _api.GetSearchUrl(Search(versions: [new Version("1.20.1"), new Version("1.19.4")]));

        Assert.Contains("gameVersion=1.20.1", url!, StringComparison.Ordinal);
        Assert.DoesNotContain("1.19.4", url, StringComparison.Ordinal);
    }

    /*
     * CARRIED-OVER DIVERGENCE between the two providers, pinned so it stays a decision.
     *
     * Modrinth REFUSES a search whose loaders it cannot filter on. CurseForge declares the same
     * validator and never calls it, so the same search goes out with an empty array. Preserved: both
     * behaviours are upstream's, and a caller may depend on either.
     */
    [Fact]
    public void ASearchForOnlyDeadLoadersSendsAnEmptyArrayRatherThanBeingRefused()
    {
        var url = _api.GetSearchUrl(Search(loaders: ModLoaderTypes.Cauldron));

        Assert.NotNull(url);
        Assert.Contains("modLoaderTypes=[]", url, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyLoaderSetIsDroppedFromASearch()
        => Assert.DoesNotContain(
            "modLoaderTypes",
            _api.GetSearchUrl(Search(loaders: ModLoaderTypes.None))!,
            StringComparison.Ordinal);

    // ================================================================== projects, files, versions

    [Fact]
    public void ProjectAndFileUrlsAreBuiltFromIds()
    {
        Assert.Equal("https://api.curseforge.com/v1/mods/306612", FlameApi.GetInfoUrl("306612"));
        Assert.Equal("https://api.curseforge.com/v1/mods/306612/files/3814740", FlameApi.GetFileUrl("306612", "3814740"));
    }

    /// <summary>The huge page size is "do not paginate" spelled as a number.</summary>
    [Fact]
    public void ListingFilesAsksForAllOfThem()
        => Assert.Equal(
            "https://api.curseforge.com/v1/mods/306612/files?pageSize=10000",
            FlameApi.GetVersionsUrl(new VersionSearchArgs { Pack = new IndexedPack { AddonId = "306612" } }));

    [Fact]
    public void ListingFilesNarrowsByVersionAndASingleLoader()
        => Assert.Equal(
            "https://api.curseforge.com/v1/mods/306612/files?pageSize=10000&gameVersion=1.20.1&modLoaderType=4",
            FlameApi.GetVersionsUrl(new VersionSearchArgs
            {
                Pack = new IndexedPack { AddonId = "306612" },
                McVersions = [new Version("1.20.1")],
                Loaders = ModLoaderTypes.Fabric,
            }));

    /*
     * The file filter is SINGULAR, so two loaders cannot be expressed. Upstream omits the parameter
     * rather than choosing one, which is right: guessing would hide files the caller asked to see.
     */
    [Fact]
    public void TwoLoadersMeanNoLoaderFilterAtAllRatherThanAGuess()
    {
        var url = FlameApi.GetVersionsUrl(new VersionSearchArgs
        {
            Pack = new IndexedPack { AddonId = "306612" },
            Loaders = ModLoaderTypes.Fabric | ModLoaderTypes.Quilt,
        });

        Assert.DoesNotContain("modLoaderType", url, StringComparison.Ordinal);
    }

    /// <summary>CurseForge dependency entries name a project, never a file — so there is no shortcut.</summary>
    [Fact]
    public void ADependencyIsAlwaysResolvedByListingFiles()
        => Assert.Equal(
            "https://api.curseforge.com/v1/mods/306612/files?pageSize=10000&gameVersion=1.20.1&modLoaderType=1",
            FlameApi.GetDependencyUrl(new DependencySearchArgs
            {
                Dependency = new Dependency { AddonId = "306612", Version = "3814740" },
                McVersion = new Version("1.20.1"),
                Loader = ModLoaderTypes.Forge,
            }));

    [Fact]
    public void CategoriesAreScopedToTheResourceType()
        => Assert.Equal(
            "https://api.curseforge.com/v1/categories?gameId=432&classId=4471",
            FlameApi.GetCategoriesUrl(ResourceType.Modpack));

    // ================================================================== request bodies

    /*
     * IDS AND FINGERPRINTS GO OUT AS JSON STRINGS, which is worth a test because the opposite is the
     * natural assumption -- CurseForge's own docs type these fields as integers. Upstream builds
     * strings and CurseForge accepts them.
     */
    [Fact]
    public void FingerprintsAreSentAsStringsDespiteBeingNumbers()
    {
        var body = FlameApi.CreateFingerprintsBody([1540447798u, 3376380438u]);

        Assert.Equal("""{"fingerprints":["1540447798","3376380438"]}""", body.ToJsonString());
    }

    [Fact]
    public void ProjectAndFileBodiesUseTheApisFieldNames()
    {
        Assert.Equal("""{"modIds":["306612","308769"]}""", FlameApi.CreateProjectsBody(["306612", "308769"]).ToJsonString());
        Assert.Equal("""{"fileIds":["3814740"]}""", FlameApi.CreateFilesBody(["3814740"]).ToJsonString());
    }

    [Fact]
    public void AnEmptyBodyIsStillAWellFormedRequest()
        => Assert.Equal("""{"modIds":[]}""", FlameApi.CreateProjectsBody([]).ToJsonString());

    // ================================================================== the rest

    [Fact]
    public void SortingMethodsMatchCurseforgesSortFieldNumbering()
    {
        Assert.Equal(
            [1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u],
            _api.SortingMethods.Select(m => m.Index));

        Assert.Equal("TotalDownloads", _api.SortingMethods.Single(m => m.Index == 6).Name);
    }

    [Fact]
    public async Task FetchingWithoutAnHttpClientIsRefusedClearly()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _api.GetProjectAsync("306612")).ConfigureAwait(true);

        Assert.Contains("URL construction", error.Message, StringComparison.Ordinal);
    }
}
