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
 * Ported in behaviour from launcher/ui/dialogs/ImportResourceDialog.cpp.
 *
 * PUTTING A DROPPED FILE WHERE IT BELONGS. Somebody downloaded a jar in a browser and wants it in
 * their instance; the launcher works out what it is and copies it into the right folder. Without this
 * the answer is "open the folder yourself and work out which one", which is what every launcher
 * before this one made people do.
 *
 * COPIED, NOT MOVED. The file is somebody's own, sitting in their downloads folder, and a launcher
 * that makes it disappear from where they put it has done something they did not ask for.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Mods;

namespace ExtremeLauncher.Launch;

/// <summary>What happened to one dropped file.</summary>
public sealed record ImportedResource(string SourcePath, PackedResourceType Type, string Destination, string Error = "")
{
    public bool Succeeded => Error.Length == 0 && Destination.Length != 0;

    public string FileName => Path.GetFileName(SourcePath);
}

public static class ResourceImport
{
    /// <summary>
    /// Identifies each file and copies it into the instance.
    /// </summary>
    /// <returns>One result per file, in the order given, whether or not each worked.</returns>
    public static IReadOnlyList<ImportedResource> Import(InstancePaths paths, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(files);

        var results = new List<ImportedResource>();

        foreach (var file in files)
        {
            results.Add(ImportOne(paths, file));
        }

        return results;
    }

    private static ImportedResource ImportOne(InstancePaths paths, string file)
    {
        var type = LocalResourceParse.Identify(file);

        if (type == PackedResourceType.Unknown)
        {
            /*
             * REFUSED, not filed somewhere plausible. A launcher that drops an unrecognised zip into
             * mods/ produces an instance that will not start and nothing on screen explaining why --
             * and the user, who knows what they dropped, cannot tell that it was misfiled.
             */
            return new ImportedResource(file, type, string.Empty, "not something this launcher recognises");
        }

        var folder = FileSystem.PathCombine(paths.GameRoot, LocalResourceParse.FolderFor(type));

        /*
         * A WORLD IS NOT A FILE YOU DROP IN A FOLDER. Every other kind is: a mod jar lives in mods/, a
         * resource pack zip in resourcepacks/. A world save must be a DIRECTORY, saves/<name>/, with a
         * level.dat inside -- copying the zip to saves/world.zip produces something Minecraft cannot see.
         * So it is extracted (a zip) or copied whole (a folder) through World.Install, which also names
         * it after the world rather than the file and deduplicates against what is already there.
         */
        if (type == PackedResourceType.WorldSave)
        {
            try
            {
                FileSystem.EnsureFolderPathExists(folder);

                return World.Install(file, folder) is { } installed
                    ? new ImportedResource(file, type, installed)
                    : new ImportedResource(file, type, string.Empty, "could not read the world");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new ImportedResource(file, type, string.Empty, e.Message);
            }
        }

        try
        {
            FileSystem.EnsureFolderPathExists(folder);

            /*
             * A RESOURCE CAN BE A DIRECTORY, not only a jar or zip -- an unpacked resource pack, say.
             * Its name is the folder's own, taken trailing-slash-safe: Path.GetFileName of a path
             * ending in a separator is empty, which produced the bug upstream's test_1178 pins (the
             * whole thing copied to a folder with no name).
             */
            var name = LeafName(file);

            if (Directory.Exists(file))
            {
                var directoryTarget = UniqueName(folder, name, existing: p => Directory.Exists(p) || File.Exists(p));

                CopyDirectory(file, directoryTarget);

                return new ImportedResource(file, type, directoryTarget);
            }

            var target = FileSystem.PathCombine(folder, name);

            /*
             * A CLASHING NAME IS SUFFIXED rather than overwritten. Dropping a newer build of a mod
             * whose file name has not changed is a real thing people do, and it deserves to be a
             * visible second file they can then delete -- not a silent replacement of something they
             * might have wanted to keep.
             */
            if (File.Exists(target))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var extension = Path.GetExtension(name);

                for (var n = 2; ; n++)
                {
                    var candidate = FileSystem.PathCombine(folder, $"{stem}-{n}{extension}");

                    if (!File.Exists(candidate))
                    {
                        target = candidate;

                        break;
                    }
                }
            }

            // Copied, not moved: the file is somebody's own and it stays where they put it.
            File.Copy(file, target);

            return new ImportedResource(file, type, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ImportedResource(file, type, string.Empty, e.Message);
        }
    }

    /// <summary>
    /// The last path segment, safe against a trailing separator. <see cref="Path.GetFileName(string)"/>
    /// returns empty for a path ending in "/" or "\", so the separators are trimmed first.
    /// </summary>
    private static string LeafName(string path)
        => Path.GetFileName(path.TrimEnd('/', '\\'));

    /// <summary>A target path under <paramref name="folder"/> that does not yet exist, suffixed if needed.</summary>
    private static string UniqueName(string folder, string name, Func<string, bool> existing)
    {
        var target = FileSystem.PathCombine(folder, name);

        for (var n = 2; existing(target); n++)
        {
            target = FileSystem.PathCombine(folder, $"{name}-{n}");
        }

        return target;
    }

    /// <summary>Copies a directory tree into a new location.</summary>
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var sourceFile in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(sourceFile, Path.Combine(destination, Path.GetRelativePath(source, sourceFile)), overwrite: true);
        }
    }
}
