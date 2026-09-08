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
 * Every branch of the loader detection exists because some real pack broke the previous rule, so the
 * coordinate shapes that motivated each one are written into the tests as literals. There is nothing
 * to derive them from — the only way to keep the rules honest is to name the inputs they were built
 * for.
 */

using System.Text;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class TechnicSolderTests
{
    private static System.Text.Json.Nodes.JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(json), "test"));

    [Fact]
    public void APackListsItsBuilds()
    {
        var pack = TechnicSolder.LoadPack(Parse("""
            { "recommended": "1.2.0", "latest": "1.3.0-beta", "builds": ["1.0.0", "1.2.0", "1.3.0-beta"] }
            """));

        Assert.Equal("1.2.0", pack.Recommended);
        Assert.Equal("1.3.0-beta", pack.Latest);
        Assert.Equal(["1.0.0", "1.2.0", "1.3.0-beta"], pack.Builds);
    }

    [Fact]
    public void ABuildListsItsMods()
    {
        var build = TechnicSolder.LoadPackBuild(Parse("""
            {
              "minecraft": "1.7.10",
              "mods": [
                { "name": "railcraft", "version": "9.12.2.0", "md5": "abc", "url": "https://solder.invalid/a.zip" }
              ]
            }
            """));

        Assert.Equal("1.7.10", build.Minecraft);

        var mod = Assert.Single(build.Mods);

        Assert.Equal("railcraft", mod.Name);
        Assert.Equal("9.12.2.0", mod.Version);
        Assert.Equal("abc", mod.Md5);
    }

    /// <summary>The one optional field: Solder allows a mod with no version recorded.</summary>
    [Fact]
    public void AModWithNoVersionIsStillRead()
    {
        var build = TechnicSolder.LoadPackBuild(Parse("""
            { "minecraft": "1.7.10", "mods": [ { "name": "m", "md5": "abc", "url": "https://x.invalid/a.zip" } ] }
            """));

        Assert.Equal(string.Empty, Assert.Single(build.Mods).Version);
    }

    /// <summary>An md5 is all Solder publishes — weak, and the only thing on offer.</summary>
    [Fact]
    public void AModWithNoHashIsRefused()
        => Assert.ThrowsAny<LauncherException>(() => TechnicSolder.LoadPackBuild(Parse("""
            { "minecraft": "1.7.10", "mods": [ { "name": "m", "url": "https://x.invalid/a.zip" } ] }
            """)));

    [Fact]
    public void ABuildUrlIsSolderPlusPackAndVersion()
        => Assert.Equal(
            "https://solder.example/api/modpack/tekkit/1.2.9",
            TechnicSolder.BuildUrl("https://solder.example/api", "tekkit", "1.2.9"));

    /// <summary>A base URL recorded with a trailing slash gives the same result as one without.</summary>
    [Fact]
    public void ABuildUrlTrimsTheBaseTrailingSlash()
        => Assert.Equal(
            "https://solder.example/api/modpack/tekkit/1.2.9",
            TechnicSolder.BuildUrl("https://solder.example/api/", "tekkit", "1.2.9"));
}

public sealed class TechnicVersionJsonTests
{
    private static System.Text.Json.Nodes.JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(json), "test"));

    private static System.Text.Json.Nodes.JsonObject WithLibraries(string libraries, string extra = "")
        => Parse($$"""
            { "inheritsFrom": "1.20.1", "libraries": {{libraries}} {{extra}} }
            """);

    // ================================================================== the Minecraft version

    [Fact]
    public void InheritsFromIsTheMinecraftVersion()
        => Assert.Equal(
            new PackComponent(PackComponents.MinecraftUid, "1.20.1", Important: true),
            TechnicVersionJson.DetectComponents(Parse("""{ "inheritsFrom": "1.20.1" }"""))[0]);

    /// <summary>Old FML packs predate the field, so fmlversion.properties is the fallback.</summary>
    [Fact]
    public void TheFmlVersionFillsInForAPackWithNoInheritsFrom()
        => Assert.Equal(
            "1.4.7",
            TechnicVersionJson.DetectComponents(Parse("{}"), fmlMinecraftVersion: "1.4.7")[0].Version);

    /// <summary>With neither, there is no version to fetch and the pack cannot be installed.</summary>
    [Fact]
    public void APackWithNoMinecraftVersionAtAllIsRefused()
        => Assert.ThrowsAny<LauncherException>(() => TechnicVersionJson.DetectComponents(Parse("{}")));

    /// <summary>Everything else degrades: an unrecognised loader still installs as vanilla.</summary>
    [Fact]
    public void APackWithNoRecognisedLoaderStillYieldsMinecraft()
    {
        var components = TechnicVersionJson.DetectComponents(
            WithLibraries("""[{ "name": "com.example:something:1.0" }]"""));

        Assert.Equal(PackComponents.MinecraftUid, Assert.Single(components).Uid);
    }

    // ================================================================== Forge

    /*
     * The coordinate is net.minecraftforge:forge:<mc>-<forge>, so the Forge version is everything
     * after the first hyphen -- EXCEPT on 1.7.10, where the coordinate repeats the Minecraft version
     * on the end and only the middle field is wanted.
     */
    [Theory]
    [InlineData("net.minecraftforge:forge:1.20.1-47.2.0", "47.2.0")]
    [InlineData("net.minecraftforge:fmlloader:1.20.1-47.2.0", "47.2.0")]
    [InlineData("net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10", "10.13.4.1614")]
    [InlineData("net.minecraftforge:fmlloader:1.7.10-10.13.4.1614-1.7.10", "10.13.4.1614")]
    public void ForgeVersionsAreReadOutOfTheCoordinate(string name, string expected)
        => Assert.Equal(
            new PackComponent(PackComponents.ForgeUid, expected),
            TechnicVersionJson.DetectForgeVersion(name));

    /// <summary>Without a hyphen there is no Minecraft-version prefix to strip, and nothing to read.</summary>
    [Fact]
    public void AForgeCoordinateWithNoHyphenIsNotRead()
        => Assert.Null(TechnicVersionJson.DetectForgeVersion("net.minecraftforge:forge:47.2.0"));

    [Fact]
    public void ANonForgeCoordinateIsNotRead()
        => Assert.Null(TechnicVersionJson.DetectForgeVersion("net.fabricmc:fabric-loader:0.15.0"));

    // ================================================================== NeoForge

    /*
     * NEOFORGE DOES NOT PUT ITS VERSION IN A COORDINATE. Upstream reads the value following
     * --fml.neoForgeVersion in the game argument list: a launcher inferring a component version from
     * a command line it is about to build. There is nowhere else it appears.
     */
    [Fact]
    public void NeoForgesVersionComesFromTheGameArguments()
    {
        var root = WithLibraries(
            """[{ "name": "net.neoforged.fancymodloader:loader:4.0.24" }]""",
            """, "arguments": { "game": ["--fml.neoForgeVersion", "20.4.190", "--other", "x"] }""");

        var components = TechnicVersionJson.DetectComponents(root);

        Assert.Equal(new PackComponent(PackComponents.NeoForgeUid, "20.4.190"), components[1]);
    }

    /// <summary>NeoForge kept the older spelling for a while.</summary>
    [Fact]
    public void TheOlderForgeVersionArgumentIsAccepted()
    {
        var root = WithLibraries(
            """[{ "name": "net.neoforged.fancymodloader:loader:1.0" }]""",
            """, "arguments": { "game": ["--fml.forgeVersion", "20.2.88"] }""");

        Assert.Equal("20.2.88", TechnicVersionJson.DetectNeoForgeVersion(root));
    }

    /// <summary>The pack is NeoForge either way; guessing another loader from a later library is worse.</summary>
    [Fact]
    public void ANeoForgePackWithNoVersionArgumentGetsNoLoader()
    {
        var root = WithLibraries("""
            [{ "name": "net.neoforged.fancymodloader:loader:1.0" },
             { "name": "net.fabricmc:fabric-loader:0.15.0" }]
            """);

        Assert.Null(TechnicVersionJson.DetectLoader(root));
    }

    [Fact]
    public void AnArgumentListWithNoVersionMarkerYieldsNothing()
        => Assert.Equal(
            string.Empty,
            TechnicVersionJson.DetectNeoForgeVersion(Parse("""{ "arguments": { "game": ["--x", "y"] } }""")));

    // ================================================================== the prefix map

    [Theory]
    [InlineData("net.fabricmc:fabric-loader:0.15.0", PackComponents.FabricUid, "0.15.0")]
    [InlineData("org.quiltmc:quilt-loader:0.23.0", PackComponents.QuiltUid, "0.23.0")]
    [InlineData("net.minecraftforge:minecraftforge:14.23.5.2860", PackComponents.ForgeUid, "14.23.5.2860")]
    public void LoadersWithAnOrdinaryCoordinateAreReadFromTheMap(string name, string uid, string version)
    {
        var components = TechnicVersionJson.DetectComponents(WithLibraries($$"""[{ "name": "{{name}}" }]"""));

        Assert.Equal(new PackComponent(uid, version), components[1]);
    }

    /*
     * The prefix map does not stop the search: upstream's inner break exits only the map lookup, so
     * the outer loop keeps going and a Forge or NeoForge library later in the list still wins.
     */
    [Fact]
    public void AForgeLibraryAfterAMapMatchStillWins()
    {
        var root = WithLibraries("""
            [{ "name": "net.fabricmc:fabric-loader:0.15.0" },
             { "name": "net.minecraftforge:forge:1.20.1-47.2.0" }]
            """);

        Assert.Equal(new PackComponent(PackComponents.ForgeUid, "47.2.0"), TechnicVersionJson.DetectLoader(root));
    }

    /// <summary>First match wins among the map entries themselves.</summary>
    [Fact]
    public void TheFirstMapMatchIsKept()
    {
        var root = WithLibraries("""
            [{ "name": "net.fabricmc:fabric-loader:0.15.0" },
             { "name": "org.quiltmc:quilt-loader:0.23.0" }]
            """);

        Assert.Equal(new PackComponent(PackComponents.FabricUid, "0.15.0"), TechnicVersionJson.DetectLoader(root));
    }

    /// <summary>A library list holding anything but objects is stepped over, not fatal.</summary>
    [Fact]
    public void NonObjectLibraryEntriesAreSkipped()
    {
        var root = WithLibraries("""["a string", 42, { "name": "net.fabricmc:fabric-loader:0.15.0" }]""");

        Assert.Equal(new PackComponent(PackComponents.FabricUid, "0.15.0"), TechnicVersionJson.DetectLoader(root));
    }

    [Fact]
    public void APackWithNoLibrariesAtAllHasNoLoader()
        => Assert.Null(TechnicVersionJson.DetectLoader(Parse("""{ "inheritsFrom": "1.20.1" }""")));
}

public sealed class TechnicSearchTests
{
    private static System.Text.Json.Nodes.JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(json), "test"));

    private const string Base = "https://api.technicpack.net/";

    [Fact]
    public void AnEmptyTermIsTheTrendingList()
    {
        var (url, mode) = TechnicSearch.SearchUrl(Base, "multimc", string.Empty);

        Assert.Equal("https://api.technicpack.net/trending?build=multimc", url);
        Assert.Equal(TechnicSearchMode.List, mode);
    }

    [Fact]
    public void APlainTermIsASearch()
    {
        var (url, mode) = TechnicSearch.SearchUrl(Base, "multimc", "tekkit");

        Assert.Equal("https://api.technicpack.net/search?build=multimc&q=tekkit", url);
        Assert.Equal(TechnicSearchMode.List, mode);
    }

    [Fact]
    public void AHashTermIsOnePackBySlug()
    {
        var (url, mode) = TechnicSearch.SearchUrl(Base, "multimc", "#tekkit");

        Assert.Equal("https://api.technicpack.net/modpack/tekkit?build=multimc", url);
        Assert.Equal(TechnicSearchMode.Single, mode);
    }

    [Fact]
    public void AnHttpModpackUrlIsUpgradedToHttpsAndSingle()
    {
        var (url, mode) = TechnicSearch.SearchUrl(Base, "multimc", "http://api.technicpack.net/modpack/tekkit");

        Assert.Equal("https://api.technicpack.net/modpack/tekkit?build=multimc", url);
        Assert.Equal(TechnicSearchMode.Single, mode);
    }

    [Fact]
    public void AnHttpsModpackUrlIsSingle()
    {
        var (url, mode) = TechnicSearch.SearchUrl(Base, "multimc", "https://api.technicpack.net/modpack/tekkit");

        Assert.Equal("https://api.technicpack.net/modpack/tekkit?build=multimc", url);
        Assert.Equal(TechnicSearchMode.Single, mode);
    }

    [Fact]
    public void AListResponseIsReadAndVanillaIsSkipped()
    {
        var packs = TechnicSearch.ParseList(Parse("""
            { "modpacks": [
                { "name": "Tekkit", "slug": "tekkit", "iconUrl": "https://x.invalid/icons/tekkit.png" },
                { "name": "Vanilla", "slug": "vanilla", "iconUrl": "https://x.invalid/v.png" },
                { "name": "Hexxit", "slug": "hexxit", "iconUrl": "null" }
            ] }
            """));

        Assert.Equal(["Tekkit", "Hexxit"], packs.Select(p => p.Name));
        Assert.Equal("tekkit.png", packs[0].LogoName);
        Assert.Equal("https://x.invalid/icons/tekkit.png", packs[0].LogoUrl);

        // No icon: the literal "null" upstream uses, for both fields.
        Assert.Equal("null", packs[1].LogoName);
        Assert.Equal("null", packs[1].LogoUrl);
    }

    [Fact]
    public void ASingleResponseIsReadWithItsIcon()
    {
        var pack = TechnicSearch.ParseSingle(Parse("""
            { "displayName": "Tekkit Classic", "name": "tekkit",
              "icon": { "url": "https://x.invalid/icons/tekkit.png" } }
            """));

        Assert.NotNull(pack);
        Assert.Equal("Tekkit Classic", pack!.Name);
        Assert.Equal("tekkit", pack.Slug);
        Assert.Equal("tekkit.png", pack.LogoName);
    }

    /// <summary>An error response (an unknown pack) yields null.</summary>
    [Fact]
    public void ASingleErrorResponseIsNull()
        => Assert.Null(TechnicSearch.ParseSingle(Parse("""{ "error": "No such modpack" }""")));
}

public sealed class TechnicDetailTests
{
    private static System.Text.Json.Nodes.JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(json), "test"));

    [Fact]
    public void AStringUrlIsASingleZipPack()
    {
        var detail = TechnicDetail.Parse(Parse("""
            { "url": "https://x.invalid/pack.zip", "minecraft": "1.7.10", "version": "1.2.3",
              "user": "Author", "platformUrl": "https://technicpack.net/modpack/x",
              "description": "A pack." }
            """));

        Assert.NotNull(detail);
        Assert.False(detail!.IsSolder);
        Assert.Equal("https://x.invalid/pack.zip", detail.Url);
        Assert.Equal("1.7.10", detail.MinecraftVersion);
        Assert.Equal("1.2.3", detail.CurrentVersion);
        Assert.Equal("Author", detail.Author);
        Assert.Equal("https://technicpack.net/modpack/x", detail.WebsiteUrl);
        Assert.Equal("A pack.", detail.Description);
    }

    /// <summary>A null url with a solder url is a Solder pack, its trailing slashes trimmed.</summary>
    [Fact]
    public void ANullUrlWithSolderIsASolderPack()
    {
        var detail = TechnicDetail.Parse(Parse("""
            { "url": null, "solder": "https://solder.invalid/api/", "minecraft": "1.6.4", "version": "2.0" }
            """));

        Assert.NotNull(detail);
        Assert.True(detail!.IsSolder);
        Assert.Equal("https://solder.invalid/api", detail.Url); // trailing slash trimmed
    }

    /// <summary>A single-zip URL is not slash-trimmed — only Solder is.</summary>
    [Fact]
    public void ASingleZipUrlKeepsAnyTrailingSlash()
    {
        var detail = TechnicDetail.Parse(Parse("""{ "url": "https://x.invalid/pack/" }"""));

        Assert.Equal("https://x.invalid/pack/", detail!.Url);
    }

    /// <summary>Upstream requires the url key present at all, even for Solder packs.</summary>
    [Fact]
    public void AMissingUrlKeyIsNotAPack()
        => Assert.Null(TechnicDetail.Parse(Parse("""{ "solder": "https://solder.invalid/api" }""")));

    [Fact]
    public void ANullUrlWithNoSolderIsNotAPack()
        => Assert.Null(TechnicDetail.Parse(Parse("""{ "url": null }""")));
}
