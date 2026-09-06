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
 * Characterization tests for OneSixVersionFormat. There is no upstream Qt test for this file.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using Xunit;

using Json = ExtremeLauncher.Core.Json;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class OneSixVersionFormatTests
{
    private static JsonObject Parse(string json) => Json.RequireObject(Json.RequireDocument(json));

    private static VersionFile Read(string json, bool requireOrder = false)
        => OneSixVersionFormat.VersionFileFromJson(Parse(json), "test.json", requireOrder);

    [Fact]
    public void ReadsIdentityAndOrder()
    {
        var patch = Read("""
            { "name": "Fabric Loader", "uid": "net.fabricmc.fabric-loader", "version": "0.14.21", "order": 10 }
            """, requireOrder: true);

        Assert.Equal("Fabric Loader", patch.Name);
        Assert.Equal("net.fabricmc.fabric-loader", patch.Uid);
        Assert.Equal("0.14.21", patch.Version);
        Assert.Equal(10, patch.Order);
    }

    [Fact]
    public void FileIdIsAcceptedAsTheOldSpellingOfUid()
        => Assert.Equal("net.minecraft", Read("""{ "fileId": "net.minecraft" }""").Uid);

    [Fact]
    public void AnIllegalUidIsFlaggedAsASecurityProblem()
    {
        var patch = Read("""{ "uid": "../../etc/passwd" }""");

        // The uid becomes a directory name on disk, so path characters in it are a real concern.
        Assert.Equal(ProblemSeverity.Error, patch.GetProblemSeverity());
        Assert.Contains(patch.GetProblems(), p => p.Description.Contains("illegal characters", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("net.minecraft")]
    [InlineData("org.multimc.jarmods")]
    [InlineData("simple")]
    [InlineData("with-dash_and_underscore")]
    public void LegalUidsPassValidation(string uid)
        => Assert.Equal(ProblemSeverity.None, Read($$"""{ "uid": "{{uid}}" }""").GetProblemSeverity());

    [Fact]
    public void ReadsTheOneSixExtensions()
    {
        var patch = Read("""
            {
              "uid": "test",
              "+tweakers": [ "net.minecraftforge.fml.common.launcher.FMLTweaker" ],
              "+traits": [ "legacyLaunch", "no-texturepacks" ],
              "+jvmArgs": [ "-Dfml.ignoreInvalidMinecraftCertificates=true" ]
            }
            """);

        Assert.Single(patch.AddTweakers);
        Assert.Equal(2, patch.Traits.Count);
        Assert.Contains("legacyLaunch", patch.Traits);
        Assert.Single(patch.AddnJvmArguments);
    }

    [Fact]
    public void ReadsMmcPrefixedLibraryHints()
    {
        var patch = Read("""
            {
              "uid": "test",
              "libraries": [
                {
                  "name": "a:b:1",
                  "MMC-hint": "local",
                  "MMC-filename": "custom.jar",
                  "MMC-displayname": "Pretty Name",
                  "MMC-absoluteUrl": "https://cdn.invalid/x.jar"
                }
              ]
            }
            """);

        var library = Assert.Single(patch.Libraries);

        Assert.Equal("local", library.Hint);
        Assert.Equal("custom.jar", library.Filename);
        Assert.Equal("Pretty Name", library.DisplayNameOverride);
        Assert.Equal("https://cdn.invalid/x.jar", library.AbsoluteUrl);
    }

    [Fact]
    public void TheMisspelledAbsoluteUrlKeyIsStillHonoured()
    {
        // "MMC-absulute_url" shipped as a typo; files containing it still exist in the wild.
        var patch = Read("""
            { "uid": "test", "libraries": [ { "name": "a:b:1", "MMC-absulute_url": "https://cdn.invalid/typo.jar" } ] }
            """);

        Assert.Equal("https://cdn.invalid/typo.jar", patch.Libraries[0].AbsoluteUrl);
    }

    [Fact]
    public void BothLibraryKeysTogetherAreAWarningButStillRead()
    {
        var patch = Read("""
            {
              "uid": "test",
              "libraries": [ { "name": "a:b:1" } ],
              "+libraries": [ { "name": "c:d:2" } ]
            }
            """);

        Assert.Equal(ProblemSeverity.Warning, patch.GetProblemSeverity());

        // Both are still loaded rather than one being dropped.
        Assert.Equal(2, patch.Libraries.Count);
    }

    [Fact]
    public void ReadsAgentsWithTheirArguments()
    {
        var patch = Read("""
            { "uid": "test", "+agents": [ { "name": "a:agent:1", "argument": "mode=debug" } ] }
            """);

        var agent = Assert.Single(patch.Agents);

        Assert.Equal("a:agent:1", agent.Library.Name.Serialize());
        Assert.Equal("mode=debug", agent.Argument);
    }

    [Fact]
    public void LegacyPlusJarModsGetASyntheticCoordinate()
    {
        var patch = Read("""
            { "uid": "test", "name": "Some Mod (jar mod)", "+jarMods": [ { "name": "coolmod.jar" } ] }
            """);

        var jarMod = Assert.Single(patch.JarMods);

        // A unique coordinate is invented; the real filename becomes the override.
        Assert.StartsWith("org.multimc.jarmods:", jarMod.Name.Serialize(), StringComparison.Ordinal);
        Assert.Equal("coolmod.jar", jarMod.Filename);
        Assert.Equal("local", jarMod.Hint);

        // " (jar mod)" is stripped from the patch name when no originalName is given.
        Assert.Equal("Some Mod", jarMod.DisplayNameOverride);
    }

    [Fact]
    public void LegacyPlusJarModsPreferOriginalName()
    {
        var patch = Read("""
            { "uid": "test", "name": "X", "+jarMods": [ { "name": "renamed.jar", "originalName": "Real Name" } ] }
            """);

        Assert.Equal("Real Name", patch.JarMods[0].DisplayNameOverride);
    }

    [Fact]
    public void MainJarIsReconstructedFromTheClientDownload()
    {
        var patch = Read("""
            {
              "uid": "net.minecraft",
              "id": "1.20.1",
              "downloads": {
                "client": { "sha1": "abc", "size": 1, "url": "https://piston.invalid/client.jar" }
              }
            }
            """);

        Assert.NotNull(patch.MainJar);
        Assert.Equal("com.mojang:minecraft:1.20.1:client", patch.MainJar.Name.Serialize());
        Assert.Equal("https://piston.invalid/client.jar", patch.MainJar.MojangDownloads?.Artifact?.Url);
    }

    [Fact]
    public void AMissingClientDownloadIsAnError()
    {
        var patch = Read("""{ "uid": "net.minecraft", "id": "1.20.1" }""");

        Assert.NotNull(patch.MainJar);
        Assert.Equal(ProblemSeverity.Error, patch.GetProblemSeverity());
        Assert.Contains(patch.GetProblems(), p => p.Description.Contains("main jar", StringComparison.Ordinal));
    }

    [Fact]
    public void McVersionBecomesAMinecraftRequirement()
    {
        var patch = Read("""{ "uid": "net.minecraftforge", "mcVersion": "1.12.2" }""");

        var requirement = Assert.Single(patch.Requires);

        Assert.Equal("net.minecraft", requirement.Uid);
        Assert.Equal("1.12.2", requirement.EqualsVersion);
    }

    [Fact]
    public void AnExplicitRequiresWinsOverMcVersion()
    {
        var patch = Read("""
            {
              "uid": "net.minecraftforge",
              "mcVersion": "1.12.2",
              "requires": [ { "uid": "net.minecraft", "equals": "1.20.1" } ]
            }
            """);

        // Require is keyed by uid, so the explicit entry is already present and mcVersion is a no-op.
        Assert.Single(patch.Requires);
        Assert.Equal("1.20.1", patch.Requires.First().EqualsVersion);
    }

    [Theory]
    [InlineData("tweakers")]
    [InlineData("-libraries")]
    [InlineData("-tweakers")]
    [InlineData("-minecraftArguments")]
    [InlineData("+minecraftArguments")]
    public void RemovedFormatElementsAreReportedRatherThanIgnored(string element)
    {
        var patch = Read($$"""{ "uid": "test", "{{element}}": [] }""");

        Assert.Equal(ProblemSeverity.Error, patch.GetProblemSeverity());
        Assert.Contains(patch.GetProblems(), p => p.Description.Contains(element, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownFormatVersionIsRejected()
        => Assert.Throws<JsonException>(() => Read("""{ "uid": "test", "formatVersion": 99 }"""));

    // ================================================================== writing

    [Fact]
    public void RoundTripsThroughSerialization()
    {
        const string json = """
            {
              "formatVersion": 0,
              "name": "Fabric Loader",
              "uid": "net.fabricmc.fabric-loader",
              "version": "0.14.21",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "+tweakers": [ "SomeTweaker" ],
              "+traits": [ "legacyLaunch" ],
              "+jvmArgs": [ "-Xss1M" ],
              "libraries": [ { "name": "a:b:1", "MMC-hint": "local" } ],
              "requires": [ { "uid": "net.minecraft", "equals": "1.20.1" } ],
              "volatile": true
            }
            """;

        var written = OneSixVersionFormat.VersionFileToJson(Read(json));
        var reparsed = OneSixVersionFormat.VersionFileFromJson(written, "written.json");

        Assert.Equal("Fabric Loader", reparsed.Name);
        Assert.Equal("net.fabricmc.fabric-loader", reparsed.Uid);
        Assert.Equal("0.14.21", reparsed.Version);
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", reparsed.MainClass);
        Assert.Single(reparsed.AddTweakers);
        Assert.Contains("legacyLaunch", reparsed.Traits);
        Assert.Single(reparsed.AddnJvmArguments);
        Assert.Equal("local", reparsed.Libraries[0].Hint);
        Assert.Single(reparsed.Requires);
        Assert.True(reparsed.IsVolatile);
    }

    /// <remarks>
    /// UPSTREAM BUG, fixed here. versionFileToJson()'s "mods" block guards on patch->mods but iterates
    /// patch->jarMods, so a patch carrying both writes the jar mods out twice and loses the mods.
    /// Reproducing that faithfully would corrupt user patch files on every save.
    /// </remarks>
    [Fact]
    public void ModsAndJarModsAreSerializedFromTheirOwnLists()
    {
        var patch = Read("""
            {
              "uid": "test",
              "jarMods": [ { "name": "jar:mod:1" } ],
              "mods": [ { "name": "plain:mod:2" } ]
            }
            """);

        Assert.Single(patch.JarMods);
        Assert.Single(patch.Mods);

        var written = OneSixVersionFormat.VersionFileToJson(patch);

        Assert.Equal("jar:mod:1", Json.RequireArray(written, "jarMods")[0]!["name"]!.GetValue<string>());
        Assert.Equal("plain:mod:2", Json.RequireArray(written, "mods")[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void EmptyCollectionsAreOmitted()
    {
        var written = OneSixVersionFormat.VersionFileToJson(Read("""{ "uid": "test" }"""));

        Assert.False(written.ContainsKey("libraries"));
        Assert.False(written.ContainsKey("jarMods"));
        Assert.False(written.ContainsKey("mods"));
        Assert.False(written.ContainsKey("+agents"));
        Assert.False(written.ContainsKey("requires"));
        Assert.False(written.ContainsKey("volatile"));
    }

    [Fact]
    public void TheFormatVersionIsAlwaysStamped()
        => Assert.Equal(0, OneSixVersionFormat.VersionFileToJson(Read("""{ "uid": "t" }"""))["formatVersion"]!.GetValue<int>());
}
