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
 * Characterization tests for mod metadata. Upstream has no Qt test for LocalModParseTask.
 *
 * Six formats, each with its own quirks, and the quirks are the point: every one of them exists
 * because some mod in the wild does it that way, and dropping one means that mod shows up in the
 * launcher as a nameless file.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Minecraft.Mods;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class ModParseTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-mod-" + Guid.NewGuid().ToString("N"));

    public ModParseTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Builds a jar holding the given entries.</summary>
    private string MakeJar(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_temp, name);

        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }

    // ================================================================== mcmod.info (Forge, pre-1.13)

    [Fact]
    public void TheOldestMcModInfoIsABareArray()
    {
        // Files from 2013 have no wrapper at all.
        var details = ModUtils.ReadMcModInfo(Bytes("""
            [ { "modid": "examplemod", "name": "Cool Mod", "version": "1.2.3", "description": "Does things" } ]
            """));

        Assert.Equal("examplemod", details.ModId);
        Assert.Equal("Cool Mod", details.Name);
        Assert.Equal("1.2.3", details.Version);
        Assert.Equal("Does things", details.Description);
    }

    [Theory]
    [InlineData("modlist")]
    [InlineData("modList")]
    public void BothSpellingsOfTheWrapperAreAccepted(string key)
    {
        var details = ModUtils.ReadMcModInfo(Bytes($$"""
            { "modListVersion": 2, "{{key}}": [ { "modid": "m", "name": "M", "version": "1" } ] }
            """));

        // The key was renamed at some point and both forms are still in the wild.
        Assert.Equal("m", details.ModId);
    }

    [Fact]
    public void TheExampleModsNameIsIgnored()
    {
        var details = ModUtils.ReadMcModInfo(Bytes("""
            [ { "modid": "coolmod", "name": "Example Mod", "version": "1.0" } ]
            """));

        // A great many mods ship the template unchanged; a folder full of "Example Mod" helps nobody,
        // and the filename is more informative.
        Assert.Equal(string.Empty, details.Name);
        Assert.Equal("coolmod", details.ModId);
    }

    [Theory]
    [InlineData("authorList")]
    [InlineData("authors")]
    public void EitherAuthorKeyIsRead(string key)
    {
        var details = ModUtils.ReadMcModInfo(Bytes($$"""
            [ { "modid": "m", "{{key}}": [ "Alice", "Bob" ] } ]
            """));

        Assert.Equal(["Alice", "Bob"], details.Authors);
    }

    [Fact]
    public void ABareHostGetsAScheme()
    {
        var details = ModUtils.ReadMcModInfo(Bytes("""
            [ { "modid": "m", "url": "example.com/mod" } ]
            """));

        // Plain http, not https: these URLs come from mods written a decade ago, and upgrading one
        // that has no TLS would break the link rather than fix it.
        Assert.Equal("http://example.com/mod", details.HomeUrl);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com")]
    public void AUrlThatAlreadyHasASchemeIsLeftAlone(string url)
    {
        var details = ModUtils.ReadMcModInfo(Bytes($$"""[ { "modid": "m", "url": "{{url}}" } ]"""));

        Assert.Equal(url, details.HomeUrl);
    }

    [Fact]
    public void GarbageIsNotAMod()
    {
        Assert.Equal(string.Empty, ModUtils.ReadMcModInfo(Bytes("{ not json")).ModId);
        Assert.Equal(string.Empty, ModUtils.ReadMcModInfo(Bytes("[]")).ModId);
        Assert.Equal(string.Empty, ModUtils.ReadMcModInfo([]).ModId);
    }

    // ================================================================== mods.toml (Forge 1.13+)

    private const string ModsToml = """
        modLoader="javafml"
        loaderVersion="[40,)"
        license="MIT"
        issueTrackerURL="https://example.com/issues"

        [[mods]]
        modId="examplemod"
        version="1.0.0"
        displayName="Example Mod"
        authors="Alice"
        description='''
        A mod that does things.
        '''
        """;

    [Fact]
    public void ForgeTomlIsRead()
    {
        var details = ModUtils.ReadMcModToml(Bytes(ModsToml));

        Assert.Equal("examplemod", details.ModId);
        Assert.Equal("1.0.0", details.Version);
        Assert.Equal("Example Mod", details.Name);
        Assert.Contains("does things", details.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TopLevelFieldsAreFoundOutsideTheModsArray()
    {
        var details = ModUtils.ReadMcModToml(Bytes(ModsToml));

        // license and issueTrackerURL sit at the file's top level in the current template, while
        // authors sits inside [[mods]]. Both placements have shipped, so both are checked.
        Assert.Equal("https://example.com/issues", details.IssueTracker);
        Assert.Equal("MIT", Assert.Single(details.Licenses).Name);
        Assert.Equal(["Alice"], details.Authors);
    }

    [Fact]
    public void FieldsInsideTheModsArrayAlsoWork()
    {
        var details = ModUtils.ReadMcModToml(Bytes("""
            [[mods]]
            modId="m"
            displayURL="example.com"
            issueTrackerURL="https://example.com/bugs"
            """));

        Assert.Equal("http://example.com", details.HomeUrl);
        Assert.Equal("https://example.com/bugs", details.IssueTracker);
    }

    [Fact]
    public void ATomlWithoutAModsArrayYieldsNothing()
    {
        Assert.Equal(string.Empty, ModUtils.ReadMcModToml(Bytes("modLoader=\"javafml\"")).ModId);
        Assert.Equal(string.Empty, ModUtils.ReadMcModToml(Bytes("this is not toml [[[")).ModId);
    }

    // ================================================================== fabric.mod.json

    [Fact]
    public void FabricMetadataIsRead()
    {
        var details = ModUtils.ReadFabricModInfo(Bytes("""
            {
                "schemaVersion": 1,
                "id": "examplemod",
                "version": "1.0.0",
                "name": "Example Mod",
                "description": "Does things",
                "authors": [ "Alice", { "name": "Bob" } ],
                "contact": { "homepage": "https://example.com", "issues": "https://example.com/issues" },
                "license": "MIT",
                "icon": "assets/examplemod/icon.png"
            }
            """));

        Assert.Equal("examplemod", details.ModId);
        Assert.Equal("Example Mod", details.Name);

        // An author is a bare string OR an object with a name; both forms are documented.
        Assert.Equal(["Alice", "Bob"], details.Authors);

        Assert.Equal("https://example.com", details.HomeUrl);
        Assert.Equal("https://example.com/issues", details.IssueTracker);
        Assert.Equal("MIT", Assert.Single(details.Licenses).Name);
        Assert.Equal("assets/examplemod/icon.png", details.IconFile);
    }

    [Fact]
    public void FabricFallsBackToTheIdForItsName()
    {
        var details = ModUtils.ReadFabricModInfo(Bytes("""{ "schemaVersion": 1, "id": "examplemod", "version": "1" }"""));

        // The id, not the filename: a Fabric mod always has one.
        Assert.Equal("examplemod", details.Name);
    }

    [Fact]
    public void SchemaVersionZeroStopsAtTheBasics()
    {
        var details = ModUtils.ReadFabricModInfo(Bytes("""
            { "id": "m", "version": "1", "authors": [ "Alice" ], "license": "MIT" }
            """));

        // Version 0 predates those fields entirely, so reading them would be inventing data.
        Assert.Equal("m", details.ModId);
        Assert.Empty(details.Authors);
        Assert.Empty(details.Licenses);
    }

    [Fact]
    public void TheLargestIconWins()
    {
        var details = ModUtils.ReadFabricModInfo(Bytes("""
            {
                "schemaVersion": 1, "id": "m", "version": "1",
                "icon": { "16x16": "small.png", "128x128": "big.png", "32x32": "medium.png" }
            }
            """));

        // So the launcher has something to downscale rather than something to stretch.
        Assert.Equal("big.png", details.IconFile);
    }

    [Fact]
    public void AnUnparseableIconKeyStillYieldsAPath()
    {
        var details = ModUtils.ReadFabricModInfo(Bytes("""
            { "schemaVersion": 1, "id": "m", "version": "1", "icon": { "default": "icon.png" } }
            """));

        // An odd key is still a valid path to an icon.
        Assert.Equal("icon.png", details.IconFile);
    }

    [Fact]
    public void ALicenceMayBeAnObjectOrAList()
    {
        var single = ModUtils.ReadFabricModInfo(Bytes("""
            {
                "schemaVersion": 1, "id": "m", "version": "1",
                "license": { "name": "Custom", "id": "custom-1", "url": "https://example.com/licence" }
            }
            """));

        var licence = Assert.Single(single.Licenses);

        Assert.Equal("Custom", licence.Name);
        Assert.Equal("custom-1", licence.Id);

        var several = ModUtils.ReadFabricModInfo(Bytes("""
            { "schemaVersion": 1, "id": "m", "version": "1", "license": [ "MIT", "Apache-2.0" ] }
            """));

        Assert.Equal(2, several.Licenses.Count);
    }

    // ================================================================== quilt.mod.json

    [Fact]
    public void QuiltMetadataIsRead()
    {
        var details = ModUtils.ReadQuiltModInfo(Bytes("""
            {
                "schema_version": 1,
                "quilt_loader": {
                    "id": "examplemod",
                    "version": "1.0.0",
                    "metadata": {
                        "name": "Example Mod",
                        "description": "Does things",
                        "contributors": { "Alice": "Owner", "Bob": "Contributor" },
                        "contact": { "homepage": "https://example.com", "issues": "https://example.com/issues" },
                        "license": "MIT",
                        "icon": "icon.png"
                    }
                }
            }
            """));

        Assert.Equal("examplemod", details.ModId);
        Assert.Equal("Example Mod", details.Name);

        // Contributors are keyed by name with their role as the value; the role is not shown anywhere.
        Assert.Equal(["Alice", "Bob"], details.Authors);

        Assert.Equal("https://example.com/issues", details.IssueTracker);
        Assert.Equal("icon.png", details.IconFile);
    }

    [Fact]
    public void AnUnknownQuiltSchemaIsLeftUnread()
    {
        // Only schema 1 is specified; a future version is not guessed at.
        var details = ModUtils.ReadQuiltModInfo(Bytes("""
            { "schema_version": 2, "quilt_loader": { "id": "m", "version": "1" } }
            """));

        Assert.Equal(string.Empty, details.ModId);
    }

    [Fact]
    public void QuiltFallsBackToTheIdForItsName()
    {
        var details = ModUtils.ReadQuiltModInfo(Bytes("""
            { "schema_version": 1, "quilt_loader": { "id": "examplemod", "version": "1" } }
            """));

        Assert.Equal("examplemod", details.Name);
    }

    // ================================================================== litemod.json

    [Fact]
    public void LiteLoaderMetadataIsRead()
    {
        var details = ModUtils.ReadLiteModInfo(Bytes("""
            {
                "name": "VoxelMap", "version": "1.9.11", "mcversion": "1.12.2",
                "author": "MamiyaOtaru", "description": "A minimap", "url": "http://example.com"
            }
            """));

        // The only format that records a Minecraft version, and the only one where the id and the name
        // are the same field — LiteLoader never had a separate identifier.
        Assert.Equal("VoxelMap", details.ModId);
        Assert.Equal("VoxelMap", details.Name);
        Assert.Equal("1.12.2", details.McVersion);
        Assert.Equal(["MamiyaOtaru"], details.Authors);
    }

    [Fact]
    public void RevisionIsTheOlderSpellingOfVersion()
    {
        var details = ModUtils.ReadLiteModInfo(Bytes("""{ "name": "M", "revision": "42" }"""));

        Assert.Equal("42", details.Version);
    }

    // ================================================================== forgeversion.properties

    [Fact]
    public void ForgesOwnVersionIsAssembledFromFourKeys()
    {
        var details = ModUtils.ReadForgeInfo(Bytes("""
            forge.major.number=14
            forge.minor.number=23
            forge.revision.number=5
            forge.build.number=2854
            """));

        Assert.Equal("14.23.5.2854", details.Version);
        Assert.Equal("Minecraft Forge", details.Name);
        Assert.Equal("Forge", details.ModId);
    }

    [Fact]
    public void ForgeIsStillNamedWhenItsVersionCannotBeRead()
    {
        var details = ModUtils.ReadForgeInfo(Bytes(string.Empty));

        // The identity is hardcoded because the file carries none, so Forge shows up in the mod list
        // even when the numbers are missing.
        Assert.Equal("Minecraft Forge", details.Name);
        Assert.Equal("0.0.0.0", details.Version);
    }

    // ================================================================== dispatch

    [Fact]
    public void AJarIsCheckedAgainstEveryFormatInOrder()
    {
        // A Fabric mod with a Forge shim carries both, and the first found wins — which decides which
        // loader's view of a multi-loader mod the launcher shows.
        var jar = MakeJar(
            "multi.jar",
            ("fabric.mod.json", """{ "schemaVersion": 1, "id": "fabric-side", "version": "1" }"""),
            ("META-INF/mods.toml", "[[mods]]\nmodId=\"forge-side\"\nversion=\"1\""));

        var mod = new Mod(jar);

        Assert.True(ModUtils.ProcessZip(mod));
        Assert.Equal("forge-side", mod.Details.ModId);
    }

    [Fact]
    public void ANeoForgeJarIsRecognised()
    {
        var jar = MakeJar("neo.jar", ("META-INF/neoforge.mods.toml", "[[mods]]\nmodId=\"neomod\"\nversion=\"2\""));
        var mod = new Mod(jar);

        Assert.True(ModUtils.Process(mod));
        Assert.Equal("neomod", mod.Details.ModId);
    }

    [Fact]
    public void AJarWithNoMetadataIsNotAMod()
    {
        var jar = MakeJar("plain.jar", ("com/example/Thing.class", "not metadata"));
        var mod = new Mod(jar);

        Assert.False(ModUtils.ProcessZip(mod));
        Assert.False(mod.Valid);
    }

    [Fact]
    public void ALitemodIsReadFromItsOwnManifest()
    {
        var path = Path.Combine(_temp, "voxelmap.litemod");

        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("litemod.json").Open());
            writer.Write("""{ "name": "VoxelMap", "version": "1.9.11" }""");
        }

        var mod = new Mod(path);

        // The extension is what routes it: a .litemod is a zip, but only litemod.json is looked for.
        Assert.Equal(ResourceType.LiteMod, mod.Type);
        Assert.True(ModUtils.Process(mod));
        Assert.Equal("VoxelMap", mod.Details.Name);
    }

    [Fact]
    public void AnUnpackedModFolderIsReadFromMcModInfo()
    {
        var folder = Path.Combine(_temp, "unpacked");
        Directory.CreateDirectory(folder);

        File.WriteAllText(
            Path.Combine(folder, "mcmod.info"),
            """[ { "modid": "dev", "name": "In Development", "version": "0.1" } ]""");

        var mod = new Mod(folder);

        Assert.True(ModUtils.Process(mod));
        Assert.Equal("In Development", mod.Details.Name);
    }

    [Fact]
    public void ADisabledJarIsStillParsed()
    {
        var jar = MakeJar("mod.jar", ("fabric.mod.json", """{ "schemaVersion": 1, "id": "m", "version": "1" }"""));
        var disabled = jar + ".disabled";

        File.Move(jar, disabled);

        var mod = new Mod(disabled);

        // Disabling is a rename, so the type survives it — and the user still wants to see what the
        // mod is while deciding whether to turn it back on.
        Assert.Equal(ResourceType.ZipFile, mod.Type);
        Assert.False(mod.Enabled);
        Assert.True(ModUtils.Process(mod));
        Assert.Equal("m", mod.Details.ModId);
    }

    [Fact]
    public void TheDisplayNameFallsBackToTheFilename()
    {
        var jar = MakeJar("SomeMod-1.2.3.jar", ("fabric.mod.json", """{ "id": "m", "version": "1" }"""));
        var mod = new Mod(jar);

        ModUtils.Process(mod);

        // Schema 0 with no "name" leaves the mod unnamed, so the file's own name stands in.
        mod.Details.Name = string.Empty;
        Assert.Equal("SomeMod-1.2.3", mod.DisplayName);
    }

    [Fact]
    public void AByteOrderMarkDoesNotBreakModMetadata()
    {
        // Mod metadata is hand-written too; see ResourcePackUtils.ParseManifest.
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Bytes("""{ "schemaVersion": 1, "id": "m", "version": "1", "name": "M" }"""))
            .ToArray();

        Assert.Equal("M", ModUtils.ReadFabricModInfo(withBom).Name);
    }
}
