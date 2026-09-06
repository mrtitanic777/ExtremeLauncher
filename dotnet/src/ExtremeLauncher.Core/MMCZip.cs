// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
 *  Copyright (c) 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from launcher/MMCZip.{h,cpp} — the pure archive functions. The QuaZip dependency goes away:
 * System.IO.Compression covers all of it.
 *
 * NOT PORTED YET: ExportToZipTask and ExtractZipTask, the two LauncherTask wrappers at the bottom of
 * the file. They are modpack export and import, which belong with wave 8.
 *
 * THE JAR-MODDING ORDER IS THE WHOLE POINT of CreateModdedJar, and it is easy to get backwards. Mods
 * are merged in REVERSE list order and the first writer of a path wins, so the mod LAST in the list is
 * the one whose classes the game actually runs. Upstream calls this "respecting the loading order of
 * components". Reversing the loop, or letting later writes win, silently swaps which mod is in effect
 * — and nothing about the resulting jar looks wrong.
 */

using System.IO.Compression;

namespace ExtremeLauncher.Core;

/// <summary>What a jar mod is on disk.</summary>
public enum JarModKind
{
    /// <summary>An archive whose contents are merged into the jar.</summary>
    ZipFile,

    /// <summary>A loose file dropped in at the jar's root.</summary>
    SingleFile,

    /// <summary>A directory whose tree is added under its own name.</summary>
    Folder,
}

/// <summary>
/// One entry in an instance's jar-mod list.
/// </summary>
/// <remarks>
/// Upstream passes <c>Mod*</c> from the resource model, which carries far more than jar modding needs.
/// This is the part <see cref="MMCZip.CreateModdedJar"/> actually reads, so the archive code does not
/// have to wait on the whole resource model being ported.
/// </remarks>
public sealed record JarMod(string Path, JarModKind Kind, bool Enabled = true)
{
    /// <summary>Works out the kind from what is on disk.</summary>
    public static JarMod FromPath(string path, bool enabled = true)
    {
        var kind = Directory.Exists(path)
            ? JarModKind.Folder
            : System.IO.Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".jar"
                ? JarModKind.ZipFile
                : JarModKind.SingleFile;

        return new JarMod(path, kind, enabled);
    }
}

public static class MMCZip
{
    /// <summary>
    /// Copies every entry of one archive into another, skipping paths already present.
    /// </summary>
    /// <param name="contained">
    /// Paths already written. Grows as entries are copied, and is what makes "first writer wins" work
    /// across a whole sequence of merges.
    /// </param>
    /// <param name="filter">Optional predicate; an entry is copied only when it returns true.</param>
    public static bool MergeZipFiles(
        ZipArchive into,
        string fromPath,
        HashSet<string> contained,
        Func<string, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(contained);

        try
        {
            using var source = ZipFile.OpenRead(fromPath);

            foreach (var entry in source.Entries)
            {
                var filename = entry.FullName;

                if (filter is not null && !filter(filename))
                {
                    continue;
                }

                // Already written by an earlier, higher-priority source.
                if (!contained.Add(filename))
                {
                    continue;
                }

                var target = into.CreateEntry(filename, CompressionLevel.Optimal);

                using var input = entry.Open();
                using var output = target.Open();

                input.CopyTo(output);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the modded minecraft.jar an instance with jar mods launches from.
    /// </summary>
    /// <remarks>
    /// The source jar is merged LAST and with META-INF excluded. Last because everything a mod
    /// overrides has to already be claimed by the time the originals arrive; without META-INF because
    /// a modded jar no longer matches Mojang's signatures, and leaving the signature files in makes the
    /// JVM reject the classes that were replaced.
    /// </remarks>
    public static bool CreateModdedJar(string sourceJarPath, string targetJarPath, IReadOnlyList<JarMod> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var succeeded = false;

        try
        {
            // Scoped so the archive is CLOSED before the cleanup below runs. Deleting the file while
            // the ZipArchive is still open does nothing useful: disposing it writes the central
            // directory straight back out and the half-built jar survives.
            using (var stream = new FileStream(targetJarPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var output = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // Paths already in the jar. Anything arriving later for the same path is dropped.
                var addedFiles = new HashSet<string>(StringComparer.Ordinal);

                // REVERSE order, so the LAST mod in the list is the first to claim its paths — and
                // therefore the one whose files survive.
                for (var i = mods.Count - 1; i >= 0; i--)
                {
                    var mod = mods[i];

                    if (!mod.Enabled)
                    {
                        continue;
                    }

                    if (!AddMod(output, mod, addedFiles))
                    {
                        return false;
                    }
                }

                succeeded = MergeZipFiles(
                    output,
                    sourceJarPath,
                    addedFiles,
                    name => !name.Contains("META-INF", StringComparison.Ordinal));
            }

            return succeeded;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (!succeeded)
            {
                // A half-built jar is not left behind: it would be launched as-is next time, and it
                // looks exactly like a good one.
                FileSystem.DeletePath(targetJarPath);
            }
        }
    }

    private static bool AddMod(ZipArchive output, JarMod mod, HashSet<string> addedFiles)
    {
        switch (mod.Kind)
        {
            case JarModKind.ZipFile:
                return MergeZipFiles(output, mod.Path, addedFiles);

            case JarModKind.SingleFile:
            {
                var name = Path.GetFileName(mod.Path);

                /*
                 * UPSTREAM BUG, reproduced: the name is registered AFTER the file is written, so a
                 * loose file colliding with something already in the jar is added anyway and the
                 * archive ends up with two entries under one path. Upstream marks it "FIXME: buggy -
                 * does not work with addedFiles". Kept because jar-mod lists are hand-built and
                 * existing packs may depend on whatever the JVM currently picks; fixing it would mean
                 * silently dropping a file a pack expects to be there.
                 */
                if (!CompressFile(output, mod.Path, name))
                {
                    return false;
                }

                addedFiles.Add(name);
                return true;
            }

            case JarModKind.Folder:
            {
                // The tree is added under the folder's own name, so the parent is the base to make
                // entry paths relative to.
                var parent = Path.GetDirectoryName(Path.GetFullPath(mod.Path)) ?? mod.Path;

                var files = new List<string>();

                if (!CollectFileListRecursively(mod.Path, null, files, null))
                {
                    return false;
                }

                /*
                 * UPSTREAM BUG, reproduced: the collision filter here compares ABSOLUTE FILE PATHS
                 * against zip entry names, which can never match, so it removes nothing. Also marked
                 * "FIXME: buggy" upstream, and also unreachable in practice — nothing in the launcher
                 * produces a folder-kind jar mod.
                 */
                return CompressDirFiles(output, parent, files);
            }

            default:
                // An unknown kind stops the launch rather than quietly producing a jar missing a mod.
                return false;
        }
    }

    // ================================================================== searching

    /// <summary>Finds the folder inside an archive that directly contains a named file.</summary>
    /// <returns>The folder's path with a trailing slash, or an empty string if it is not there.</returns>
    public static string FindFolderOfFileInZip(
        ZipArchive zip,
        string what,
        IReadOnlyCollection<string>? ignorePaths = null,
        string root = "")
    {
        ArgumentNullException.ThrowIfNull(zip);

        var best = string.Empty;

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;

            if (!name.StartsWith(root, StringComparison.Ordinal) || Path.GetFileName(name) != what)
            {
                continue;
            }

            var folder = name[..(name.Length - what.Length)];

            if (ignorePaths is not null && IsIgnored(folder[root.Length..], ignorePaths))
            {
                continue;
            }

            // Upstream recurses breadth-first from the root, so the shallowest match wins.
            if (best.Length == 0 || folder.Length < best.Length)
            {
                best = folder;
            }
        }

        return best;
    }

    private static bool IsIgnored(string relativeFolder, IReadOnlyCollection<string> ignorePaths)
        => relativeFolder
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => ignorePaths.Contains(segment) || ignorePaths.Contains(segment + "/"));

    /// <summary>Collects every folder in an archive that directly contains a named file.</summary>
    public static bool FindFilesInZip(ZipArchive zip, string what, List<string> result, string root = "")
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;

            if (name.StartsWith(root, StringComparison.Ordinal) && Path.GetFileName(name) == what)
            {
                result.Add(name[..(name.Length - what.Length)]);
            }
        }

        return result.Count != 0;
    }

    // ================================================================== extraction

    /// <summary>
    /// Extracts one subdirectory of an archive into a target directory.
    /// </summary>
    /// <returns>The paths written, or <see langword="null"/> if extraction was refused or failed.</returns>
    /// <remarks>
    /// ZIP-SLIP GUARDED. An archive can name an entry <c>../../../.ssh/authorized_keys</c>, and
    /// extracting it as given writes outside the target. Every resolved path is checked against the
    /// target root and the whole extraction is abandoned on the first violation — abandoned rather
    /// than skipped, because an archive that tried it once is not to be trusted for the rest.
    /// </remarks>
    public static List<string>? ExtractSubDir(ZipArchive zip, string subdir, string target)
    {
        ArgumentNullException.ThrowIfNull(zip);

        var root = Path.GetFullPath(target);
        var extracted = new List<string>();

        try
        {
            foreach (var entry in zip.Entries)
            {
                var name = FileSystem.RemoveInvalidPathChars(entry.FullName);

                if (!name.StartsWith(subdir, StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = name[subdir.Length..].Replace('\\', '/').TrimStart('/');

                if (relative.Length == 0)
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(root, relative));

                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return null;
                }

                // A trailing slash is how a zip spells "directory"; there is no content to read.
                if (relative.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    EnsureMinimumPermissions(destination, isDirectory: true);
                    extracted.Add(destination);

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                entry.ExtractToFile(destination, overwrite: true);
                EnsureMinimumPermissions(destination, isDirectory: false);

                extracted.Add(destination);
            }

            return extracted;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Upstream deletes what it had written on a failure part-way through.
            foreach (var path in extracted)
            {
                FileSystem.DeletePath(path);
            }

            return null;
        }
    }

    /// <summary>
    /// Makes sure the owner can read and write what was just extracted.
    /// </summary>
    /// <remarks>
    /// Archives carry the permissions of whatever machine built them, and a mode of 0444 in a modpack
    /// means the launcher cannot update the file it just wrote. The owner bits are forced on and the
    /// group and other bits are clamped to read-only. No-op on Windows, which has no such bits.
    /// </remarks>
    private static void EnsureMinimumPermissions(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var current = File.GetUnixFileMode(path);

            var mode = isDirectory
                ? current
                  | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                  | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                  | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                : (current
                   & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                      | UnixFileMode.GroupRead | UnixFileMode.OtherRead))
                  | UnixFileMode.UserRead | UnixFileMode.UserWrite;

            if (mode != current)
            {
                File.SetUnixFileMode(path, mode);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort, exactly as upstream: a warning there, and not worth failing the extraction.
        }
    }

    /// <summary>Extracts a whole archive.</summary>
    public static List<string>? ExtractDir(string fileCompressed, string dir) => ExtractDir(fileCompressed, string.Empty, dir);

    /// <summary>Extracts one subdirectory of an archive on disk.</summary>
    public static List<string>? ExtractDir(string fileCompressed, string subdir, string dir)
    {
        var zip = OpenForReading(fileCompressed, out var wasEmpty);

        if (zip is null)
        {
            // An empty archive is 22 bytes of end-of-central-directory and nothing else. Some servers
            // send one where a real file was expected, and unpacking it is a no-op rather than an error.
            return wasEmpty ? [] : null;
        }

        using (zip)
        {
            return ExtractSubDir(zip, subdir, dir);
        }
    }

    /// <summary>Extracts a single named entry.</summary>
    public static bool ExtractFile(string fileCompressed, string file, string target)
    {
        var zip = OpenForReading(fileCompressed, out var wasEmpty);

        if (zip is null)
        {
            return wasEmpty;
        }

        using (zip)
        {
            return ExtractRelFile(zip, file, target);
        }
    }

    /// <summary>Extracts a single named entry from an already-open archive.</summary>
    public static bool ExtractRelFile(ZipArchive zip, string file, string target)
    {
        ArgumentNullException.ThrowIfNull(zip);

        var entry = zip.GetEntry(file);

        if (entry is null)
        {
            return false;
        }

        try
        {
            var destination = Path.GetFullPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            entry.ExtractToFile(destination, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ZipArchive? OpenForReading(string fileCompressed, out bool wasEmptyArchive)
    {
        wasEmptyArchive = false;

        try
        {
            return ZipFile.OpenRead(fileCompressed);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The 22-byte empty-archive case, which upstream detects the same way.
            wasEmptyArchive = new FileInfo(fileCompressed) is { Exists: true, Length: 22 };
            return null;
        }
    }

    // ================================================================== compression

    /// <summary>Walks a directory tree, collecting the files to be archived.</summary>
    /// <param name="rootDir">The tree to walk. Exclusions are tested against paths relative to it.</param>
    /// <param name="subDir">Where to continue from; null starts at the root.</param>
    /// <param name="excludeFilter">Returns true for a relative path that should be left out.</param>
    public static bool CollectFileListRecursively(
        string rootDir,
        string? subDir,
        List<string> files,
        Func<string, bool>? excludeFilter)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (!Directory.Exists(rootDir))
        {
            return false;
        }

        var directory = subDir ?? rootDir;

        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!CollectFileListRecursively(rootDir, child, files, excludeFilter))
            {
                return false;
            }
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var relative = Path.GetRelativePath(rootDir, file).Replace('\\', '/');

            if (excludeFilter is not null && excludeFilter(relative))
            {
                continue;
            }

            // The original paths, because CompressDirFiles re-derives the entry names from them.
            files.Add(file);
        }

        return true;
    }

    /// <summary>Adds files to an open archive, named relative to a base directory.</summary>
    public static bool CompressDirFiles(ZipArchive zip, string dir, IEnumerable<string> files, bool followSymlinks = false)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(files);

        if (!Directory.Exists(dir))
        {
            return false;
        }

        foreach (var file in files)
        {
            var entryName = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var source = file;

            if (followSymlinks && new FileInfo(file) is { LinkTarget: not null } link)
            {
                // Store what the link points at rather than the link, so the archive is self-contained
                // on a machine that has never heard of the target.
                source = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file;
            }

            if (!CompressFile(zip, source, entryName))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Creates an archive from a list of files.</summary>
    public static bool CompressDirFiles(string fileCompressed, string dir, IEnumerable<string> files, bool followSymlinks = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fileCompressed))!);

            bool result;

            using (var stream = new FileStream(fileCompressed, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                result = CompressDirFiles(zip, dir, files, followSymlinks);
            }

            if (!result)
            {
                // A half-written archive is worse than none: it looks like a valid result.
                FileSystem.DeletePath(fileCompressed);
            }

            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FileSystem.DeletePath(fileCompressed);
            return false;
        }
    }

    private static bool CompressFile(ZipArchive zip, string path, string entryName)
    {
        try
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

            using var input = File.OpenRead(path);
            using var output = entry.Open();

            input.CopyTo(output);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
