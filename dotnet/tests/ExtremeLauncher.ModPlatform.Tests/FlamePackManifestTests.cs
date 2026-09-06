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
 * The interesting cases here are the ones where CurseForge's format differs from Modrinth's: ids
 * instead of URLs, "required" spelled the positive way, a compound loader id, and an overrides folder
 * name the pack chooses.
 */

using System.Text;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class FlamePackManifestTests
{
    private static FlamePackManifest Parse(string json) => FlamePack.Parse(Encoding.UTF8.GetBytes(json));

    private static string Manifest(
        string files = "[]",
        string modLoaders = """[{ "id": "forge-47.2.0", "primary": true }]""",
        string extra = "")
        => $$"""
        {
          "manifestType": "minecraftModpack",
          "manifestVersion": 1,
          "minecraft": { "version": "1.20.1", "modLoaders": {{modLoaders}} },
          "name": "Test Pack",
          "version": "1.0.0",
          "author": "Someone",
          "files": {{files}}
          {{extra}}
        }
        """;

    // ================================================================== the format's own gates

    [Fact]
    public void AWellFormedManifestIsRead()
    {
        var manifest = Parse(Manifest("""[{ "projectID": 306612, "fileID": 3814740, "required": true }]"""));

        Assert.Equal("Test Pack", manifest.Name);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("Someone", manifest.Author);
        Assert.Equal("1.20.1", manifest.Minecraft.Version);

        var file = Assert.Single(manifest.Files).Value;

        Assert.Equal(306612, file.ProjectId);
        Assert.Equal(3814740, file.FileId);
        Assert.True(file.Required);

        // Mods until resolution says otherwise.
        Assert.Equal("mods", file.TargetFolder);
    }

    /*
     * CurseForge uses manifest.json for more than modpacks, so the type is checked before anything
     * else is read -- the remaining fields would parse happily against the wrong document and produce
     * a nonsense pack.
     */
    [Fact]
    public void AManifestThatIsNotAModpackIsRefused()
        => Assert.ThrowsAny<LauncherException>(() => Parse("""
            { "manifestType": "minecraftWorld", "manifestVersion": 1,
              "minecraft": { "version": "1.20.1" } }
            """));

    [Fact]
    public void AnUnknownManifestVersionIsRefused()
        => Assert.ThrowsAny<LauncherException>(() => Parse("""
            { "manifestType": "minecraftModpack", "manifestVersion": 2,
              "minecraft": { "version": "1.20.1" } }
            """));

    /// <summary>Defaults that keep an under-specified pack installable.</summary>
    [Fact]
    public void AnUnderSpecifiedPackGetsUpstreamsDefaults()
    {
        var manifest = Parse("""
            { "manifestType": "minecraftModpack", "manifestVersion": 1,
              "minecraft": { "version": "1.20.1" } }
            """);

        Assert.Equal("Unnamed", manifest.Name);
        Assert.Equal("Anonymous", manifest.Author);
        Assert.Equal(string.Empty, manifest.Version);
        Assert.Equal("overrides", manifest.Overrides);
        Assert.Empty(manifest.Files);
        Assert.Empty(manifest.Minecraft.ModLoaders);
    }

    // ================================================================== files

    /*
     * THE HEADLINE DIFFERENCE FROM A .mrpack. There is no URL and no hash here -- only ids -- so
     * nothing can be downloaded until every entry has been resolved through the API.
     */
    [Fact]
    public void FilesCarryIdsAndNothingElse()
    {
        var file = Assert.Single(Parse(Manifest("""[{ "projectID": 1, "fileID": 2 }]""")).Files).Value;

        Assert.Equal(1, file.ProjectId);
        Assert.Equal(2, file.FileId);

        // Nothing on FlamePackFile can name a download or verify one.
        Assert.DoesNotContain(
            typeof(FlamePackFile).GetProperties(),
            p => p.Name.Contains("Url", StringComparison.Ordinal)
                 || p.Name.Contains("Hash", StringComparison.Ordinal));
    }

    /// <summary>Spelled the positive way round, unlike Modrinth's env block, and absent means required.</summary>
    [Theory]
    [InlineData("""{ "projectID": 1, "fileID": 2 }""", true)]
    [InlineData("""{ "projectID": 1, "fileID": 2, "required": true }""", true)]
    [InlineData("""{ "projectID": 1, "fileID": 2, "required": false }""", false)]
    public void RequiredDefaultsToTrue(string file, bool expected)
        => Assert.Equal(expected, Assert.Single(Parse(Manifest($"[{file}]")).Files).Value.Required);

    /// <summary>Keyed by file id, so a manifest naming one twice collapses to a single entry.</summary>
    [Fact]
    public void ARepeatedFileIdCollapsesToOneEntry()
    {
        var manifest = Parse(Manifest("""
            [{ "projectID": 1, "fileID": 2 }, { "projectID": 1, "fileID": 2, "required": false }]
            """));

        // Harmless: the second entry names the identical download.
        Assert.Equal(2, Assert.Single(manifest.Files).Key);
    }

    [Fact]
    public void FilesAreKeyedByFileIdSoResolutionCanBeMatchedUp()
    {
        var manifest = Parse(Manifest("""
            [{ "projectID": 10, "fileID": 100 }, { "projectID": 20, "fileID": 200 }]
            """));

        Assert.Equal([100, 200], manifest.Files.Keys.Order());
        Assert.Equal(10, manifest.Files[100].ProjectId);
    }

    // ================================================================== loaders

    [Fact]
    public void ThePrimaryLoaderIsPreferred()
    {
        var manifest = Parse(Manifest(modLoaders: """
            [{ "id": "forge-47.2.0" }, { "id": "fabric-0.15.0", "primary": true }]
            """));

        Assert.Equal("fabric-0.15.0", FlamePack.GetPrimaryModloader(manifest)!.Id);
    }

    /*
     * With none marked, the LAST recognised loader is taken -- upstream's answer, reached by looping
     * every entry and overwriting its working variables without ever breaking. Kept for exactly that
     * reason: preferring the primary flag (above) only changes behaviour for manifests that state a
     * preference upstream was ignoring.
     */
    [Fact]
    public void WithNoPrimaryTheLastLoaderIsTakenAsUpstreamDoes()
    {
        var manifest = Parse(Manifest(modLoaders: """[{ "id": "forge-47.2.0" }, { "id": "fabric-0.15.0" }]"""));

        Assert.Equal("fabric-0.15.0", FlamePack.GetPrimaryModloader(manifest)!.Id);
    }

    [Fact]
    public void APackWithNoLoaderHasNone()
        => Assert.Null(FlamePack.GetPrimaryModloader(Parse(Manifest(modLoaders: "[]"))));

    /*
     * The FIRST hyphen separates loader from version, not the last. Loader names have no hyphen and
     * versions routinely do; splitting from the wrong end produces a loader named "fabric-0.15.0"
     * that no metadata server has heard of.
     */
    [Theory]
    [InlineData("forge-47.2.0", "forge", "47.2.0")]
    [InlineData("fabric-0.15.0-build.1", "fabric", "0.15.0-build.1")]
    [InlineData("neoforge-20.4.190", "neoforge", "20.4.190")]
    [InlineData("forge", "forge", "")]
    [InlineData("", "", "")]
    public void ALoaderIdSplitsAtTheFirstHyphen(string id, string loader, string version)
        => Assert.Equal((loader, version), FlamePack.SplitModloaderId(id));

    /// <summary>Carried, never interpreted — upstream says only CurseForge's own client knows it.</summary>
    [Fact]
    public void TheLibrariesFieldIsReadButNotUnderstood()
    {
        var manifest = Parse("""
            { "manifestType": "minecraftModpack", "manifestVersion": 1,
              "minecraft": { "version": "1.2.5", "libraries": "some-ftb-retro-thing" } }
            """);

        Assert.Equal("some-ftb-retro-thing", manifest.Minecraft.Libraries);
    }

    // ================================================================== overrides

    [Fact]
    public void ThePackCanNameItsOwnOverridesFolder()
        => Assert.Equal(
            "my-overrides",
            Parse(Manifest(extra: """, "overrides": "my-overrides" """)).Overrides);

    /*
     * SECURITY, AND NOT PRESENT UPSTREAM. The overrides name comes out of an untrusted manifest and
     * is used as a path inside the archive, so it gets the same containment check the .mrpack file
     * paths get.
     */
    [Theory]
    [InlineData("../../../etc")]
    [InlineData("..")]
    [InlineData("/etc")]
    [InlineData("overrides/../..")]
    public void AnOverridesFolderThatEscapesIsRefused(string overrides)
        => Assert.ThrowsAny<LauncherException>(
            () => Parse(Manifest(extra: $""", "overrides": "{overrides}" """)));

    /// <summary>Legitimate nesting is still fine — the guard is about escaping, not depth.</summary>
    [Fact]
    public void ANestedOverridesFolderIsAccepted()
        => Assert.Equal(
            "client/overrides",
            Parse(Manifest(extra: """, "overrides": "client/overrides" """)).Overrides);
}
