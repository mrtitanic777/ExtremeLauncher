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
 * Dropping files onto an instance.
 *
 * ASSERTED ON THE FOLDERS. Where a file ended up is the entire question, and the game reads those
 * folders rather than anything this launcher remembers.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Mods;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ResourceImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "el-drop-" + Guid.NewGuid().ToString("N"));

    private readonly InstancePaths _paths;

    private readonly string _downloads;

    public ResourceImportTests()
    {
        var instance = Path.Combine(_root, "instance");

        Directory.CreateDirectory(instance);

        _paths = new InstancePaths(instance);
        _downloads = Path.Combine(_root, "downloads");

        Directory.CreateDirectory(_paths.GameRoot);
        Directory.CreateDirectory(_downloads);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A zip in the "downloads" folder, as if a browser had just put it there.</summary>
    private string Downloaded(string name, params (string Path, string Contents)[] entries)
    {
        var path = Path.Combine(_downloads, name);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entryPath, contents) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open(), Encoding.UTF8);

            writer.Write(contents);
        }

        return path;
    }

    private string ModJar(string name = "sodium.jar")
        => Downloaded(name, ("fabric.mod.json", """{"schemaVersion":1,"id":"sodium","version":"0.5.13","name":"Sodium"}"""));

    private string ResourcePackZip(string name = "faithful.zip")
        => Downloaded(
            name,
            ("pack.mcmeta", """{"pack":{"pack_format":15,"description":"Faithful"}}"""),
            ("assets/minecraft/textures/block/stone.png", "not really a png"));

    private string InGame(string relative)
        => Path.Combine(_paths.GameRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void AModLandsInTheModsFolder()
    {
        var result = Assert.Single(ResourceImport.Import(_paths, [ModJar()]));

        Assert.True(result.Succeeded);
        Assert.Equal(PackedResourceType.Mod, result.Type);
        Assert.True(File.Exists(InGame("mods/sodium.jar")));
    }

    [Fact]
    public void AResourcePackLandsInTheResourcePacksFolder()
    {
        // The point of identifying at all: the same gesture files two different things differently.
        var result = Assert.Single(ResourceImport.Import(_paths, [ResourcePackZip()]));

        Assert.Equal(PackedResourceType.ResourcePack, result.Type);
        Assert.True(File.Exists(InGame("resourcepacks/faithful.zip")));
        Assert.False(File.Exists(InGame("mods/faithful.zip")));
    }

    [Fact]
    public void TheOriginalFileStaysWhereItWas()
    {
        /*
         * COPIED, NOT MOVED. The file is somebody's own, sitting in their downloads folder, and a
         * launcher that makes it vanish from where they put it has done something they did not ask
         * for.
         */
        var source = ModJar();

        ResourceImport.Import(_paths, [source]);

        Assert.True(File.Exists(source));
    }

    [Fact]
    public void SomethingUnrecognisedIsRefusedRatherThanFiledSomewhere()
    {
        /*
         * A launcher that drops an unrecognised zip into mods/ produces an instance that will not
         * start and nothing on screen explaining why -- and the user, who knows what they dropped,
         * cannot tell it was misfiled.
         */
        var source = Downloaded("holiday-photos.zip", ("beach.jpg", "not a minecraft thing"));

        var result = Assert.Single(ResourceImport.Import(_paths, [source]));

        Assert.False(result.Succeeded);
        Assert.Equal(PackedResourceType.Unknown, result.Type);
        Assert.NotEqual(string.Empty, result.Error);

        // And nothing was written anywhere.
        Assert.False(Directory.Exists(InGame("mods")));
    }

    [Fact]
    public void ADroppedFileWithAClashingNameIsKeptAlongsideTheOldOne()
    {
        /*
         * Dropping a newer build whose file name has not changed is a real thing people do. Silently
         * replacing the old one removes something they might have wanted to keep, and leaves no sign
         * that anything was replaced.
         */
        ResourceImport.Import(_paths, [ModJar()]);

        var second = ResourceImport.Import(_paths, [ModJar()]);

        Assert.True(second[0].Succeeded);
        Assert.True(File.Exists(InGame("mods/sodium.jar")));
        Assert.True(File.Exists(InGame("mods/sodium-2.jar")));
    }

    [Fact]
    public void SeveralFilesAtOnceEachGoWhereTheyBelong()
    {
        // Dropping a handful together is the normal case, not the exception.
        var results = ResourceImport.Import(_paths, [ModJar(), ResourcePackZip()]);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.True(File.Exists(InGame("mods/sodium.jar")));
        Assert.True(File.Exists(InGame("resourcepacks/faithful.zip")));
    }

    [Fact]
    public void OneBadFileDoesNotStopTheGoodOnes()
    {
        // A mixed selection is exactly what a multi-file drop tends to be.
        var bad = Downloaded("nonsense.zip", ("readme.txt", "nothing to see"));

        var results = ResourceImport.Import(_paths, [bad, ModJar()]);

        Assert.False(results[0].Succeeded);
        Assert.True(results[1].Succeeded);
        Assert.True(File.Exists(InGame("mods/sodium.jar")));
    }

    [Fact]
    public void ResultsComeBackInTheOrderTheyWereGiven()
    {
        // The caller pairs them with what it handed over, so the order is part of the contract.
        var mod = ModJar("first.jar");
        var pack = ResourcePackZip("second.zip");

        var results = ResourceImport.Import(_paths, [mod, pack]);

        Assert.Equal(mod, results[0].SourcePath);
        Assert.Equal(pack, results[1].SourcePath);
    }

    [Fact]
    public void AFileThatIsNotThereIsReportedRatherThanThrowing()
    {
        var result = Assert.Single(ResourceImport.Import(_paths, [Path.Combine(_downloads, "gone.jar")]));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void DroppingNothingDoesNothing()
    {
        Assert.Empty(ResourceImport.Import(_paths, []));
    }

    // ================================================================== worlds are not just files

    private static byte[] LevelDat(string levelName)
    {
        var data = new NbtTag { Type = NbtTagType.Compound, Name = "Data" };
        data.Put("LevelName", levelName);

        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Children.Add(data);

        return Nbt.WriteCompressed(root);
    }

    /// <summary>A world zip in "downloads": the world wrapped in its folder, the way one is exported --
    /// "<world>/level.dat" one level down, which is what WorldSave detection recognises.</summary>
    private string WorldZip(string name, string wrapper, string levelName)
    {
        var path = Path.Combine(_downloads, name);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        using (var level = archive.CreateEntry($"{wrapper}/level.dat").Open())
        {
            level.Write(LevelDat(levelName));
        }

        using (var region = archive.CreateEntry($"{wrapper}/region/r.0.0.mca").Open())
        {
            region.Write([1, 2, 3, 4]);
        }

        return path;
    }

    /*
     * THE BUG THIS FIXES: a dropped world zip used to be copied to saves/world.zip -- a file Minecraft
     * cannot see. It has to become saves/<name>/level.dat, a directory. Everything else drops as a file;
     * a world does not.
     */
    [Fact]
    public void AWorldZipIsExtractedIntoSavesNotCopiedAsAFile()
    {
        var result = Assert.Single(
            ResourceImport.Import(_paths, [WorldZip("upload.zip", "MyWorld", "Imported World")]));

        Assert.True(result.Succeeded);
        Assert.Equal(PackedResourceType.WorldSave, result.Type);

        // Named after the world from level.dat, a real directory with its contents -- the wrapper folder
        // stripped, and no zip left sitting in saves/.
        Assert.True(File.Exists(InGame("saves/Imported World/level.dat")));
        Assert.True(File.Exists(InGame("saves/Imported World/region/r.0.0.mca")));
        Assert.False(File.Exists(InGame("saves/upload.zip")));
        Assert.False(Directory.Exists(InGame("saves/MyWorld")));
    }
}
