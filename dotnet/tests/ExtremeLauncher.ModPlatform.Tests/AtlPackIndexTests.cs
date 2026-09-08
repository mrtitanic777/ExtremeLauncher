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
 * For the readers that surround the manifest: the pack index (the browser's list), the share code (a
 * saved selection of optional mods), and loadVersion (the whole-version assembly the manifest tests
 * only exercise piece by piece). These pin the wiring — which sections are read, how a safe icon name
 * is derived, and that an error response carries no data.
 */

using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class AtlPackIndexTests
{
    private static JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(json), "test"));

    // ================================================================== pack index

    [Fact]
    public void AnIndexedPackIsRead()
    {
        var pack = AtlPackIndex.LoadIndexedPack(Parse("""
            {
              "id": 42, "position": 3, "name": "All The Mods 9", "type": "public",
              "system": false, "description": "Kitchen sink.",
              "versions": [ { "version": "0.2.0", "minecraft": "1.20.1" },
                            { "version": "0.1.0", "minecraft": "1.20.1" } ]
            }
            """));

        Assert.Equal(42, pack.Id);
        Assert.Equal(3, pack.Position);
        Assert.Equal("All The Mods 9", pack.Name);
        Assert.Equal(AtlPackType.Public, pack.Type);
        Assert.False(pack.System);
        Assert.Equal("Kitchen sink.", pack.Description);
        Assert.Equal(["0.2.0", "0.1.0"], pack.Versions.Select(v => v.Version));
        Assert.Equal("1.20.1", pack.Versions[0].Minecraft);
    }

    [Fact]
    public void APrivateTypeIsRecognised()
    {
        var pack = AtlPackIndex.LoadIndexedPack(Parse("""
            { "id": 1, "position": 1, "name": "Secret", "type": "private", "versions": [] }
            """));

        Assert.Equal(AtlPackType.Private, pack.Type);
    }

    /// <summary>The icon name strips everything but letters and digits, lowercases, and adds ".png".</summary>
    [Fact]
    public void TheSafeNameIsDerivedFromTheName()
    {
        var pack = AtlPackIndex.LoadIndexedPack(Parse("""
            { "id": 1, "position": 1, "name": "FTB: Sky Odyssey (2)!", "type": "public", "versions": [] }
            """));

        Assert.Equal("ftbskyodyssey2.png", pack.SafeName);
    }

    [Fact]
    public void SystemAndDescriptionDefaultWhenAbsent()
    {
        var pack = AtlPackIndex.LoadIndexedPack(Parse("""
            { "id": 1, "position": 1, "name": "Bare", "type": "public", "versions": [] }
            """));

        Assert.False(pack.System);
        Assert.Equal(string.Empty, pack.Description);
    }

    // ================================================================== share code

    [Fact]
    public void ASuccessfulShareCodeCarriesItsSelection()
    {
        var response = AtlShareCodeReader.LoadResponse(Parse("""
            {
              "error": false, "code": 200, "message": "OK",
              "data": {
                "pack": "atm9", "version": "0.2.0",
                "mods": { "optional": [ { "selected": true, "name": "JEI" },
                                        { "selected": false, "name": "Optifine" } ] }
              }
            }
            """));

        Assert.False(response.Error);
        Assert.Equal(200, response.Code);
        Assert.Equal("OK", response.Message);
        Assert.NotNull(response.Data);
        Assert.Equal("atm9", response.Data!.Pack);
        Assert.Equal("0.2.0", response.Data.Version);
        Assert.Collection(
            response.Data.Mods,
            m => Assert.True(m is { Name: "JEI", Selected: true }),
            m => Assert.True(m is { Name: "Optifine", Selected: false }));
    }

    /// <summary>An error response has no data, and upstream never reads it.</summary>
    [Fact]
    public void AnErrorResponseHasNoData()
    {
        var response = AtlShareCodeReader.LoadResponse(Parse("""
            { "error": true, "code": 404, "message": "Not found" }
            """));

        Assert.True(response.Error);
        Assert.Equal(404, response.Code);
        Assert.Equal("Not found", response.Message);
        Assert.Null(response.Data);
    }

    /// <summary>A null message is tolerated rather than required.</summary>
    [Fact]
    public void ANullMessageIsLeftEmpty()
    {
        var response = AtlShareCodeReader.LoadResponse(Parse("""
            { "error": true, "code": 500, "message": null }
            """));

        Assert.Equal(string.Empty, response.Message);
    }

    // ================================================================== loadVersion

    [Fact]
    public void AWholeVersionIsAssembled()
    {
        var version = AtlPackManifest.LoadVersion(Parse("""
            {
              "version": "0.2.0", "minecraft": "1.20.1", "noConfigs": false,
              "mainClass": { "mainClass": "net.example.Main", "depends": "SomeMod" },
              "extraArguments": { "arguments": "-Dfml.x=1", "depends": "OtherMod" },
              "loader": { "type": "forge", "metadata": { "version": "47.1.0", "recommended": true } },
              "libraries": [ { "url": "https://atl.invalid/l.jar", "file": "l.jar",
                               "md5": "abc", "download": "server" } ],
              "mods": [ { "name": "JEI", "version": "1.0", "url": "https://atl.invalid/jei.jar",
                          "file": "jei.jar", "download": "direct", "type": "mods" } ],
              "configs": { "filesize": 1024, "sha1": "deadbeef" },
              "colours": { "warn": "FF0000" },
              "warnings": { "old": "This mod is outdated" },
              "messages": { "install": "Enjoy", "update": "Updated" },
              "keeps": { "files": [ { "base": "config", "target": "options.txt" } ] },
              "deletes": { "folders": [ { "base": "root", "target": "old" } ] }
            }
            """));

        Assert.Equal("0.2.0", version.Version);
        Assert.Equal("1.20.1", version.Minecraft);
        Assert.False(version.NoConfigs);
        Assert.Equal("net.example.Main", version.MainClass.MainClass);
        Assert.Equal("SomeMod", version.MainClass.Depends);
        Assert.Equal("-Dfml.x=1", version.ExtraArguments.Arguments);
        Assert.Equal("forge", version.Loader.Type);
        Assert.Equal("47.1.0", version.Loader.Version);
        Assert.True(version.Loader.Recommended);
        Assert.Single(version.Libraries);
        Assert.Equal(AtlDownloadType.Server, version.Libraries[0].Download);
        Assert.Single(version.Mods);
        Assert.Equal("JEI", version.Mods[0].Name);
        Assert.Equal(1024, version.Configs.FileSize);
        Assert.Equal("FF0000", version.Colours["warn"]);
        Assert.Equal("This mod is outdated", version.Warnings["old"]);
        Assert.Equal("Enjoy", version.Messages.Install);
        Assert.Equal("Updated", version.Messages.Update);
        Assert.Equal("options.txt", Assert.Single(version.Keeps.Files).Target);
        Assert.Equal("old", Assert.Single(version.Deletes.Folders).Target);
    }

    /// <summary>Only version and minecraft are required; every nested section is optional.</summary>
    [Fact]
    public void AMinimalVersionOmitsEveryOptionalSection()
    {
        var version = AtlPackManifest.LoadVersion(Parse("""
            { "version": "1.0", "minecraft": "1.7.10" }
            """));

        Assert.Equal("1.0", version.Version);
        Assert.False(version.NoConfigs);
        Assert.Empty(version.Mods);
        Assert.Empty(version.Libraries);
        Assert.Empty(version.Colours);
        Assert.Empty(version.Keeps.Files);
        Assert.Empty(version.Deletes.Folders);
        Assert.Equal(string.Empty, version.Loader.Type);
        Assert.Equal(string.Empty, version.Messages.Install);
    }

    // ================================================================== pack source

    private static byte[] Bytes(string json) => System.Text.Encoding.UTF8.GetBytes(json);

    [Fact]
    public void TheListUrlIsUnderTheServer()
        => Assert.Equal(
            "https://atl.invalid/atl/launcher/json/packsnew.json", AtlPackSource.ListUrl("https://atl.invalid/atl/"));

    [Fact]
    public void ParsingReadsEveryPackInTheArray()
    {
        var packs = AtlPackSource.Parse(Bytes("""
            [
              { "id": 1, "position": 1, "name": "One", "type": "public", "versions": [] },
              { "id": 2, "position": 2, "name": "Two", "type": "private", "versions": [] }
            ]
            """));

        Assert.Equal(["One", "Two"], packs.Select(p => p.Name));
    }

    /// <summary>A malformed pack is skipped, and the rest of the list still loads.</summary>
    [Fact]
    public void AMalformedPackIsSkippedNotFatal()
    {
        var packs = AtlPackSource.Parse(Bytes("""
            [
              { "id": 1, "position": 1, "name": "Good", "type": "public", "versions": [] },
              { "id": 2, "position": 2, "type": "public", "versions": [] },
              { "id": 3, "position": 3, "name": "AlsoGood", "type": "public", "versions": [] }
            ]
            """));

        Assert.Equal(["Good", "AlsoGood"], packs.Select(p => p.Name));
    }

    [Fact]
    public void AnEmptyListParsesToNothing()
        => Assert.Empty(AtlPackSource.Parse(Bytes("[]")));

    /// <summary>The install safe name strips non-alphanumerics but keeps case — no ".png", unlike the logo.</summary>
    [Theory]
    [InlineData("Sky Factory 4", "SkyFactory4")]
    [InlineData("All the Mods: 9!", "AlltheMods9")]
    [InlineData("Vanilla", "Vanilla")]
    public void TheInstallSafeNameStripsNonAlphanumericsKeepingCase(string name, string expected)
        => Assert.Equal(expected, AtlPackIndex.InstallSafeName(name));
}
