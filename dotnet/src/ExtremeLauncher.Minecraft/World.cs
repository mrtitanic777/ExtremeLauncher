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
 * Ported from launcher/minecraft/World.{h,cpp} and the scanning half of WorldList.cpp.
 *
 * A SAVED WORLD, WHICH IS THE MOST IRREPLACEABLE THING THE LAUNCHER TOUCHES. A mod can be
 * re-downloaded and an instance rebuilt; a world someone has played for two years cannot. Everything
 * here is shaped by that.
 *
 * A WORLD HAS TWO NAMES AND THEY DISAGREE. The FOLDER name is what the filesystem calls it; the LEVEL
 * name is what Minecraft shows, stored inside level.dat. They start the same and drift apart the
 * moment a world is renamed in-game or a folder is renamed by hand, so both are kept and neither is
 * derived from the other.
 *
 * RENAMING WRITES level.dat AND THEN MOVES THE FOLDER, in that order. If the write fails there is
 * nothing to undo; if the move fails afterwards the world still opens and merely sits in a folder
 * with the old name. The other order risks a world whose folder says one thing and whose contents say
 * another, which is how a save gets lost in a list of forty.
 */

using System.IO.Compression;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

/// <summary>The mode a world was last saved in.</summary>
/// <remarks>
/// The ORIGINAL number is kept alongside the interpretation, as upstream does: a future Minecraft
/// could add a fifth mode, and showing "3" is better than showing "Survival" because that is what an
/// unrecognised value would otherwise fall back to.
/// </remarks>
public readonly record struct GameType(int? Original)
{
    public GameTypeKind Kind => Original switch
    {
        0 => GameTypeKind.Survival,
        1 => GameTypeKind.Creative,
        2 => GameTypeKind.Adventure,
        3 => GameTypeKind.Spectator,
        _ => GameTypeKind.Unknown,
    };

    public override string ToString()
        => Kind == GameTypeKind.Unknown
            ? Original is { } value ? $"Unknown ({value})" : "Undefined"
            : Kind.ToString();
}

public enum GameTypeKind
{
    Unknown = -1,
    Survival = 0,
    Creative = 1,
    Adventure = 2,
    Spectator = 3,
}

public sealed class World
{
    public const string LevelDatName = "level.dat";

    private World(string path) => Path = path;

    /// <summary>
    /// The world's directory.
    /// </summary>
    /// <remarks>
    /// Updated when a rename moves the folder. Leaving it stale is a real defect and not a cosmetic
    /// one: every later operation on the object -- reload, a second rename, a delete -- would address
    /// a directory that is no longer there, and a delete addressing the wrong path is the worst
    /// outcome available in this file. Upstream refreshes its QFileInfo for the same reason.
    /// </remarks>
    public string Path { get; private set; }

    /// <summary>What the filesystem calls it.</summary>
    public string FolderName => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));

    /// <summary>What Minecraft shows, from inside level.dat. Falls back to the folder name.</summary>
    public string ActualName { get; private set; } = string.Empty;

    /// <summary>When it was last played, or the level.dat's own timestamp if it does not say.</summary>
    public DateTimeOffset LastPlayed { get; private set; }

    public GameType GameType { get; private set; }

    public long RandomSeed { get; private set; }

    /// <summary>Whether level.dat could be read and understood.</summary>
    public bool IsValid { get; private set; }

    /// <summary>The world's icon, which Minecraft writes as icon.png in the world folder.</summary>
    public string IconPath => System.IO.Path.Combine(Path, "icon.png");

    /// <summary>Whether the world has a saved icon. False means there is nothing to reset.</summary>
    public bool HasIcon => File.Exists(IconPath);

    /// <summary>
    /// Removes the world's icon so Minecraft regenerates it from the spawn on next play.
    /// </summary>
    /// <remarks>
    /// Ported from World::resetIcon. False when there was no icon to remove -- upstream disables the
    /// action in that case, and it is not a failure, just nothing to do.
    /// </remarks>
    public bool ResetIcon()
    {
        if (!HasIcon)
        {
            return false;
        }

        try
        {
            File.Delete(IconPath);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The usual cause is the game running and holding the file; the caller reports it.
            return false;
        }
    }

    /// <summary>Reads a world folder.</summary>
    /// <remarks>
    /// An unreadable or absent level.dat yields an INVALID world rather than a failure. A saves folder
    /// routinely contains half-copied worlds and leftovers, and refusing to list the other thirty-nine
    /// because of one of them helps nobody.
    /// </remarks>
    public static World Load(string path)
    {
        var world = new World(path);

        world.ActualName = world.FolderName;
        world.Reload();

        return world;
    }

    /// <summary>Re-reads level.dat.</summary>
    public bool Reload()
    {
        IsValid = false;

        var levelDat = System.IO.Path.Combine(Path, LevelDatName);

        if (!File.Exists(levelDat))
        {
            return false;
        }

        // The file's own mtime is the fallback for a world that never recorded when it was played.
        LastPlayed = new DateTimeOffset(File.GetLastWriteTimeUtc(levelDat), TimeSpan.Zero);

        NbtTag data;

        try
        {
            var root = Nbt.ReadCompressed(FileSystem.Read(levelDat));

            // Everything lives under a single "Data" compound; a document without one is not a world.
            if (root.Get("Data") is not { Type: NbtTagType.Compound } found)
            {
                return false;
            }

            data = found;
        }
        catch (Exception e) when (e is LauncherException or IOException or InvalidDataException)
        {
            return false;
        }

        ActualName = data.GetString("LevelName") ?? FolderName;

        if (data.GetLong("LastPlayed") is { } timestamp)
        {
            LastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        }

        GameType = new GameType(data.GetInt("GameType"));

        /*
         * THE SEED MOVED. Modern worlds keep it under WorldGenSettings/seed; older ones have
         * RandomSeed at the top level. Upstream tries the new location first and falls back, and both
         * are still in the wild -- a world created in 1.15 and played in 1.21 keeps the old layout.
         */
        RandomSeed = data.Get("WorldGenSettings")?.GetLong("seed")
            ?? data.GetLong("RandomSeed")
            ?? 0;

        IsValid = true;

        return true;
    }

    /// <summary>
    /// Imports a world from a zip (a <c>.mcworld</c> is a zip too) into a saves folder.
    /// </summary>
    /// <remarks>
    /// Ported from World::install and WorldList::installWorld. The world may sit at the root of the zip
    /// or one folder down -- a zip made by "compress this folder" wraps it in a directory -- so the
    /// level.dat is located wherever it is and only that subtree is extracted, the prefix stripped.
    ///
    /// THE FOLDER IS NAMED AFTER THE WORLD, NOT THE ZIP. The destination takes the world's own name
    /// from level.dat, deduplicated against what is already in the folder. Naming it after the zip
    /// would carry "Backup (3).zip" into the saves list and let two different worlds both called
    /// "World.zip" collide.
    /// </remarks>
    /// <returns>The created folder, or null when the zip holds no readable world, or extraction failed.</returns>
    public static string? Install(string source, string savesFolder)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(savesFolder);

        ZipArchive zip;

        try
        {
            zip = ZipFile.OpenRead(source);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }

        using (zip)
        {
            /*
             * The shallowest level.dat wins (FindFolderOfFileInZip is breadth-first). An empty offset
             * means either "at the root" or "not there at all"; the GetEntry check tells the two apart,
             * so a zip with no level.dat anywhere is refused rather than extracted as an empty world.
             */
            var offset = MMCZip.FindFolderOfFileInZip(zip, LevelDatName);

            if (zip.GetEntry(offset + LevelDatName) is not { } levelDat)
            {
                return null;
            }

            if (!TryReadLevelName(levelDat, out var levelName))
            {
                // level.dat is there but will not parse: not a world we can trust, so not imported.
                return null;
            }

            // The world's own name, or the zip's file name when level.dat names none.
            var name = levelName.Length != 0
                ? levelName
                : System.IO.Path.GetFileNameWithoutExtension(source);

            var folder = FileSystem.DirNameFromString(name, savesFolder);

            if (folder.Length == 0)
            {
                return null;
            }

            var destination = FileSystem.PathCombine(savesFolder, folder);

            if (MMCZip.ExtractSubDir(zip, offset, destination) is null)
            {
                // Refused (zip-slip) or failed partway: leave nothing half-written in the saves list.
                TryDeleteDirectory(destination);

                return null;
            }

            return destination;
        }
    }

    /// <summary>Reads the LevelName out of a level.dat zip entry. False when the NBT will not parse.</summary>
    private static bool TryReadLevelName(ZipArchiveEntry entry, out string name)
    {
        name = string.Empty;

        try
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);

            var root = Nbt.ReadCompressed(memory.ToArray());

            if (root.Get("Data") is not { Type: NbtTagType.Compound } data)
            {
                return false;
            }

            name = data.GetString("LevelName") ?? string.Empty;

            return true;
        }
        catch (Exception e) when (e is LauncherException or IOException or InvalidDataException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the import already failed and this was the tidying up.
        }
    }

    /// <summary>
    /// Renames the world, in level.dat and on disk.
    /// </summary>
    /// <remarks>
    /// The whole of level.dat is rewritten to change one string, which is why the NBT writer has to
    /// preserve tags it does not understand. See Nbt.
    /// </remarks>
    /// <returns>False if nothing was changed. A partial rename leaves a world that still opens.</returns>
    public bool Rename(string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);

        if (!IsValid || newName.Length == 0)
        {
            return false;
        }

        var levelDat = System.IO.Path.Combine(Path, LevelDatName);

        try
        {
            var root = Nbt.ReadCompressed(FileSystem.Read(levelDat));

            if (root.Get("Data") is not { Type: NbtTagType.Compound } data)
            {
                return false;
            }

            data.Put("LevelName", newName);

            // Written first: if this fails there is nothing to undo.
            FileSystem.Write(levelDat, Nbt.WriteCompressed(root));
        }
        catch (Exception e) when (e is LauncherException or IOException or InvalidDataException)
        {
            return false;
        }

        ActualName = newName;

        /*
         * The folder move is best-effort. A world whose folder name no longer matches its level name
         * is untidy and completely functional; failing the whole rename over it would leave the user
         * with a world already renamed inside and a launcher claiming it was not.
         */
        TryMoveFolder(newName);

        return true;
    }

    private void TryMoveFolder(string newName)
    {
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));

        if (parent is null)
        {
            return;
        }

        var target = System.IO.Path.Combine(parent, FileSystem.DirNameFromString(newName, parent));

        if (string.Equals(target, System.IO.Path.GetFullPath(Path), StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Directory.Move(Path, target);

            Path = target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Untidy, not broken. See the note above. Path deliberately stays as it was, because the
            // folder did too.
        }
    }
    /// <summary>
    /// Removes the world from disk.
    /// </summary>
    /// <remarks>
    /// Ported from World::destroy. TRASHED FIRST, and only deleted outright when the platform has no
    /// trash -- which upstream does here and does NOT do for a mod, and the difference is exactly
    /// right: a world is the one thing in an instance that exists nowhere else.
    ///
    /// An INVALID world refuses by default, as upstream does -- a folder the launcher could not parse
    /// is the case where "this is the world you think it is" is least certain.
    /// </remarks>
    /// <param name="allowInvalid">
    /// UPSTREAM BUG #20, and the reason this parameter exists. WorldListPage enables Remove whenever a
    /// ROW is selected -- `index.isValid()` is about the model index, not the world -- while
    /// World::destroy refuses when `!is_valid`. So pressing Remove on a corrupted save does nothing
    /// whatsoever, with no message, and the user cannot clear it up from the launcher at all.
    ///
    /// That is the main reason someone opens this page. The worlds page passes true and confirms
    /// first, so the refusal becomes a deliberate choice made by a person rather than a button that
    /// quietly does not work.
    /// </param>
    public bool Destroy(bool allowInvalid = false)
    {
        if (!IsValid && !allowInvalid)
        {
            return false;
        }

        return Trash.TryTrash(Path, out _) || FileSystem.DeletePath(Path);
    }
}

public static class WorldList
{
    /// <summary>
    /// Every world in a saves folder, valid or not.
    /// </summary>
    /// <remarks>
    /// INVALID WORLDS ARE LISTED TOO, deliberately. A folder that looks like a world but will not
    /// parse is exactly what a user needs to see -- hiding it makes a save that failed to copy simply
    /// vanish from the launcher, which is indistinguishable from having been deleted.
    /// </remarks>
    /// <remarks>Ordered by folder name, so a listing does not depend on enumeration order.</remarks>
    public static List<World> Scan(string savesFolder)
    {
        if (!Directory.Exists(savesFolder))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateDirectories(savesFolder)
                .Order(StringComparer.Ordinal)
                .Select(World.Load),
        ];
    }
}
