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
 * A SAVED WORLD IS THE MOST IRREPLACEABLE THING THE LAUNCHER TOUCHES. A mod can be re-downloaded and
 * an instance rebuilt; a world someone has played for two years cannot. So these tests lean on what
 * happens when things are wrong — an unreadable level.dat, a rename that half-succeeds — rather than
 * on reading a good one.
 *
 * Real level.dat files are built with the NBT writer, which is itself checked against hand-assembled
 * bytes from the specification. That keeps this from being two of my own components agreeing.
 */

using System.IO.Compression;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class WorldTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-world-" + Guid.NewGuid().ToString("N"));

    private readonly string _saves;

    public WorldTests()
    {
        _saves = Path.Combine(_temp, "saves");
        Directory.CreateDirectory(_saves);
    }

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

    /// <summary>Builds a world folder with a real, gzipped level.dat.</summary>
    private string MakeWorld(
        string folderName,
        string? levelName = null,
        long? lastPlayed = null,
        int? gameType = null,
        long? seed = null,
        bool modernSeed = true)
    {
        var path = Path.Combine(_saves, folderName);
        Directory.CreateDirectory(path);

        var data = new NbtTag { Type = NbtTagType.Compound, Name = "Data" };

        if (levelName is not null)
        {
            data.Put("LevelName", levelName);
        }

        if (lastPlayed is { } played)
        {
            data.Children.Add(new NbtTag { Type = NbtTagType.Long, Name = "LastPlayed", Value = played });
        }

        if (gameType is { } mode)
        {
            data.Children.Add(new NbtTag { Type = NbtTagType.Int, Name = "GameType", Value = mode });
        }

        if (seed is { } value)
        {
            if (modernSeed)
            {
                var settings = new NbtTag { Type = NbtTagType.Compound, Name = "WorldGenSettings" };
                settings.Children.Add(new NbtTag { Type = NbtTagType.Long, Name = "seed", Value = value });

                data.Children.Add(settings);
            }
            else
            {
                data.Children.Add(new NbtTag { Type = NbtTagType.Long, Name = "RandomSeed", Value = value });
            }
        }

        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Children.Add(data);

        File.WriteAllBytes(Path.Combine(path, "level.dat"), Nbt.WriteCompressed(root));

        return path;
    }

    // ================================================================== reading

    [Fact]
    public void AWorldIsReadFromItsLevelDat()
    {
        var world = World.Load(MakeWorld(
            "MyWorld-1",
            levelName: "My Survival World",
            lastPlayed: 1_700_000_000_000,
            gameType: 0,
            seed: -4_172_144_997_902_289_642));

        Assert.True(world.IsValid);
        Assert.Equal("My Survival World", world.ActualName);
        Assert.Equal("MyWorld-1", world.FolderName);
        Assert.Equal(GameTypeKind.Survival, world.GameType.Kind);
        Assert.Equal(-4_172_144_997_902_289_642, world.RandomSeed);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), world.LastPlayed);
    }

    /*
     * A WORLD HAS TWO NAMES AND THEY DISAGREE. They start the same and drift apart the moment a world
     * is renamed in-game or a folder is renamed by hand, so neither is derived from the other.
     */
    [Fact]
    public void TheFolderNameAndTheLevelNameAreKeptSeparately()
    {
        var world = World.Load(MakeWorld("New World (2)", levelName: "Hardcore Attempt 4"));

        Assert.Equal("New World (2)", world.FolderName);
        Assert.Equal("Hardcore Attempt 4", world.ActualName);
    }

    /// <summary>A world whose level.dat has no name falls back to the folder.</summary>
    [Fact]
    public void AWorldWithNoLevelNameUsesItsFolderName()
        => Assert.Equal("Unnamed", World.Load(MakeWorld("Unnamed")).ActualName);

    /*
     * THE SEED MOVED. Modern worlds keep it under WorldGenSettings/seed; older ones have RandomSeed at
     * the top level, and both are still in the wild -- a world created in 1.15 and played in 1.21
     * keeps the old layout.
     */
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSeedIsFoundInEitherLocation(bool modern)
        => Assert.Equal(12345L, World.Load(MakeWorld("W", seed: 12345, modernSeed: modern)).RandomSeed);

    /// <summary>The modern location wins when a world somehow has both.</summary>
    [Fact]
    public void TheModernSeedLocationIsPreferred()
    {
        var path = MakeWorld("W", seed: 111, modernSeed: true);

        var root = Nbt.ReadCompressed(File.ReadAllBytes(Path.Combine(path, "level.dat")));
        root.Get("Data")!.Children.Add(new NbtTag { Type = NbtTagType.Long, Name = "RandomSeed", Value = 999L });
        File.WriteAllBytes(Path.Combine(path, "level.dat"), Nbt.WriteCompressed(root));

        Assert.Equal(111L, World.Load(path).RandomSeed);
    }

    [Theory]
    [InlineData(0, GameTypeKind.Survival)]
    [InlineData(1, GameTypeKind.Creative)]
    [InlineData(2, GameTypeKind.Adventure)]
    [InlineData(3, GameTypeKind.Spectator)]
    public void GameTypesAreMinecraftsNumbering(int value, GameTypeKind expected)
        => Assert.Equal(expected, World.Load(MakeWorld("W", gameType: value)).GameType.Kind);

    /*
     * The ORIGINAL number is kept alongside the interpretation: a future Minecraft could add a fifth
     * mode, and showing "4" is better than showing "Survival", which is what an unrecognised value
     * would otherwise fall back to.
     */
    [Fact]
    public void AnUnknownGameTypeKeepsItsNumber()
    {
        var world = World.Load(MakeWorld("W", gameType: 4));

        Assert.Equal(GameTypeKind.Unknown, world.GameType.Kind);
        Assert.Equal(4, world.GameType.Original);
        Assert.Contains("4", world.GameType.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AWorldWithNoGameTypeSaysSo()
    {
        var world = World.Load(MakeWorld("W"));

        Assert.Null(world.GameType.Original);
        Assert.Equal("Undefined", world.GameType.ToString());
    }

    /// <summary>The file's own timestamp stands in for a world that never recorded one.</summary>
    [Fact]
    public void AWorldWithNoLastPlayedUsesTheFilesTimestamp()
    {
        var world = World.Load(MakeWorld("W"));

        Assert.True(world.IsValid);
        Assert.True(world.LastPlayed > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    // ================================================================== what is not a world

    /*
     * A saves folder routinely contains half-copied worlds and leftovers. Refusing to list the other
     * thirty-nine because of one of them helps nobody.
     */
    [Fact]
    public void AFolderWithNoLevelDatIsInvalidRatherThanFatal()
    {
        Directory.CreateDirectory(Path.Combine(_saves, "NotAWorld"));

        var world = World.Load(Path.Combine(_saves, "NotAWorld"));

        Assert.False(world.IsValid);
        Assert.Equal("NotAWorld", world.ActualName);
    }

    [Fact]
    public void AnUnreadableLevelDatIsInvalidRatherThanFatal()
    {
        var path = Path.Combine(_saves, "Corrupt");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "level.dat"), "this is not gzipped NBT");

        Assert.False(World.Load(path).IsValid);
    }

    /// <summary>Everything lives under a "Data" compound; a document without one is not a world.</summary>
    [Fact]
    public void ADocumentWithNoDataCompoundIsInvalid()
    {
        var path = Path.Combine(_saves, "Odd");
        Directory.CreateDirectory(path);

        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Put("Something", "else");

        File.WriteAllBytes(Path.Combine(path, "level.dat"), Nbt.WriteCompressed(root));

        Assert.False(World.Load(path).IsValid);
    }

    // ================================================================== renaming

    [Fact]
    public void RenamingWritesTheNewNameIntoLevelDat()
    {
        var world = World.Load(MakeWorld("MyWorld", levelName: "Old Name", seed: 42));

        Assert.True(world.Rename("New Name"));
        Assert.Equal("New Name", world.ActualName);

        // Read back from disk, through a fresh World, so this is not just the in-memory field.
        var reloaded = World.Load(Path.Combine(_saves, "New Name"));

        Assert.True(reloaded.IsValid);
        Assert.Equal("New Name", reloaded.ActualName);
    }

    /*
     * The whole of level.dat is rewritten to change one string, so every tag the launcher does not
     * understand has to survive. This is the property the NBT writer exists for.
     */
    [Fact]
    public void RenamingPreservesEverythingElseInTheFile()
    {
        var path = MakeWorld("MyWorld", levelName: "Old", gameType: 1, seed: 777);

        // A tag the launcher has no idea about, of a type it never reads.
        var root = Nbt.ReadCompressed(File.ReadAllBytes(Path.Combine(path, "level.dat")));
        root.Get("Data")!.Children.Add(new NbtTag
        {
            Type = NbtTagType.LongArray,
            Name = "SomeFutureThing",
            Value = new[] { 1L, -2L, 3L },
        });
        File.WriteAllBytes(Path.Combine(path, "level.dat"), Nbt.WriteCompressed(root));

        Assert.True(World.Load(path).Rename("Renamed"));

        var reloaded = Nbt.ReadCompressed(File.ReadAllBytes(Path.Combine(_saves, "Renamed", "level.dat")));
        var data = reloaded.Get("Data")!;

        Assert.Equal("Renamed", data.GetString("LevelName"));
        Assert.Equal(1, data.GetInt("GameType"));
        Assert.Equal(777L, data.Get("WorldGenSettings")!.GetLong("seed"));
        Assert.Equal([1L, -2L, 3L], (long[])data.Get("SomeFutureThing")!.Value!);
    }

    [Fact]
    public void RenamingMovesTheFolderToo()
    {
        var world = World.Load(MakeWorld("OldFolder", levelName: "Old"));

        world.Rename("Brand New");

        Assert.True(Directory.Exists(Path.Combine(_saves, "Brand New")));
        Assert.False(Directory.Exists(Path.Combine(_saves, "OldFolder")));
    }

    /// <summary>A name already taken by another folder gets a suffix rather than colliding.</summary>
    [Fact]
    public void RenamingOntoAnExistingFolderNameDoesNotCollide()
    {
        MakeWorld("Taken", levelName: "Taken");

        var world = World.Load(MakeWorld("Mine", levelName: "Mine"));

        Assert.True(world.Rename("Taken"));

        // Both survive: the level name is "Taken", the folder is something else.
        Assert.True(Directory.Exists(Path.Combine(_saves, "Taken")));
        Assert.Equal(2, Directory.GetDirectories(_saves).Length);
    }

    [Fact]
    public void AnInvalidWorldCannotBeRenamed()
    {
        Directory.CreateDirectory(Path.Combine(_saves, "NotAWorld"));

        Assert.False(World.Load(Path.Combine(_saves, "NotAWorld")).Rename("Anything"));
    }

    [Fact]
    public void RenamingToNothingIsRefused()
        => Assert.False(World.Load(MakeWorld("W", levelName: "W")).Rename(string.Empty));

    // ================================================================== scanning

    /*
     * INVALID WORLDS ARE LISTED TOO. Hiding one makes a save that failed to copy simply vanish from
     * the launcher, which is indistinguishable from having been deleted.
     */
    [Fact]
    public void ScanningListsEveryFolderIncludingBrokenOnes()
    {
        MakeWorld("Alpha", levelName: "Alpha");
        MakeWorld("Beta", levelName: "Beta");
        Directory.CreateDirectory(Path.Combine(_saves, "Broken"));

        var worlds = WorldList.Scan(_saves);

        Assert.Equal(["Alpha", "Beta", "Broken"], worlds.Select(w => w.FolderName));
        Assert.Equal(2, worlds.Count(w => w.IsValid));
    }

    [Fact]
    public void ScanningAMissingFolderIsEmptyRatherThanFatal()
        => Assert.Empty(WorldList.Scan(Path.Combine(_temp, "nope")));

    /// <summary>A world name may contain anything, including an emoji — see the NBT encoding note.</summary>
    [Fact]
    public void AWorldNamedWithAnEmojiRoundTrips()
    {
        var world = World.Load(MakeWorld("W", levelName: "Base \U0001F600"));

        Assert.Equal("Base \U0001F600", world.ActualName);

        Assert.True(world.Rename("Nether \U0001F525"));
        Assert.Equal("Nether \U0001F525", World.Load(world.Path).ActualName);
    }

    // ================================================================== resetting the icon

    [Fact]
    public void AWorldWithAnIconCanHaveItReset()
    {
        var path = MakeWorld("W", levelName: "Iconic");
        File.WriteAllBytes(Path.Combine(path, "icon.png"), [1, 2, 3]);

        var world = World.Load(path);
        Assert.True(world.HasIcon);

        Assert.True(world.ResetIcon());
        Assert.False(File.Exists(Path.Combine(path, "icon.png")));
        Assert.False(world.HasIcon);
    }

    [Fact]
    public void ResettingAWorldWithNoIconDoesNothing()
    {
        // Nothing to remove is not a failure -- upstream disables the action, and ResetIcon returns
        // false so the caller knows there was nothing to do.
        var world = World.Load(MakeWorld("W", levelName: "Plain"));

        Assert.False(world.HasIcon);
        Assert.False(world.ResetIcon());
    }

    // ================================================================== importing from a zip

    /// <summary>A world packed into a zip, optionally wrapped in an inner folder like a "compress
    /// this folder" archive. Carries a region file so the whole subtree, not just level.dat, proves
    /// it extracted.</summary>
    private string MakeWorldZip(string zipName, string? levelName, string prefix = "")
    {
        var data = new NbtTag { Type = NbtTagType.Compound, Name = "Data" };

        if (levelName is not null)
        {
            data.Put("LevelName", levelName);
        }

        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Children.Add(data);

        var zipPath = Path.Combine(_temp, zipName);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        WriteEntry(zip, prefix + "level.dat", Nbt.WriteCompressed(root));
        WriteEntry(zip, prefix + "region/r.0.0.mca", [1, 2, 3, 4]);

        return zipPath;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    [Fact]
    public void AWorldZipIsImportedUnderItsLevelName()
    {
        var zip = MakeWorldZip("upload.zip", "Imported World");

        var dest = World.Install(zip, _saves);

        Assert.Equal(FileSystem.PathCombine(_saves, "Imported World"), dest);
        Assert.True(File.Exists(Path.Combine(dest!, "level.dat")));

        // The whole subtree, not just level.dat: a "world" that lost its region files is not one.
        Assert.True(File.Exists(Path.Combine(dest!, "region", "r.0.0.mca")));
    }

    [Fact]
    public void AWorldWrappedInAFolderIsImportedWithoutTheWrapper()
    {
        // A zip made by compressing the world's folder puts everything under "MyWorld/". The wrapper
        // is stripped: the saves folder holds the world, not a folder holding the world.
        var zip = MakeWorldZip("upload.zip", "Imported World", prefix: "MyWorld/");

        var dest = World.Install(zip, _saves);

        Assert.Equal(FileSystem.PathCombine(_saves, "Imported World"), dest);
        Assert.True(File.Exists(Path.Combine(dest!, "level.dat")));
        Assert.True(File.Exists(Path.Combine(dest!, "region", "r.0.0.mca")));
    }

    [Fact]
    public void TheDestinationIsNamedForTheWorldNotTheZip()
    {
        // A file called "Backup (3).zip" should not carry that name into the saves list.
        var zip = MakeWorldZip("Backup (3).zip", "Survival");

        var dest = World.Install(zip, _saves);

        Assert.Equal(FileSystem.PathCombine(_saves, "Survival"), dest);
    }

    [Fact]
    public void AZipWithNoLevelNameFallsBackToTheZipFileName()
    {
        var zip = MakeWorldZip("MyExport.zip", levelName: null);

        var dest = World.Install(zip, _saves);

        Assert.Equal(FileSystem.PathCombine(_saves, "MyExport"), dest);
    }

    [Fact]
    public void AZipWithNoLevelDatImportsNothing()
    {
        var zipPath = Path.Combine(_temp, "notaworld.zip");

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "readme.txt", [1, 2, 3]);
        }

        Assert.Null(World.Install(zipPath, _saves));

        // Nothing half-written: a refused import leaves the saves folder as it was.
        Assert.False(Directory.Exists(_saves) && Directory.EnumerateFileSystemEntries(_saves).Any());
    }

    [Fact]
    public void AZipWhoseLevelDatWillNotParseIsRefused()
    {
        var zipPath = Path.Combine(_temp, "corrupt.zip");

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "level.dat", System.Text.Encoding.UTF8.GetBytes("not gzipped NBT"));
        }

        Assert.Null(World.Install(zipPath, _saves));
    }

    [Fact]
    public void ImportingTheSameWorldTwiceDoesNotCollide()
    {
        var zip = MakeWorldZip("upload.zip", "Imported World");

        var first = World.Install(zip, _saves);
        var second = World.Install(zip, _saves);

        Assert.Equal(FileSystem.PathCombine(_saves, "Imported World"), first);

        // The second lands beside the first rather than overwriting it -- DirNameFromString dedups.
        Assert.NotEqual(first, second);
        Assert.True(Directory.Exists(first!));
        Assert.True(Directory.Exists(second!));
    }

    [Fact]
    public void AnImportedWorldReadsBackAsAValidWorld()
    {
        // The point of the whole thing: after import, the world opens like any other.
        var zip = MakeWorldZip("upload.zip", "Imported World");

        var world = World.Load(World.Install(zip, _saves)!);

        Assert.True(world.IsValid);
        Assert.Equal("Imported World", world.ActualName);
    }
}
