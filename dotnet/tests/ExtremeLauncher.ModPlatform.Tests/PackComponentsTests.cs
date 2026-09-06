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
 * The uids are metadata-server identifiers, so they are asserted literally rather than through a
 * round-trip: a typo here produces "component not found" at install time with nothing pointing back
 * to the mapping that caused it.
 */

using System.Text;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class PackComponentsTests
{
    // ================================================================== Modrinth

    private static ModrinthPackManifest ModrinthManifest(string dependencies)
        => ModrinthPack.Parse(Encoding.UTF8.GetBytes($$"""
            { "formatVersion": 1, "game": "minecraft", "files": [], "dependencies": {{dependencies}} }
            """));

    [Fact]
    public void AModrinthPackMapsItsDependenciesDirectly()
    {
        var components = PackComponents.FromModrinth(
            ModrinthManifest("""{ "minecraft": "1.20.1", "fabric-loader": "0.15.0" }"""));

        Assert.Equal(
            [("net.minecraft", "1.20.1"), ("net.fabricmc.fabric-loader", "0.15.0")],
            components.Select(c => (c.Uid, c.Version)));
    }

    /// <summary>Minecraft is the one component the user chose; the loader comes with the pack.</summary>
    [Fact]
    public void OnlyMinecraftIsMarkedImportant()
    {
        var components = PackComponents.FromModrinth(
            ModrinthManifest("""{ "minecraft": "1.20.1", "forge": "47.2.0" }"""));

        Assert.True(components[0].Important);
        Assert.False(components[1].Important);
    }

    [Theory]
    [InlineData("fabric-loader", "net.fabricmc.fabric-loader")]
    [InlineData("quilt-loader", "org.quiltmc.quilt-loader")]
    [InlineData("forge", "net.minecraftforge")]
    [InlineData("neoforge", "net.neoforged")]
    public void EveryModrinthLoaderMapsToItsMetadataUid(string key, string uid)
    {
        var components = PackComponents.FromModrinth(
            ModrinthManifest($$"""{ "minecraft": "1.20.1", "{{key}}": "1.2.3" }"""));

        Assert.Equal(uid, components[1].Uid);
    }

    [Fact]
    public void AVanillaPackGetsMinecraftAlone()
        => Assert.Equal(
            "net.minecraft",
            Assert.Single(PackComponents.FromModrinth(ModrinthManifest("""{ "minecraft": "1.20.1" }"""))).Uid);

    /*
     * A pack may name several loaders and each becomes its own component, matching upstream's
     * independent if-statements. Whether the combination is installable is the component resolver's
     * problem, not the importer's.
     */
    [Fact]
    public void SeveralModrinthLoadersEachBecomeAComponent()
    {
        var components = PackComponents.FromModrinth(
            ModrinthManifest("""{ "minecraft": "1.20.1", "fabric-loader": "0.15.0", "quilt-loader": "0.23.0" }"""));

        Assert.Equal(3, components.Count);
    }

    // ================================================================== CurseForge

    private static FlamePackManifest FlameManifest(string modLoaders, string mcVersion = "1.20.1")
        => FlamePack.Parse(Encoding.UTF8.GetBytes($$"""
            { "manifestType": "minecraftModpack", "manifestVersion": 1,
              "minecraft": { "version": "{{mcVersion}}", "modLoaders": {{modLoaders}} } }
            """));

    [Fact]
    public void ACurseforgePackSplitsItsCompoundLoaderId()
    {
        var components = PackComponents.FromFlame(FlameManifest("""[{ "id": "forge-47.2.0" }]"""));

        Assert.Equal(
            [("net.minecraft", "1.20.1"), ("net.minecraftforge", "47.2.0")],
            components.Select(c => (c.Uid, c.Version)));
    }

    [Theory]
    [InlineData("forge-47.2.0", "net.minecraftforge", "47.2.0")]
    [InlineData("fabric-0.15.0", "net.fabricmc.fabric-loader", "0.15.0")]
    [InlineData("quilt-0.23.0", "org.quiltmc.quilt-loader", "0.23.0")]
    [InlineData("neoforge-20.4.190", "net.neoforged", "20.4.190")]
    public void EveryCurseforgeLoaderMapsToItsMetadataUid(string id, string uid, string version)
        => Assert.Equal(new PackComponent(uid, version), PackComponents.ResolveLoader(id));

    /// <summary>CurseForge capitalises inconsistently across packs.</summary>
    [Fact]
    public void LoaderNamesAreMatchedCaseInsensitively()
        => Assert.Equal("net.minecraftforge", PackComponents.ResolveLoader("Forge-47.2.0")!.Value.Uid);

    /*
     * For 1.20.1 ONLY, CurseForge writes "neoforge-1.20.1-47.1.0" where every other version is
     * "neoforge-20.4.190". Upstream hardcodes the string and calls it "a mess for curseforge".
     */
    [Fact]
    public void TheNeoForge1201PrefixIsStripped()
        => Assert.Equal(
            new PackComponent("net.neoforged", "47.1.0"),
            PackComponents.ResolveLoader("neoforge-1.20.1-47.1.0"));

    /// <summary>Left as a special case: a rule general enough for both would mangle real versions.</summary>
    [Theory]
    [InlineData("20.4.190", "20.4.190")]
    [InlineData("1.20.2-47.1.0", "1.20.2-47.1.0")]
    [InlineData("1.20.1-47.1.0", "47.1.0")]
    public void OnlyTheOneNeoForgePrefixIsSpecialCased(string version, string expected)
        => Assert.Equal(expected, PackComponents.StripNeoForgeMinecraftPrefix(version));

    /// <summary>A pack naming a loader this launcher does not know is still playable without it.</summary>
    [Fact]
    public void AnUnknownLoaderIsSkippedRatherThanFatal()
    {
        var components = PackComponents.FromFlame(FlameManifest("""[{ "id": "rift-1.0.4" }]"""));

        Assert.Equal("net.minecraft", Assert.Single(components).Uid);
        Assert.Null(PackComponents.ResolveLoader("rift-1.0.4"));
    }

    [Fact]
    public void ACurseforgePackWithNoLoaderGetsMinecraftAlone()
        => Assert.Single(PackComponents.FromFlame(FlameManifest("[]")));

    /*
     * "recommended" is not a version. It has to survive to the caller, which resolves it against the
     * metadata index -- filtering by Minecraft version for Forge and NeoForge only, since Fabric and
     * Quilt releases are not tied to one.
     */
    [Fact]
    public void RecommendedIsPassedThroughRatherThanResolvedHere()
        => Assert.Equal(
            new PackComponent("net.minecraftforge", PackComponents.RecommendedVersion),
            PackComponents.ResolveLoader("forge-recommended"));

    // ================================================================== the trailing-dots hack

    /*
     * Upstream calls these "mysterious trailing dots" and warns when it finds them. They come from
     * hand-edited manifests, and "1.20.1." matches no version on any metadata server -- so the import
     * fails at component resolution with nothing pointing at the cause.
     */
    [Theory]
    [InlineData("1.20.1.", "1.20.1")]
    [InlineData("1.20.1...", "1.20.1")]
    [InlineData("1.20.1", "1.20.1")]
    [InlineData("1.20", "1.20")]
    [InlineData("", "")]
    public void TrailingDotsAreRemovedFromTheMinecraftVersion(string version, string expected)
        => Assert.Equal(expected, PackComponents.NormaliseMinecraftVersion(version));

    /// <summary>Applied by both importers, not just CurseForge's.</summary>
    [Fact]
    public void BothImportersNormaliseTheMinecraftVersion()
    {
        Assert.Equal("1.20.1", PackComponents.FromFlame(FlameManifest("[]", mcVersion: "1.20.1."))[0].Version);

        Assert.Equal(
            "1.20.1",
            PackComponents.FromModrinth(ModrinthManifest("""{ "minecraft": "1.20.1." }"""))[0].Version);
    }

    /// <summary>An interior dot is not trailing — only the tail is trimmed.</summary>
    [Fact]
    public void OnlyTrailingDotsAreRemoved()
        => Assert.Equal("1.20.1-pre.1", PackComponents.NormaliseMinecraftVersion("1.20.1-pre.1."));
}
