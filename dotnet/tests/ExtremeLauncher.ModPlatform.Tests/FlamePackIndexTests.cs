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
 * For FlamePackIndex, the CurseForge modpack listing parser. The behaviours worth pinning are the ones
 * specific to it: a pack with no usable default file is rejected, the logo name is built from the slug
 * plus the URL extension, links have trailing slashes trimmed, and a pack's files are sorted newest
 * first with the URL-less ones dropped.
 */

using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class FlamePackIndexTests
{
    private static JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(json), "test"));

    private static JsonArray ParseArray(string json)
        => Json.RequireArrayValue(Json.RequireDocument(Encoding.UTF8.GetBytes(json), "test"));

    private const string GoodPack = """
        {
          "id": 238222, "name": "All the Mods 9", "slug": "all-the-mods-9",
          "summary": "Kitchen sink.", "mainFileId": 5000,
          "logo": { "thumbnailUrl": "https://media.forgecdn.net/avatars/thumbnails/1/2/logo.png" },
          "authors": [ { "name": "ATMTeam", "url": "https://curseforge.com/members/atmteam" } ],
          "links": {
            "websiteUrl": "https://curseforge.com/atm9/",
            "issuesUrl": "https://github.com/atm/atm9/issues/",
            "sourceUrl": "", "wikiUrl": "https://atm.wiki/"
          },
          "latestFiles": [ { "id": 5000, "gameVersions": ["1.20.1", "Forge"] } ]
        }
        """;

    [Fact]
    public void AListingIsRead()
    {
        var pack = new IndexedPack();
        FlamePackIndex.LoadIndexedPack(pack, Parse(GoodPack));

        Assert.Equal("238222", pack.AddonId);
        Assert.Equal(ResourceProvider.Flame, pack.Provider);
        Assert.Equal("All the Mods 9", pack.Name);
        Assert.Equal("all-the-mods-9", pack.Slug);
        Assert.Equal("Kitchen sink.", pack.Description);
        Assert.Equal("https://media.forgecdn.net/avatars/thumbnails/1/2/logo.png", pack.LogoUrl);
        Assert.Equal("ATMTeam", Assert.Single(pack.Authors).Name);
    }

    /// <summary>The logo filename is the slug with the extension taken from the logo URL.</summary>
    [Fact]
    public void TheLogoNameIsSlugPlusUrlExtension()
    {
        var pack = new IndexedPack();
        FlamePackIndex.LoadIndexedPack(pack, Parse(GoodPack));

        Assert.Equal("all-the-mods-9.png", pack.LogoName);
    }

    /// <summary>Links load with any trailing slash trimmed; the website lands on the pack itself.</summary>
    [Fact]
    public void LinksAreTrimmedAndPlaced()
    {
        var pack = new IndexedPack();
        FlamePackIndex.LoadIndexedPack(pack, Parse(GoodPack));

        Assert.Equal("https://curseforge.com/atm9", pack.WebsiteUrl);
        Assert.Equal("https://github.com/atm/atm9/issues", pack.ExtraData.IssuesUrl);
        Assert.Equal("https://atm.wiki", pack.ExtraData.WikiUrl);
        Assert.Equal(string.Empty, pack.ExtraData.SourceUrl);
        Assert.True(pack.ExtraDataLoaded);
    }

    [Fact]
    public void APackWhoseDefaultFileHasNoGameVersionsIsRejected()
    {
        var json = Parse("""
            {
              "id": 1, "name": "Broken", "slug": "broken", "mainFileId": 5000,
              "logo": { "thumbnailUrl": "https://x.invalid/a.png" }, "authors": [],
              "latestFiles": [ { "id": 5000, "gameVersions": [] } ]
            }
            """);

        Assert.Throws<JsonException>(() => FlamePackIndex.LoadIndexedPack(new IndexedPack(), json));
    }

    [Fact]
    public void APackWithNoMatchingDefaultFileIsRejected()
    {
        var json = Parse("""
            {
              "id": 1, "name": "Broken", "slug": "broken", "mainFileId": 5000,
              "logo": { "thumbnailUrl": "https://x.invalid/a.png" }, "authors": [],
              "latestFiles": [ { "id": 4999, "gameVersions": ["1.20.1"] } ]
            }
            """);

        Assert.Throws<JsonException>(() => FlamePackIndex.LoadIndexedPack(new IndexedPack(), json));
    }

    [Fact]
    public void VersionsAreReadNewestFirstAndSplitIntoMcVersionsAndLoaders()
    {
        var pack = new IndexedPack { AddonId = "238222" };

        FlamePackIndex.LoadIndexedPackVersions(pack, ParseArray("""
            [
              { "id": 100, "displayName": "v1", "releaseType": 2,
                "gameVersions": ["1.20.1", "Forge"], "downloadUrl": "https://x.invalid/100.zip" },
              { "id": 300, "displayName": "v3", "releaseType": 1,
                "gameVersions": ["1.20.4", "NeoForge"], "downloadUrl": "https://x.invalid/300.zip" },
              { "id": 200, "displayName": "v2", "releaseType": 3,
                "gameVersions": ["1.20.2", "Fabric"], "downloadUrl": "https://x.invalid/200.zip" }
            ]
            """));

        Assert.True(pack.VersionsLoaded);
        Assert.Equal(["300", "200", "100"], pack.Versions.Select(v => v.FileId));

        var newest = pack.Versions[0];
        Assert.Equal("238222", newest.AddonId);
        Assert.Equal(VersionType.Release, newest.VersionType);
        Assert.Equal(["1.20.4"], newest.McVersion);
        Assert.Equal(ModLoaderTypes.NeoForge, newest.Loaders);
    }

    [Fact]
    public void AVersionWithNoDownloadUrlIsDropped()
    {
        var pack = new IndexedPack { AddonId = "1" };

        FlamePackIndex.LoadIndexedPackVersions(pack, ParseArray("""
            [
              { "id": 1, "displayName": "no-dl", "releaseType": 1, "gameVersions": ["1.20.1"] },
              { "id": 2, "displayName": "ok", "releaseType": 1,
                "gameVersions": ["1.20.1"], "downloadUrl": "https://x.invalid/2.zip" }
            ]
            """));

        var only = Assert.Single(pack.Versions);
        Assert.Equal("2", only.FileId);
    }

    [Fact]
    public void AVersionTargetingNoMinecraftVersionIsDropped()
    {
        var pack = new IndexedPack { AddonId = "1" };

        FlamePackIndex.LoadIndexedPackVersions(pack, ParseArray("""
            [ { "id": 1, "displayName": "empty", "releaseType": 1,
                "gameVersions": [], "downloadUrl": "https://x.invalid/1.zip" } ]
            """));

        Assert.Empty(pack.Versions);
    }
}
