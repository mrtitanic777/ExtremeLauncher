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
 * Upstream has no test for this parser, so these are characterization tests written against
 * Modrinth's documented response shapes. They cover the paths that decide what actually gets
 * installed -- which file of a multi-file version, which hash it is checked against, and which
 * versions are rejected outright -- because those fail silently rather than loudly.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class ModrinthPackIndexTests
{
    private static JsonObject Parse(string json) => Json.RequireObject(Json.RequireDocument(
        System.Text.Encoding.UTF8.GetBytes(json), "test"));

    private static JsonArray ParseArray(string json)
        => Json.RequireArray(Json.RequireDocument(System.Text.Encoding.UTF8.GetBytes(json), "test"));

    // ================================================================== projects

    /// <summary>A search hit carries "project_id"; a direct project fetch carries "id".</summary>
    [Fact]
    public void TheProjectIdIsReadFromEitherKey()
    {
        var fromSearch = new IndexedPack();
        ModrinthPackIndex.LoadIndexedPack(fromSearch, Parse("""
            { "project_id": "P7dR8mSH", "title": "Fabric API" }
            """));

        var fromFetch = new IndexedPack();
        ModrinthPackIndex.LoadIndexedPack(fromFetch, Parse("""
            { "id": "P7dR8mSH", "title": "Fabric API" }
            """));

        Assert.Equal("P7dR8mSH", fromSearch.AddonId);
        Assert.Equal("P7dR8mSH", fromFetch.AddonId);
    }

    [Fact]
    public void AProjectWithNeitherIdIsRefused()
        => Assert.ThrowsAny<LauncherException>(
            () => ModrinthPackIndex.LoadIndexedPack(new IndexedPack(), Parse("""{ "title": "Nameless" }""")));

    [Fact]
    public void AProjectIsReadIntoTheSharedVocabulary()
    {
        var pack = new IndexedPack();

        ModrinthPackIndex.LoadIndexedPack(pack, Parse("""
            {
              "project_id": "P7dR8mSH",
              "title": "Fabric API",
              "slug": "fabric-api",
              "description": "Core API",
              "icon_url": "https://cdn.modrinth.com/icon.png",
              "author": "modmuss50",
              "client_side": "required",
              "server_side": "required"
            }
            """));

        Assert.Equal(ResourceProvider.Modrinth, pack.Provider);
        Assert.Equal("Fabric API", pack.Name);
        Assert.Equal("https://modrinth.com/mod/fabric-api", pack.WebsiteUrl);
        Assert.Equal("https://cdn.modrinth.com/icon.png", pack.LogoUrl);

        // The id doubles as the icon cache key, so it survives a rename.
        Assert.Equal("P7dR8mSH", pack.LogoName);

        Assert.Equal("https://modrinth.com/user/modmuss50", Assert.Single(pack.Authors).Url);
        Assert.Equal("both", pack.Side);

        // Modrinth has more to say than a search hit carries.
        Assert.False(pack.ExtraDataLoaded);
    }

    /// <summary>No slug means no page, and an empty URL rather than a half-built one.</summary>
    [Fact]
    public void AProjectWithNoSlugGetsNoWebsiteUrl()
    {
        var pack = new IndexedPack();
        ModrinthPackIndex.LoadIndexedPack(pack, Parse("""{ "id": "abc", "title": "T" }"""));

        Assert.Equal(string.Empty, pack.WebsiteUrl);
        Assert.Equal("No author(s)", Assert.Single(pack.Authors).Name);
    }

    /// <summary>"optional" counts as supported — an optional client mod still runs on the client.</summary>
    [Theory]
    [InlineData("required", "required", "both")]
    [InlineData("optional", "optional", "both")]
    [InlineData("required", "unsupported", "client")]
    [InlineData("unsupported", "required", "server")]
    [InlineData("optional", "unsupported", "client")]
    public void SidesAreDerivedFromBothMarkings(string client, string server, string expected)
    {
        var pack = new IndexedPack();

        ModrinthPackIndex.LoadIndexedPack(pack, Parse($$"""
            { "id": "a", "title": "T", "client_side": "{{client}}", "server_side": "{{server}}" }
            """));

        Assert.Equal(expected, pack.Side);
    }

    /// <summary>Unsupported on both leaves the side unset rather than inventing "none".</summary>
    [Fact]
    public void APackSupportedNowhereGetsNoSide()
    {
        var pack = new IndexedPack();

        ModrinthPackIndex.LoadIndexedPack(pack, Parse("""
            { "id": "a", "title": "T", "client_side": "unsupported", "server_side": "unsupported" }
            """));

        Assert.Equal(string.Empty, pack.Side);
    }

    // ================================================================== extra data

    [Fact]
    public void ExtraDataTrimsTrailingSlashesAndStripsLineBreakTags()
    {
        var pack = new IndexedPack();

        ModrinthPackIndex.LoadExtraPackData(pack, Parse("""
            {
              "issues_url": "https://github.com/x/issues/",
              "source_url": "https://github.com/x",
              "wiki_url": "https://wiki.example/",
              "discord_url": "https://discord.gg/x/",
              "status": "approved",
              "body": "line one<br>line two<br>",
              "donation_urls": [ { "id": "patreon", "platform": "Patreon", "url": "https://patreon.com/x" } ]
            }
            """));

        Assert.Equal("https://github.com/x/issues", pack.ExtraData.IssuesUrl);

        // Already clean, and left alone.
        Assert.Equal("https://github.com/x", pack.ExtraData.SourceUrl);
        Assert.Equal("https://wiki.example", pack.ExtraData.WikiUrl);
        Assert.Equal("https://discord.gg/x", pack.ExtraData.DiscordUrl);

        // Every <br>, since the body is Markdown and a stray tag renders literally.
        Assert.Equal("line oneline two", pack.ExtraData.Body);

        Assert.Equal("Patreon", Assert.Single(pack.ExtraData.Donate).Platform);
        Assert.True(pack.ExtraDataLoaded);
    }

    // ================================================================== versions

    private const string OneVersion = """
        [{
          "project_id": "P7dR8mSH",
          "id": "abc123",
          "date_published": "2023-06-12T10:00:00Z",
          "game_versions": ["1.20.1", "1.20"],
          "loaders": ["fabric", "quilt"],
          "name": "Fabric API 0.83.0",
          "version_number": "0.83.0+1.20.1",
          "version_type": "release",
          "changelog": "Fixes",
          "dependencies": [
            { "project_id": "dep1", "version_id": "v1", "dependency_type": "required" },
            { "project_id": "dep2", "dependency_type": "embedded" }
          ],
          "files": [{
            "url": "https://cdn.modrinth.com/fabric-api.jar",
            "filename": "fabric-api-0.83.0.jar",
            "primary": true,
            "hashes": { "sha1": "aaa", "sha512": "bbb" }
          }]
        }]
        """;

    [Fact]
    public void AVersionIsReadIntoTheSharedVocabulary()
    {
        var version = ModrinthPackIndex.LoadIndexedPackVersion(Json.RequireObject(ParseArray(OneVersion)[0]));

        Assert.NotNull(version);
        Assert.Equal("abc123", version.FileId);
        Assert.Equal(["1.20.1", "1.20"], version.McVersion);
        Assert.Equal(ModLoaderTypes.Fabric | ModLoaderTypes.Quilt, version.Loaders);
        Assert.Equal(VersionType.Release, version.VersionType);
        Assert.Equal("fabric-api-0.83.0.jar", version.FileName);

        Assert.Equal(2, version.Dependencies.Count);
        Assert.Equal(DependencyType.Required, version.Dependencies[0].Type);
        Assert.Equal(DependencyType.Embedded, version.Dependencies[1].Type);
    }

    /// <summary>sha512 is preferred, so a file with several hashes is checked against the strongest.</summary>
    [Fact]
    public void ThePreferredHashWinsAndTheFallbackFollowsProviderOrder()
    {
        var obj = Json.RequireObject(ParseArray(OneVersion)[0]);

        var preferred = ModrinthPackIndex.LoadIndexedPackVersion(obj);
        Assert.Equal("sha512", preferred!.HashType);
        Assert.Equal("bbb", preferred.Hash);

        // Asking for one that is not there falls back to the provider's own order, best first.
        var fallback = ModrinthPackIndex.LoadIndexedPackVersion(obj, preferredHashType: "md5");
        Assert.Equal("sha512", fallback!.HashType);
    }

    [Fact]
    public void AHashListWithOnlyASha1StillReports()
    {
        var version = ModrinthPackIndex.LoadIndexedPackVersion(Parse("""
            {
              "project_id": "p", "id": "i", "date_published": "2023-01-01T00:00:00Z",
              "game_versions": ["1.20.1"], "loaders": ["fabric"], "name": "n",
              "version_number": "1", "version_type": "release", "changelog": "",
              "files": [{ "url": "u", "filename": "f.jar", "primary": true, "hashes": { "sha1": "aaa" } }]
            }
            """));

        Assert.Equal("sha1", version!.HashType);
        Assert.Equal("aaa", version.Hash);
    }

    /*
     * The three ways a version is unusable. Each returns null rather than a blank object that a
     * caller has to recognise by testing a field for emptiness.
     */
    [Theory]
    [InlineData("""{ "project_id": "p", "id": "i", "date_published": "d", "game_versions": [], "loaders": [], "name": "n", "version_number": "1", "version_type": "release", "changelog": "", "files": [{"url":"u","filename":"f","primary":true,"hashes":{}}] }""")]
    [InlineData("""{ "project_id": "p", "id": "i", "date_published": "d", "game_versions": ["1.20.1"], "loaders": [], "name": "n", "version_number": "1", "version_type": "release", "changelog": "", "files": [] }""")]
    [InlineData("""{ "project_id": "p", "id": "i", "date_published": "d", "game_versions": ["1.20.1"], "loaders": [], "name": "n", "version_number": "1", "version_type": "release", "changelog": "", "files": [{"filename":"f","primary":true,"hashes":{}}] }""")]
    public void AnUnusableVersionIsNullRatherThanBlank(string json)
        => Assert.Null(ModrinthPackIndex.LoadIndexedPackVersion(Parse(json)));

    /// <summary>Modrinth adds loader names over time; an unknown one is ignored, not fatal.</summary>
    [Fact]
    public void AnUnrecognisedLoaderNameIsIgnored()
    {
        var version = ModrinthPackIndex.LoadIndexedPackVersion(Parse("""
            {
              "project_id": "p", "id": "i", "date_published": "d",
              "game_versions": ["1.20.1"], "loaders": ["fabric", "someNewLoader"], "name": "n",
              "version_number": "1", "version_type": "release", "changelog": "",
              "files": [{ "url": "u", "filename": "f.jar", "primary": true, "hashes": {} }]
            }
            """));

        Assert.Equal(ModLoaderTypes.Fabric, version!.Loaders);
    }

    // ================================================================== file selection

    private static string TwoFiles(bool firstIsPrimary) => $$"""
        {
          "project_id": "p", "id": "i", "date_published": "d",
          "game_versions": ["1.20.1"], "loaders": ["fabric"], "name": "n",
          "version_number": "1", "version_type": "release", "changelog": "",
          "files": [
            { "url": "u1", "filename": "mod-sources.jar", "primary": {{(firstIsPrimary ? "true" : "false")}}, "hashes": {} },
            { "url": "u2", "filename": "mod.jar", "primary": true, "hashes": {} }
          ]
        }
        """;

    [Fact]
    public void ThePrimaryFileIsChosenFromAMultiFileVersion()
    {
        Assert.Equal("mod-sources.jar", ModrinthPackIndex.LoadIndexedPackVersion(Parse(TwoFiles(true)))!.FileName);
        Assert.Equal("mod.jar", ModrinthPackIndex.LoadIndexedPackVersion(Parse(TwoFiles(false)))!.FileName);
    }

    /// <summary>A name match beats the primary flag, for matching a file a user already has.</summary>
    [Fact]
    public void APreferredFileNameOutranksThePrimaryFlag()
    {
        var version = ModrinthPackIndex.LoadIndexedPackVersion(
            Parse(TwoFiles(false)),
            preferredFileName: "sources");

        Assert.Equal("mod-sources.jar", version!.FileName);
    }

    /*
     * UPSTREAM BUG, reproduced rather than fixed. Choosing a file by name sets is_preferred inside
     * the selection loop, and the line after the loop overwrites it unconditionally -- so that
     * assignment is dead. Left as found: "preferred" is read everywhere as "Modrinth's canonical
     * file", and making a name match set it would change what the flag means.
     */
    [Fact]
    public void ChoosingAFileByNameDoesNotMarkItPreferred()
    {
        var version = ModrinthPackIndex.LoadIndexedPackVersion(
            Parse(TwoFiles(false)),
            preferredFileName: "sources");

        Assert.Equal("mod-sources.jar", version!.FileName);
        Assert.False(version.IsPreferred);
    }

    /// <summary>A lone file is preferred whatever its flag says — there is nothing else to pick.</summary>
    [Fact]
    public void ASingleFileIsAlwaysPreferred()
    {
        var version = ModrinthPackIndex.LoadIndexedPackVersion(Parse("""
            {
              "project_id": "p", "id": "i", "date_published": "d",
              "game_versions": ["1.20.1"], "loaders": [], "name": "n",
              "version_number": "1", "version_type": "release", "changelog": "",
              "files": [{ "url": "u", "filename": "f.jar", "primary": false, "hashes": {} }]
            }
            """));

        Assert.True(version!.IsPreferred);
    }

    // ================================================================== ordering and filtering

    private static string VersionAt(string id, string date, string loaders = """["fabric"]""") => $$"""
        {
          "project_id": "p", "id": "{{id}}", "date_published": "{{date}}",
          "game_versions": ["1.20.1"], "loaders": {{loaders}}, "name": "n",
          "version_number": "1", "version_type": "release", "changelog": "",
          "files": [{ "url": "u", "filename": "f.jar", "primary": true, "hashes": {} }]
        }
        """;

    [Fact]
    public void VersionsComeOutNewestFirst()
    {
        var pack = new IndexedPack();

        ModrinthPackIndex.LoadIndexedPackVersions(pack, ParseArray($"""
            [ {VersionAt("old", "2022-01-01T00:00:00Z")},
              {VersionAt("new", "2024-01-01T00:00:00Z")},
              {VersionAt("mid", "2023-01-01T00:00:00Z")} ]
            """));

        Assert.Equal(["new", "mid", "old"], pack.Versions.Select(v => v.FileId));
        Assert.True(pack.VersionsLoaded);
    }

    /*
     * Versions published in the same second keep the order the provider sent them in. Upstream's
     * std::sort is not stable, so "the newest version" is a coin flip for a project that published
     * several at once; a stable sort makes it repeatable and costs nothing.
     */
    [Fact]
    public void VersionsSharingATimestampKeepTheProvidersOrder()
    {
        var pack = new IndexedPack();

        ModrinthPackIndex.LoadIndexedPackVersions(pack, ParseArray($"""
            [ {VersionAt("first", "2023-01-01T00:00:00Z")},
              {VersionAt("second", "2023-01-01T00:00:00Z")},
              {VersionAt("third", "2023-01-01T00:00:00Z")} ]
            """));

        Assert.Equal(["first", "second", "third"], pack.Versions.Select(v => v.FileId));
    }

    /// <summary>An unusable version is dropped from the list rather than failing the whole load.</summary>
    [Fact]
    public void OneBadVersionDoesNotLoseTheGoodOnes()
    {
        var pack = new IndexedPack();

        ModrinthPackIndex.LoadIndexedPackVersions(pack, ParseArray($$"""
            [ {{VersionAt("good", "2023-01-01T00:00:00Z")}},
              { "project_id": "p", "id": "bad", "date_published": "d", "game_versions": [],
                "loaders": [], "name": "n", "version_number": "1", "version_type": "release",
                "changelog": "", "files": [] } ]
            """));

        Assert.Equal("good", Assert.Single(pack.Versions).FileId);
    }

    [Fact]
    public void ADependencyResolvesToTheNewestVersionThatFitsTheLoader()
    {
        var array = ParseArray($"""
            [ {VersionAt("fabricOld", "2022-01-01T00:00:00Z")},
              {VersionAt("forgeNew", "2024-01-01T00:00:00Z", """["forge"]""")} ]
            """);

        Assert.Equal("fabricOld", ModrinthPackIndex.LoadDependencyVersions(array, ModLoaderTypes.Fabric)!.FileId);

        // With no loader known, the newest wins outright.
        Assert.Equal("forgeNew", ModrinthPackIndex.LoadDependencyVersions(array, loaders: null)!.FileId);
    }

    /*
     * A version declaring NO loaders passes the filter. Resource packs and data packs have none, and
     * excluding them would make every non-mod dependency unresolvable.
     */
    [Fact]
    public void AVersionWithNoLoadersFitsAnyLoader()
    {
        var array = ParseArray($"""[ {VersionAt("loaderless", "2023-01-01T00:00:00Z", "[]")} ]""");

        Assert.Equal("loaderless", ModrinthPackIndex.LoadDependencyVersions(array, ModLoaderTypes.Forge)!.FileId);
    }

    [Fact]
    public void ADependencyWithNothingSuitableIsNull()
    {
        var array = ParseArray($"""[ {VersionAt("fabricOnly", "2023-01-01T00:00:00Z")} ]""");

        Assert.Null(ModrinthPackIndex.LoadDependencyVersions(array, ModLoaderTypes.Forge));
        Assert.Null(ModrinthPackIndex.LoadDependencyVersions(ParseArray("[]"), ModLoaderTypes.Fabric));
    }
}
