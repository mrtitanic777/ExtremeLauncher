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
 * Most of these exist for one line: the heuristic that splits CurseForge's single "gameVersions"
 * array into Minecraft versions, loaders and sides. Getting it wrong does not throw -- it offers
 * Forge files to a Fabric instance -- so the classification is tested value by value.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class FlameModIndexTests
{
    private static JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(System.Text.Encoding.UTF8.GetBytes(json), "test"));

    private static JsonArray ParseArray(string json)
        => Json.RequireArray(Json.RequireDocument(System.Text.Encoding.UTF8.GetBytes(json), "test"));

    // ================================================================== projects

    [Fact]
    public void AProjectIsReadIntoTheSharedVocabulary()
    {
        var pack = new IndexedPack();

        FlameModIndex.LoadIndexedPack(pack, Parse("""
            {
              "id": 306612,
              "name": "Fabric API",
              "slug": "fabric-api",
              "summary": "Core API",
              "links": { "websiteUrl": "https://www.curseforge.com/minecraft/mc-mods/fabric-api" },
              "logo": { "title": "logo.png", "thumbnailUrl": "https://media.forgecdn.net/thumb.png" },
              "authors": [ { "name": "modmuss50", "url": "https://www.curseforge.com/members/modmuss50" } ]
            }
            """));

        // The numeric id becomes a string, invariantly — a grouping locale would corrupt it.
        Assert.Equal("306612", pack.AddonId);
        Assert.Equal(ResourceProvider.Flame, pack.Provider);
        Assert.Equal("https://media.forgecdn.net/thumb.png", pack.LogoUrl);
        Assert.Equal("modmuss50", Assert.Single(pack.Authors).Name);
    }

    /// <summary>The thumbnail feeds a list; the full image is the fallback, not the preference.</summary>
    [Fact]
    public void TheFullLogoIsUsedOnlyWhenThereIsNoThumbnail()
    {
        var pack = new IndexedPack();

        FlameModIndex.LoadIndexedPack(pack, Parse("""
            {
              "id": 1, "name": "n", "slug": "s",
              "logo": { "title": "t", "url": "https://media.forgecdn.net/full.png" }
            }
            """));

        Assert.Equal("https://media.forgecdn.net/full.png", pack.LogoUrl);
    }

    /*
     * The two halves of the extra data check each other: the description arrives from a separate
     * endpoint, so links alone are not "loaded" and a body alone is not either.
     */
    [Fact]
    public void ExtraDataIsCompleteOnlyOnceBothHalvesHaveArrived()
    {
        var pack = new IndexedPack();
        var project = Parse("""
            { "id": 1, "name": "n", "slug": "s",
              "links": { "issuesUrl": "https://github.com/x/issues/" } }
            """);

        FlameModIndex.LoadIndexedPack(pack, project);

        // Links are in, but the description has not been fetched yet.
        Assert.Equal("https://github.com/x/issues", pack.ExtraData.IssuesUrl);
        Assert.False(pack.ExtraDataLoaded);

        FlameModIndex.LoadBody(pack, "The long description.");

        Assert.True(pack.ExtraDataLoaded);
    }

    /// <summary>A project with neither links nor a body stays incomplete, and that is harmless.</summary>
    [Fact]
    public void AProjectWithNothingExtraNeverCompletes()
    {
        var pack = new IndexedPack();

        FlameModIndex.LoadIndexedPack(pack, Parse("""{ "id": 1, "name": "n", "slug": "s" }"""));
        FlameModIndex.LoadBody(pack, string.Empty);

        Assert.False(pack.ExtraDataLoaded);
    }

    // ================================================================== the gameVersions heuristic

    private static IndexedVersion? VersionWith(string gameVersions) => FlameModIndex.LoadIndexedPackVersion(Parse($$"""
        {
          "id": 3814740, "modId": 306612, "fileDate": "2023-06-12T10:00:00Z",
          "displayName": "Fabric API 0.83.0", "fileName": "fabric-api-0.83.0.jar",
          "releaseType": 1, "gameVersions": {{gameVersions}}
        }
        """));

    /// <summary>One array, three kinds of thing, told apart by shape.</summary>
    [Fact]
    public void GameVersionsAreSplitIntoVersionsLoadersAndSides()
    {
        var version = VersionWith("""["1.20.1", "Forge", "Client", "1.20"]""");

        Assert.Equal(["1.20.1", "1.20"], version!.McVersion);
        Assert.Equal(ModLoaderTypes.Forge, version.Loaders);
        Assert.Equal("client", version.Side);
    }

    /// <summary>CurseForge capitalises loader names; the launcher's spelling is lowercase.</summary>
    [Theory]
    [InlineData("Forge", ModLoaderTypes.Forge)]
    [InlineData("NeoForge", ModLoaderTypes.NeoForge)]
    [InlineData("Fabric", ModLoaderTypes.Fabric)]
    [InlineData("Quilt", ModLoaderTypes.Quilt)]
    [InlineData("Cauldron", ModLoaderTypes.Cauldron)]
    [InlineData("LiteLoader", ModLoaderTypes.LiteLoader)]
    public void LoaderNamesAreMatchedCaseInsensitively(string name, ModLoaderTypes expected)
        => Assert.Equal(expected, VersionWith($"""["1.20.1", "{name}"]""")!.Loaders);

    /// <summary>A file can target several loaders, and they accumulate.</summary>
    [Fact]
    public void SeveralLoadersCombine()
        => Assert.Equal(
            ModLoaderTypes.Fabric | ModLoaderTypes.Quilt,
            VersionWith("""["1.20.1", "Fabric", "Quilt"]""")!.Loaders);

    /// <summary>Sides accumulate too: listing both promotes the value rather than overwriting it.</summary>
    [Theory]
    [InlineData("""["1.20.1", "Client"]""", "client")]
    [InlineData("""["1.20.1", "Server"]""", "server")]
    [InlineData("""["1.20.1", "Client", "Server"]""", "both")]
    [InlineData("""["1.20.1", "Server", "Client"]""", "both")]
    [InlineData("""["1.20.1", "Client", "Client"]""", "client")]
    [InlineData("""["1.20.1"]""", "")]
    public void SidesAccumulateAcrossTheArray(string gameVersions, string expected)
        => Assert.Equal(expected, VersionWith(gameVersions)!.Side);

    /*
     * The dot is the whole test for "is this a Minecraft version". It is crude and it holds: every
     * Minecraft version has one and no loader or side name does. Snapshot versions are the case most
     * likely to break it, so they are checked explicitly.
     */
    [Fact]
    public void ADotIsWhatMakesAStringAMinecraftVersion()
    {
        Assert.Equal(["1.20.1", "1.7.10"], VersionWith("""["1.20.1", "Forge", "1.7.10"]""")!.McVersion);

        // A snapshot has no dot, so it is not recognised as a version -- upstream's behaviour.
        Assert.Empty(VersionWith("""["23w31a", "Forge"]""")!.McVersion);
    }

    /// <summary>An unrecognised entry is neither a version nor a loader, and is simply dropped.</summary>
    [Fact]
    public void UnrecognisedEntriesAreIgnored()
    {
        var version = VersionWith("""["1.20.1", "Forge", "SomethingNew"]""");

        Assert.Equal(["1.20.1"], version!.McVersion);
        Assert.Equal(ModLoaderTypes.Forge, version.Loaders);
    }

    /// <summary>A file targeting no Minecraft version at all is unusable.</summary>
    [Fact]
    public void AFileWithNoGameVersionsIsNull()
        => Assert.Null(VersionWith("[]"));

    // ================================================================== files

    [Fact]
    public void ReleaseTypesAreCurseforgesNumbering()
    {
        Assert.Equal(VersionType.Release, ReleaseType(1));
        Assert.Equal(VersionType.Beta, ReleaseType(2));
        Assert.Equal(VersionType.Alpha, ReleaseType(3));

        // Anything else is Unknown, which sorts last.
        Assert.Equal(VersionType.Unknown, ReleaseType(99));

        static VersionType ReleaseType(int type) => FlameModIndex.LoadIndexedPackVersion(Parse($$"""
            {
              "id": 1, "modId": 2, "fileDate": "d", "displayName": "n", "fileName": "f.jar",
              "releaseType": {{type}}, "gameVersions": ["1.20.1"]
            }
            """))!.VersionType;
    }

    /*
     * A MISSING DOWNLOAD URL IS NORMAL, unlike on Modrinth where it invalidates the version.
     * CurseForge omits it for projects whose authors forbid third-party downloads, and the file is
     * still a real file -- what to do about it belongs to the installer, not the parser.
     */
    [Fact]
    public void AFileWithNoDownloadUrlIsStillAValidFile()
    {
        var version = FlameModIndex.LoadIndexedPackVersion(Parse("""
            {
              "id": 1, "modId": 2, "fileDate": "d", "displayName": "n", "fileName": "f.jar",
              "releaseType": 1, "gameVersions": ["1.20.1"]
            }
            """));

        Assert.NotNull(version);
        Assert.Equal(string.Empty, version.DownloadUrl);
    }

    [Theory]
    [InlineData(1, "sha1")]
    [InlineData(2, "md5")]
    public void HashAlgorithmsAreCurseforgesNumbering(int algorithm, string expected)
        => Assert.Equal(expected, FlameModIndex.HashAlgorithmFromId(algorithm));

    /*
     * CARRIED-OVER HAZARD, pinned. Upstream's default case shares with 1, so an undocumented
     * algorithm id is reported as "sha1" -- a hash labelled with an algorithm that did not produce
     * it. Kept because changing it would reject files that work today.
     */
    [Fact]
    public void AnUnknownHashAlgorithmIsReportedAsSha1()
        => Assert.Equal("sha1", FlameModIndex.HashAlgorithmFromId(99));

    /// <summary>The ARRAY's order decides, not a preference order — unlike the Modrinth parser.</summary>
    [Fact]
    public void TheFirstListedHashWinsRatherThanTheStrongest()
    {
        var version = FlameModIndex.LoadIndexedPackVersion(Parse("""
            {
              "id": 1, "modId": 2, "fileDate": "d", "displayName": "n", "fileName": "f.jar",
              "releaseType": 1, "gameVersions": ["1.20.1"],
              "hashes": [ { "algo": 2, "value": "md5hash" }, { "algo": 1, "value": "sha1hash" } ]
            }
            """));

        Assert.Equal("md5", version!.HashType);
        Assert.Equal("md5hash", version.Hash);
    }

    [Theory]
    [InlineData(1, DependencyType.Embedded)]
    [InlineData(2, DependencyType.Optional)]
    [InlineData(3, DependencyType.Required)]
    [InlineData(4, DependencyType.Tool)]
    [InlineData(5, DependencyType.Incompatible)]
    [InlineData(6, DependencyType.Include)]
    [InlineData(99, DependencyType.Unknown)]
    public void RelationTypesAreCurseforgesNumbering(int relation, DependencyType expected)
        => Assert.Equal(expected, FlameModIndex.DependencyTypeFromRelation(relation));

    /// <summary>CurseForge dependencies name a project only — there is no version field to read.</summary>
    [Fact]
    public void DependenciesCarryAProjectButNeverAVersion()
    {
        var version = FlameModIndex.LoadIndexedPackVersion(Parse("""
            {
              "id": 1, "modId": 2, "fileDate": "d", "displayName": "n", "fileName": "f.jar",
              "releaseType": 1, "gameVersions": ["1.20.1"],
              "dependencies": [ { "modId": 306612, "relationType": 3 } ]
            }
            """));

        var dependency = Assert.Single(version!.Dependencies);

        Assert.Equal("306612", dependency.AddonId);
        Assert.Equal(DependencyType.Required, dependency.Type);
        Assert.Equal(string.Empty, dependency.Version);
    }

    // ================================================================== ordering and filtering

    private static string FileAt(int id, string date, string gameVersions = """["1.20.1", "Fabric"]""") => $$"""
        {
          "id": {{id}}, "modId": 306612, "fileDate": "{{date}}",
          "displayName": "n", "fileName": "f.jar", "releaseType": 1,
          "gameVersions": {{gameVersions}}
        }
        """;

    [Fact]
    public void FilesComeOutNewestFirst()
    {
        var pack = new IndexedPack();

        FlameModIndex.LoadIndexedPackVersions(pack, ParseArray($"""
            [ {FileAt(1, "2022-01-01T00:00:00Z")},
              {FileAt(3, "2024-01-01T00:00:00Z")},
              {FileAt(2, "2023-01-01T00:00:00Z")} ]
            """));

        Assert.Equal(["3", "2", "1"], pack.Versions.Select(v => v.FileId));
        Assert.True(pack.VersionsLoaded);
    }

    /// <summary>Files sharing a timestamp keep CurseForge's order, rather than being reshuffled.</summary>
    [Fact]
    public void FilesSharingATimestampKeepTheProvidersOrder()
    {
        var pack = new IndexedPack();

        FlameModIndex.LoadIndexedPackVersions(pack, ParseArray($"""
            [ {FileAt(1, "2023-01-01T00:00:00Z")},
              {FileAt(2, "2023-01-01T00:00:00Z")},
              {FileAt(3, "2023-01-01T00:00:00Z")} ]
            """));

        Assert.Equal(["1", "2", "3"], pack.Versions.Select(v => v.FileId));
    }

    [Fact]
    public void ADependencyResolvesToTheNewestFileThatFitsTheLoader()
    {
        var array = ParseArray($"""
            [ {FileAt(1, "2022-01-01T00:00:00Z")},
              {FileAt(2, "2024-01-01T00:00:00Z", """["1.20.1", "Forge"]""")} ]
            """);

        Assert.Equal("1", FlameModIndex.LoadDependencyVersions(array, ModLoaderTypes.Fabric)!.FileId);
        Assert.Equal("2", FlameModIndex.LoadDependencyVersions(array, loaders: null)!.FileId);
        Assert.Null(FlameModIndex.LoadDependencyVersions(array, ModLoaderTypes.Quilt));
    }

    /// <summary>A file declaring no loader fits anything — resource packs and data packs have none.</summary>
    [Fact]
    public void AFileWithNoLoaderFitsAnyLoader()
    {
        var array = ParseArray($"""[ {FileAt(1, "2023-01-01T00:00:00Z", """["1.20.1"]""")} ]""");

        Assert.Equal("1", FlameModIndex.LoadDependencyVersions(array, ModLoaderTypes.Forge)!.FileId);
    }

    /// <summary>One unusable file does not lose the rest.</summary>
    [Fact]
    public void AFileWithNoGameVersionsIsDroppedFromTheList()
    {
        var pack = new IndexedPack();

        FlameModIndex.LoadIndexedPackVersions(pack, ParseArray($"""
            [ {FileAt(1, "2023-01-01T00:00:00Z")}, {FileAt(2, "2024-01-01T00:00:00Z", "[]")} ]
            """));

        Assert.Equal("1", Assert.Single(pack.Versions).FileId);
    }
}
