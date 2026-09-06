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
 * THESE ROUND-TRIP THROUGH THE PARSERS rather than asserting the JSON directly, wherever the format
 * allows it. A field spelled wrong on the writing side produces a pack other launchers reject, and
 * asserting the JSON I meant to write cannot catch a name only the reader knows -- the two halves
 * would simply agree with each other. Only the fields no parser reads are asserted literally.
 */

using System.Text;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class PackExportTests
{
    private static readonly PackComponent[] FabricInstance =
    [
        new(PackComponents.MinecraftUid, "1.20.1", Important: true),
        new(PackComponents.FabricUid, "0.15.0"),
    ];

    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";

    private const string Sha512 =
        "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce"
        + "47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e";

    private static ExportFile File(
        string path,
        PackwizSide side = PackwizSide.UniversalSide,
        bool enabled = true)
        => new(path, "https://cdn.modrinth.com/a.jar", Sha1, Sha512, 1234, side, enabled);

    // ================================================================== Modrinth, by round trip

    [Fact]
    public void AnExportedMrpackIndexReadsBackAsTheSamePack()
    {
        var index = PackExport.CreateModrinthIndex(
            "My Pack", "1.0.0", "A summary", FabricInstance, [File("mods/a.jar")]);

        var parsed = ModrinthPack.Parse(Encoding.UTF8.GetBytes(index.ToJsonString()));

        Assert.Equal("My Pack", parsed.Name);
        Assert.Equal("1.0.0", parsed.VersionId);
        Assert.Equal("1.20.1", parsed.Dependencies.MinecraftVersion);
        Assert.Equal("0.15.0", parsed.Dependencies.FabricVersion);

        var file = Assert.Single(parsed.Files);

        Assert.Equal("mods/a.jar", file.Path);
        Assert.Equal(new Uri("https://cdn.modrinth.com/a.jar"), Assert.Single(file.Downloads));
        Assert.True(file.Required);
    }

    /// <summary>Every loader the format names survives a round trip, so none is misspelled.</summary>
    [Theory]
    [InlineData(PackComponents.FabricUid, "fabric-loader")]
    [InlineData(PackComponents.QuiltUid, "quilt-loader")]
    [InlineData(PackComponents.ForgeUid, "forge")]
    [InlineData(PackComponents.NeoForgeUid, "neoforge")]
    public void EveryLoaderRoundTripsThroughTheDependencyBlock(string uid, string expectedKey)
    {
        var index = PackExport.CreateModrinthIndex(
            "P", "1", string.Empty,
            [new PackComponent(PackComponents.MinecraftUid, "1.20.1"), new PackComponent(uid, "1.2.3")],
            []);

        // The parser refuses an unknown dependency name, so parsing at all proves the spelling.
        var parsed = ModrinthPack.Parse(Encoding.UTF8.GetBytes(index.ToJsonString()));

        Assert.Equal("1.2.3", PackComponents.FromModrinth(parsed).Single(c => c.Uid == uid).Version);

        Assert.Equal(expectedKey, PackExport.ModrinthDependencyKey(uid));
    }

    /// <summary>A component the format has no name for is left out rather than invented.</summary>
    [Fact]
    public void AnUnknownComponentIsNotExported()
    {
        var index = PackExport.CreateModrinthIndex(
            "P", "1", string.Empty,
            [new PackComponent(PackComponents.MinecraftUid, "1.20.1"), new PackComponent("org.lwjgl3", "3.3.1")],
            []);

        // Would throw on an unknown dependency key, so this proves lwjgl3 was dropped.
        var parsed = ModrinthPack.Parse(Encoding.UTF8.GetBytes(index.ToJsonString()));

        Assert.Equal("1.20.1", parsed.Dependencies.MinecraftVersion);
        Assert.Null(PackExport.ModrinthDependencyKey("org.lwjgl3"));
    }

    /// <summary>Optional, not absent — the pack still records that the author shipped it.</summary>
    [Fact]
    public void ADisabledModIsExportedAsOptionalWithItsSuffixRemoved()
    {
        var index = PackExport.CreateModrinthIndex(
            "P", "1", string.Empty, FabricInstance,
            [File("mods/off.jar.disabled", enabled: false)]);

        var parsed = ModrinthPack.Parse(Encoding.UTF8.GetBytes(index.ToJsonString()));

        // Optional files come back in their own list, and the name is one the launcher will recognise.
        Assert.Empty(parsed.Files);
        Assert.Equal("mods/off.jar", Assert.Single(parsed.OptionalFiles).Path);
    }

    /// <summary>With optional support off there is nowhere to say "shipped but off".</summary>
    [Fact]
    public void WithOptionalFilesOffADisabledModIsExportedAsRequired()
    {
        var index = PackExport.CreateModrinthIndex(
            "P", "1", string.Empty, FabricInstance,
            [File("mods/off.jar.disabled", enabled: false)],
            optionalFiles: false);

        var parsed = ModrinthPack.Parse(Encoding.UTF8.GetBytes(index.ToJsonString()));

        Assert.Empty(parsed.OptionalFiles);

        // Suffix and all: the entry names the file exactly as it sits on disk.
        Assert.Equal("mods/off.jar.disabled", Assert.Single(parsed.Files).Path);
    }

    /*
     * ASYMMETRIC ON PURPOSE, and upstream's comment says why: "a server side mod does not imply that
     * the mod does not work on the client". A .mrpack entry marked server-only is SKIPPED by client
     * importers, so wrongly marking one silently drops a mod the pack needs.
     */
    [Fact]
    public void AClientOnlyModIsMarkedServerUnsupportedButNotTheReverse()
    {
        var index = PackExport.CreateModrinthIndex(
            "P", "1", string.Empty, FabricInstance,
            [File("mods/client.jar", PackwizSide.ClientSide), File("mods/server.jar", PackwizSide.ServerSide)]);

        var files = index["files"]!.AsArray();

        Assert.Equal("unsupported", files[0]!["env"]!["server"]!.GetValue<string>());
        Assert.Equal("required", files[0]!["env"]!["client"]!.GetValue<string>());

        // The server-side mod stays installable on the client, deliberately.
        Assert.Equal("required", files[1]!["env"]!["client"]!.GetValue<string>());
        Assert.Equal("required", files[1]!["env"]!["server"]!.GetValue<string>());
    }

    /// <summary>Omitted rather than written empty: the field is optional and an empty one says nothing.</summary>
    [Fact]
    public void AnEmptySummaryIsOmitted()
    {
        var withSummary = PackExport.CreateModrinthIndex("P", "1", "Something", FabricInstance, []);
        var without = PackExport.CreateModrinthIndex("P", "1", string.Empty, FabricInstance, []);

        Assert.Equal("Something", withSummary["summary"]!.GetValue<string>());
        Assert.False(without.ContainsKey("summary"));
    }

    /// <summary>Both hashes and the size — none of which a CurseForge manifest can carry.</summary>
    [Fact]
    public void FilesCarryBothHashesAndTheirSize()
    {
        var index = PackExport.CreateModrinthIndex("P", "1", string.Empty, FabricInstance, [File("mods/a.jar")]);

        var file = index["files"]!.AsArray()[0]!;

        Assert.Equal(Sha1, file["hashes"]!["sha1"]!.GetValue<string>());
        Assert.Equal(Sha512, file["hashes"]!["sha512"]!.GetValue<string>());
        Assert.Equal(1234, file["fileSize"]!.GetValue<long>());
    }

    // ================================================================== CurseForge, by round trip

    [Fact]
    public void AnExportedCurseforgeManifestReadsBackAsTheSamePack()
    {
        var manifest = PackExport.CreateFlameManifest(
            "My Pack", "1.0.0", "Someone", FabricInstance,
            [new FlameExportFile(306612, 3814740)]);

        var parsed = FlamePack.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()));

        Assert.Equal("My Pack", parsed.Name);
        Assert.Equal("Someone", parsed.Author);
        Assert.Equal("1.20.1", parsed.Minecraft.Version);
        Assert.Equal("overrides", parsed.Overrides);

        var file = Assert.Single(parsed.Files).Value;

        Assert.Equal(306612, file.ProjectId);
        Assert.Equal(3814740, file.FileId);
        Assert.True(file.Required);
    }

    /// <summary>Written by the exporter and read by the importer — the two must agree.</summary>
    [Theory]
    [InlineData(PackComponents.FabricUid, "0.15.0")]
    [InlineData(PackComponents.QuiltUid, "0.23.0")]
    [InlineData(PackComponents.ForgeUid, "47.2.0")]
    [InlineData(PackComponents.NeoForgeUid, "20.4.190")]
    public void EveryLoaderSurvivesAnExportImportRoundTrip(string uid, string version)
    {
        var manifest = PackExport.CreateFlameManifest(
            "P", "1", "A",
            [new PackComponent(PackComponents.MinecraftUid, "1.20.4"), new PackComponent(uid, version)],
            []);

        var parsed = FlamePack.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()));

        Assert.Equal(new PackComponent(uid, version), PackComponents.FromFlame(parsed)[1]);
    }

    /*
     * The 1.20.1 special case, mirrored from the import side. Writing it without the embedded
     * Minecraft version produces a manifest CurseForge's own client cannot install.
     */
    [Fact]
    public void TheNeoForge1201PrefixIsWrittenAndReadBackCleanly()
    {
        var components = new PackComponent[]
        {
            new(PackComponents.MinecraftUid, "1.20.1"),
            new(PackComponents.NeoForgeUid, "47.1.0"),
        };

        Assert.Equal("neoforge-1.20.1-47.1.0", PackExport.CreateFlameLoaderId(components));

        var parsed = FlamePack.Parse(Encoding.UTF8.GetBytes(
            PackExport.CreateFlameManifest("P", "1", "A", components, []).ToJsonString()));

        // The importer strips the prefix again, so the version survives intact.
        Assert.Equal(new PackComponent(PackComponents.NeoForgeUid, "47.1.0"), PackComponents.FromFlame(parsed)[1]);
    }

    [Fact]
    public void OnAnyOtherVersionNeoForgeGetsNoPrefix()
        => Assert.Equal(
            "neoforge-20.4.190",
            PackExport.CreateFlameLoaderId([
                new PackComponent(PackComponents.MinecraftUid, "1.20.4"),
                new PackComponent(PackComponents.NeoForgeUid, "20.4.190"),
            ]));

    /*
     * ONE LOADER ONLY. The format has room for a list but every consumer reads one, so an instance
     * carrying two components has to lose one -- and a fixed priority at least makes which one
     * predictable.
     */
    [Fact]
    public void AnInstanceWithTwoLoadersExportsTheHigherPriorityOne()
        => Assert.Equal(
            "quilt-0.23.0",
            PackExport.CreateFlameLoaderId([
                new PackComponent(PackComponents.FabricUid, "0.15.0"),
                new PackComponent(PackComponents.QuiltUid, "0.23.0"),
            ]));

    [Fact]
    public void AVanillaInstanceExportsNoLoader()
    {
        Assert.Null(PackExport.CreateFlameLoaderId([new PackComponent(PackComponents.MinecraftUid, "1.20.1")]));

        var parsed = FlamePack.Parse(Encoding.UTF8.GetBytes(PackExport
            .CreateFlameManifest("P", "1", "A", [new PackComponent(PackComponents.MinecraftUid, "1.20.1")], [])
            .ToJsonString()));

        Assert.Empty(parsed.Minecraft.ModLoaders);
    }

    /// <summary>The exported loader is marked primary, which is what the importer now prefers.</summary>
    [Fact]
    public void TheExportedLoaderIsMarkedPrimary()
    {
        var parsed = FlamePack.Parse(Encoding.UTF8.GetBytes(
            PackExport.CreateFlameManifest("P", "1", "A", FabricInstance, []).ToJsonString()));

        Assert.True(Assert.Single(parsed.Minecraft.ModLoaders).Primary);
    }

    /*
     * With optional files off, everything is required -- including mods the user had disabled. The
     * format has no way to say "shipped but off", so the choice is between required or lost.
     */
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void RequiredFollowsEnabledOnlyWhenOptionalFilesAreOn(bool enabled, bool optionalFiles, bool expected)
    {
        var manifest = PackExport.CreateFlameManifest(
            "P", "1", "A", FabricInstance,
            [new FlameExportFile(1, 2, Enabled: enabled)],
            optionalFiles);

        var parsed = FlamePack.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString()));

        Assert.Equal(expected, Assert.Single(parsed.Files).Value.Required);
    }

    // ================================================================== the mod list

    [Fact]
    public void TheModListLinksEveryModToItsProjectPage()
    {
        var html = PackExport.CreateFlameModList([new FlameExportFile(306612, 1, Name: "Fabric API", Authors: "modmuss50")]);

        Assert.Equal(
            "<ul><li><a href=\"https://www.curseforge.com/projects/306612\">Fabric API (by modmuss50)</a></li>\n</ul>",
            html);
    }

    /// <summary>Resource packs and shaders are in the pack but not in its mod list.</summary>
    [Fact]
    public void OnlyModsAppearInTheModList()
        => Assert.Equal(
            "<ul></ul>",
            PackExport.CreateFlameModList([new FlameExportFile(1, 2, Name: "Some Pack", IsMod: false)]));

    [Fact]
    public void AModWithNoAuthorGetsNoParentheses()
        => Assert.Contains(
            ">Lone Mod</a>",
            PackExport.CreateFlameModList([new FlameExportFile(1, 2, Name: "Lone Mod")]),
            StringComparison.Ordinal);

    /// <summary>A mod called "&lt;script&gt;" is unlikely; one with an ampersand is not.</summary>
    [Fact]
    public void NamesAndAuthorsAreHtmlEscaped()
    {
        var html = PackExport.CreateFlameModList([new FlameExportFile(1, 2, Name: "Tools & Bits", Authors: "<b>me</b>")]);

        Assert.Contains("Tools &amp; Bits", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
    }
}
