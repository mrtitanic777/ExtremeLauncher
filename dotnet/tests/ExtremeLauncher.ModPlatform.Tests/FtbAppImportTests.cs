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
 * Real directories, because that is the input: this reads the FTB app's own instances where they sit
 * on disk. The important property is that nothing throws -- the caller points at a folder and every
 * subdirectory is tried, so one unreadable instance must not lose the other twenty.
 */

using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class FtbAppImportTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-ftb-" + Guid.NewGuid().ToString("N"));

    public FtbAppImportTests() => Directory.CreateDirectory(_temp);

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

    private const string Instance = """
        {
          "uuid": "abc-123", "id": 91, "versionId": 11940,
          "name": "FTB Skies", "version": "1.6.0", "mcVersion": "1.19.2",
          "totalPlayTime": 3600, "jvmArgs": "-Xmx8G"
        }
        """;

    private const string Targets = """
        {
          "targets": [
            { "name": "minecraft", "version": "1.19.2", "type": "game" },
            { "name": "forge", "version": "43.3.5", "type": "modloader" }
          ]
        }
        """;

    private string MakeInstance(string name, string? instance = Instance, string? version = Targets, bool icon = false)
    {
        var path = Path.Combine(_temp, name);
        Directory.CreateDirectory(path);

        if (instance is not null)
        {
            File.WriteAllText(Path.Combine(path, "instance.json"), instance);
        }

        if (version is not null)
        {
            File.WriteAllText(Path.Combine(path, "version.json"), version);
        }

        if (icon)
        {
            File.WriteAllBytes(Path.Combine(path, "folder.jpg"), [0xFF, 0xD8]);
        }

        return path;
    }

    // ================================================================== reading one instance

    [Fact]
    public void AnFtbInstanceIsReadFromItsTwoFiles()
    {
        var pack = FtbAppImport.ParseDirectory(MakeInstance("skies"));

        Assert.NotNull(pack);
        Assert.Equal("abc-123", pack.Uuid);
        Assert.Equal(91, pack.Id);
        Assert.Equal(11940, pack.VersionId);
        Assert.Equal("FTB Skies", pack.Name);
        Assert.Equal("1.19.2", pack.McVersion);
        Assert.Equal(3600, pack.TotalPlayTime);
        Assert.Equal(ModLoaderTypes.Forge, pack.LoaderType);
        Assert.Equal("43.3.5", pack.LoaderVersion);
    }

    /*
     * Upstream's target loop assigns the loader's version over the pack's own `version` field, and the
     * install task reads it back from there -- so the struct's `loaderVersion` member is dead and the
     * pack version is destroyed. Nothing upstream displays the pack version, so it never surfaces.
     * Kept as two fields here, since two meanings in one is one too many.
     */
    [Fact]
    public void ThePackVersionAndTheLoaderVersionAreBothKept()
    {
        var pack = FtbAppImport.ParseDirectory(MakeInstance("skies"))!;

        Assert.Equal("1.6.0", pack.Version);
        Assert.Equal("43.3.5", pack.LoaderVersion);
    }

    [Theory]
    [InlineData("neoforge", ModLoaderTypes.NeoForge)]
    [InlineData("forge", ModLoaderTypes.Forge)]
    [InlineData("fabric", ModLoaderTypes.Fabric)]
    [InlineData("quilt", ModLoaderTypes.Quilt)]
    public void EveryKnownLoaderTargetIsRecognised(string name, ModLoaderTypes expected)
    {
        var version = $$"""
            { "targets": [ { "name": "{{name}}", "version": "1.2.3" } ] }
            """;

        var pack = FtbAppImport.ParseDirectory(MakeInstance("pack", version: version))!;

        Assert.Equal(expected, pack.LoaderType);
        Assert.Equal("1.2.3", pack.LoaderVersion);
    }

    /// <summary>The array also lists the game and sometimes a Java runtime, which are not loaders.</summary>
    [Fact]
    public void NonLoaderTargetsAreSkippedRatherThanSpecialCased()
    {
        var version = """
            { "targets": [
                { "name": "minecraft", "version": "1.19.2" },
                { "name": "java", "version": "17.0.1" },
                { "name": "fabric", "version": "0.15.0" } ] }
            """;

        Assert.Equal(ModLoaderTypes.Fabric, FtbAppImport.ParseDirectory(MakeInstance("p", version: version))!.LoaderType);
    }

    /// <summary>A vanilla FTB pack has no loader target, and that is not a failure.</summary>
    [Fact]
    public void AVanillaPackHasNoLoader()
    {
        var version = """{ "targets": [ { "name": "minecraft", "version": "1.19.2" } ] }""";

        var pack = FtbAppImport.ParseDirectory(MakeInstance("p", version: version))!;

        Assert.Equal(ModLoaderTypes.None, pack.LoaderType);
        Assert.Equal(string.Empty, pack.LoaderVersion);
    }

    [Fact]
    public void AnIconIsPickedUpWhenTheAppWroteOne()
    {
        Assert.NotEqual(string.Empty, FtbAppImport.ParseDirectory(MakeInstance("with", icon: true))!.IconPath);
        Assert.Equal(string.Empty, FtbAppImport.ParseDirectory(MakeInstance("without"))!.IconPath);
    }

    // ================================================================== what is not an instance

    /*
     * NOTHING THROWS. The caller points at the FTB app's instances folder and tries every
     * subdirectory, so anything that is not an instance has to come back as "not one" rather than as
     * an exception that loses the rest of the scan.
     */
    [Theory]
    [MemberData(nameof(NotInstances))]
    public void ADirectoryThatIsNotAnFtbInstanceIsNullRatherThanFatal(string? instance, string? version)
        => Assert.Null(FtbAppImport.ParseDirectory(
            MakeInstance("candidate-" + Guid.NewGuid().ToString("N"), instance, version)));

    public static TheoryData<string?, string?> NotInstances() => new()
    {
        // Neither file: an ordinary folder that happens to sit alongside the instances.
        { null, null },

        // instance.json alone: an install that never finished, which has no loader to import.
        { Instance, null },
        { null, Targets },

        // Present but not JSON at all.
        { "not json", Targets },
        { Instance, "not json" },

        // JSON, but not the shape claimed -- a required field missing, or the wrong type.
        { """{ "uuid": "x" }""", Targets },
        { """{ "uuid": "x", "id": "not a number", "versionId": 1, "name": "n", "version": "1", "mcVersion": "1.19.2", "totalPlayTime": 0 }""", Targets },
        { Instance, """{ "targets": "not an array" }""" },
    };

    [Fact]
    public void AMissingDirectoryIsNullRatherThanFatal()
        => Assert.Null(FtbAppImport.ParseDirectory(Path.Combine(_temp, "does-not-exist")));

    // ================================================================== scanning a folder

    [Fact]
    public void ScanningAFolderSkipsWhatIsNotAnInstance()
    {
        MakeInstance("a");
        MakeInstance("b");
        MakeInstance("junk", instance: null, version: null);
        File.WriteAllText(Path.Combine(_temp, "loose-file.txt"), "x");

        var packs = FtbAppImport.ScanFolder(_temp);

        Assert.Equal(2, packs.Count);
    }

    /// <summary>Ordered, because directory enumeration order is not.</summary>
    [Fact]
    public void ScanResultsAreOrdered()
    {
        foreach (var name in (string[])["c", "a", "b"])
        {
            MakeInstance(name);
        }

        Assert.Equal(
            ["a", "b", "c"],
            FtbAppImport.ScanFolder(_temp).Select(p => Path.GetFileName(p.Path)));
    }

    [Fact]
    public void ScanningAMissingFolderIsEmptyRatherThanFatal()
        => Assert.Empty(FtbAppImport.ScanFolder(Path.Combine(_temp, "nope")));

    // ================================================================== components

    [Fact]
    public void ComponentsCoverMinecraftAndTheLoader()
    {
        var components = FtbAppImport.GetComponents(FtbAppImport.ParseDirectory(MakeInstance("p"))!);

        Assert.Equal(
            [(PackComponents.MinecraftUid, "1.19.2"), (PackComponents.ForgeUid, "43.3.5")],
            components.Select(c => (c.Uid, c.Version)));
    }

    /*
     * BOTH are important here, unlike the pack importers where only Minecraft is. Upstream's choice,
     * and defensible: an FTB app instance is being adopted rather than installed from a recipe, so the
     * loader is as much the user's as the game version.
     */
    [Fact]
    public void TheLoaderIsMarkedImportantToo()
        => Assert.All(
            FtbAppImport.GetComponents(FtbAppImport.ParseDirectory(MakeInstance("p"))!),
            c => Assert.True(c.Important));

    [Fact]
    public void AVanillaPackGetsMinecraftAlone()
    {
        var version = """{ "targets": [ { "name": "minecraft", "version": "1.19.2" } ] }""";

        Assert.Single(FtbAppImport.GetComponents(FtbAppImport.ParseDirectory(MakeInstance("p", version: version))!));
    }
}
