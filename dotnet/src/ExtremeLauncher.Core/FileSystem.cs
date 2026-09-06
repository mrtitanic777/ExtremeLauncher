// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
 *  Copyright (c) 2023 Rachel Powers <508861+Ryex@users.noreply.github.com>
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
 * Ported from launcher/FileSystem.{h,cpp}.
 *
 * SEPARATOR CONVENTION, inherited verbatim and easy to trip over:
 *   PathCombine() returns FORWARD slashes on every platform, because it ends in QDir::cleanPath().
 *   PathTruncate() returns NATIVE separators, because it ends in a join on QDir::separator().
 * Both are covered by the ported tests. Do not "fix" one to match the other.
 *
 * NOT YET PORTED, deferred deliberately:
 *   - class copy / class clone / class create_link -- these are QObject/QThread types built on Qt
 *     signals and progress reporting. They belong with wave 2, once Task has been reshaped onto
 *     Task<T>/IProgress<T>/CancellationToken; porting them before that decision would bake in the
 *     signal-slot shape we are trying to shed.
 *   - ExternalLinkFileProcess and create_link::runPrivileged -- QLocalServer IPC with the elevated
 *     `filelink` helper. Wave 9, alongside the helper itself.
 *   - clone_file and the win_ioctl_clone / linux_ficlone / macos_bsd_clonefile trio -- per-platform
 *     reflink ioctls, each needing its own P/Invoke surface. Wave 9.
 *   - createShortcut -- .lnk via COM on Windows, .desktop on Linux, alias on macOS. Wave 9.
 *   - trash() -- QFile::moveToTrash has no BCL equivalent; needs SHFileOperation / gio / NSFileManager.
 *   - hardLinkCount() -- needs GetFileInformationByHandle on Windows; only used by the link machinery
 *     above, so it travels with it.
 *   - getPathNameInLocal8bit() / shortPathName() -- Windows 8.3 path fallback, needs GetShortPathNameW.
 */

using System.Globalization;

namespace ExtremeLauncher.Core;

public sealed class FileSystemException : LauncherException
{
    public FileSystemException(string message) : base(message)
    {
    }

    public FileSystemException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public enum FilesystemType
{
    Fat,
    Ntfs,
    Refs,
    Ext,
    Ext2Old,
    Ext234,
    Xfs,
    Btrfs,
    Nfs,
    Zfs,
    Apfs,
    Hfs,
    HfsPlus,
    HfsX,
    Fuseblk,
    F2Fs,
    Bcachefs,
    Unknown,
}

public sealed class FilesystemInfo
{
    public FilesystemType FsType { get; set; } = FilesystemType.Unknown;

    public string FsTypeName { get; set; } = string.Empty;

    /// <summary>Always -1: QStorageInfo::blockSize() has no BCL equivalent.</summary>
    public int BlockSize { get; set; } = -1;

    public long BytesAvailable { get; set; }

    public long BytesFree { get; set; }

    public long BytesTotal { get; set; }

    public string Name { get; set; } = string.Empty;

    public string RootPath { get; set; } = string.Empty;
}

public static class FileSystem
{
    private const string BadWinChars = "<>:\"|?*\r\n";
    private const string BadNtfsChars = "<>:\"|?*";
    private const string BadHfsChars = ":";
    private const string BadFilenameChars = BadWinChars + "\\/";

    /// <summary>
    /// Ordered as in the C++ enum, because the fuzzy lookup returns the first containment match and
    /// that order is therefore load-bearing. See <see cref="GetFilesystemTypeFuzzy"/>.
    /// </summary>
    private static readonly (FilesystemType Type, string[] Names)[] FilesystemTypeNames =
    [
        (FilesystemType.Fat, ["FAT"]),
        (FilesystemType.Ntfs, ["NTFS"]),
        (FilesystemType.Refs, ["REFS"]),
        (FilesystemType.Ext, ["EXT"]),
        (FilesystemType.Ext2Old, ["EXT_2_OLD", "EXT2_OLD"]),
        (FilesystemType.Ext234, ["EXT2/3/4", "EXT_2_3_4", "EXT2", "EXT3", "EXT4"]),
        (FilesystemType.Xfs, ["XFS"]),
        (FilesystemType.Btrfs, ["BTRFS"]),
        (FilesystemType.Nfs, ["NFS"]),
        (FilesystemType.Zfs, ["ZFS"]),
        (FilesystemType.Apfs, ["APFS"]),
        (FilesystemType.Hfs, ["HFS"]),
        (FilesystemType.HfsPlus, ["HFSPLUS"]),
        (FilesystemType.HfsX, ["HFSX"]),
        (FilesystemType.Fuseblk, ["FUSEBLK"]),
        (FilesystemType.F2Fs, ["F2FS"]),
        (FilesystemType.Bcachefs, ["BCACHEFS"]),
        (FilesystemType.Unknown, ["UNKNOWN"]),
    ];

    private static readonly FilesystemType[] CloneFilesystems =
    [
        FilesystemType.Btrfs, FilesystemType.Apfs, FilesystemType.Zfs,
        FilesystemType.Xfs, FilesystemType.Refs, FilesystemType.Bcachefs,
    ];

    private static readonly FilesystemType[] NonLinkFilesystems = [FilesystemType.Fat];

    private static char[] SeparatorChars => OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];

    // ================================================================== path logic

    /// <summary>
    /// Equivalent of <c>QDir::cleanPath</c>: native separators become '/', redundant separators are
    /// collapsed, and '.'/'..' are resolved lexically.
    /// </summary>
    /// <remarks>
    /// The backslash conversion is Windows-only, matching Qt -- on Unix a backslash is a legal
    /// filename character and must be left alone.
    /// </remarks>
    public static string CleanPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var normalized = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

        // Peel off a drive or UNC prefix so segment processing cannot consume it.
        var prefix = string.Empty;
        var rest = normalized;

        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            prefix = normalized[..2];
            rest = normalized[2..];
        }
        else if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            prefix = "//";
            rest = normalized[2..];
        }

        var rooted = rest.StartsWith('/');
        var resolved = new List<string>();

        foreach (var segment in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;

                case ".." when resolved.Count > 0 && resolved[^1] != "..":
                    resolved.RemoveAt(resolved.Count - 1);
                    continue;

                case ".." when rooted:
                    continue; // Cannot ascend past the root.

                default:
                    resolved.Add(segment);
                    continue;
            }
        }

        var joined = string.Join('/', resolved);
        var result = prefix + (rooted ? "/" : string.Empty) + joined;

        // Qt collapses a fully-cancelled relative path to ".".
        return result.Length == 0 ? "." : result;
    }

    public static string ToNativeSeparators(string path)
        => OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    /// <remarks>Returns forward slashes on all platforms -- see the file header.</remarks>
    public static string PathCombine(string path1, string path2)
    {
        if (string.IsNullOrEmpty(path1))
        {
            return path2;
        }

        if (string.IsNullOrEmpty(path2))
        {
            return path1;
        }

        return CleanPath(path1 + Path.DirectorySeparatorChar + path2);
    }

    public static string PathCombine(string path1, string path2, string path3)
        => PathCombine(PathCombine(path1, path2), path3);

    public static string PathCombine(string path1, string path2, string path3, string path4)
        => PathCombine(PathCombine(path1, path2, path3), path4);

    /// <summary>Equivalent of <c>QFileInfo::absolutePath()</c>: the parent directory, made absolute.</summary>
    public static string AbsolutePath(string path)
    {
        var parent = DirectoryPart(path);
        return CleanPath(Path.GetFullPath(parent));
    }

    /// <summary>"foo.txt" -&gt; 0, "bar/foo.txt" -&gt; 1, "/baz/bar/foo.txt" -&gt; 2.</summary>
    public static int PathDepth(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        var parts = SplitPath(DirectoryPart(path));

        var numParts = parts.Count;
        numParts -= parts.Count(p => p == ".");
        numParts -= parts.Count(p => p == "..") * 2;

        return numParts;
    }

    /// <summary>Cuts segments off <paramref name="path"/> until it is at most <paramref name="depth"/> deep.</summary>
    /// <remarks>Returns native separators -- see the file header.</remarks>
    public static string PathTruncate(string path, int depth)
    {
        if (string.IsNullOrEmpty(path) || depth < 0)
        {
            return string.Empty;
        }

        var truncated = DirectoryPart(path);

        if (PathDepth(truncated) > depth)
        {
            return PathTruncate(truncated, depth);
        }

        var parts = SplitPath(truncated);

        if (parts.Count > 0 && parts[0] == "." && !path.StartsWith('.'))
        {
            parts.RemoveAt(0);
        }

        if (ToNativeSeparators(path).StartsWith(Path.DirectorySeparatorChar))
        {
            parts.Insert(0, string.Empty);
        }

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>Resolves a bare name via PATH, or a relative/absolute path directly.</summary>
    /// <returns>The absolute path, or an empty string if it is not an existing executable.</returns>
    public static string ResolveExecutable(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (!path.Contains('/'))
        {
            path = FindExecutableOnPath(path) ?? path;
        }

        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return CleanPath(Path.GetFullPath(path));
    }

    /// <summary>
    /// Paths inside the current directory become relative to it; everything else becomes absolute.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var currentAbsolute = CleanPath(Directory.GetCurrentDirectory());
        var newAbsolute = CleanPath(Path.GetFullPath(path));

        if (!newAbsolute.StartsWith(currentAbsolute, StringComparison.Ordinal))
        {
            return newAbsolute;
        }

        var relative = Path.GetRelativePath(currentAbsolute, newAbsolute);
        return CleanPath(relative);
    }

    public static string RemoveInvalidFilenameChars(string value, char replaceWith = '-')
        => ReplaceChars(value, BadFilenameChars, replaceWith);

    /// <remarks>
    /// Which characters are invalid depends on the filesystem under <paramref name="path"/>, so this
    /// performs a <see cref="StatFs"/> call. On Windows the Win32-reserved set always applies.
    /// </remarks>
    public static string RemoveInvalidPathChars(string path, char replaceWith = '-')
    {
        var invalidChars = OperatingSystem.IsWindows() ? BadWinChars : string.Empty;

        invalidChars += StatFs(path).FsType switch
        {
            FilesystemType.Fat or FilesystemType.Ntfs or FilesystemType.Refs => BadNtfsChars,
            FilesystemType.Apfs or FilesystemType.Hfs or FilesystemType.HfsPlus or FilesystemType.HfsX => BadHfsChars,
            _ => string.Empty,
        };

        return invalidChars.Length == 0 ? path : ReplaceChars(path, invalidChars, replaceWith);
    }

    /// <summary>Finds a directory name derived from <paramref name="value"/> that does not yet exist.</summary>
    public static string DirNameFromString(string value, string inDir = ".")
    {
        var baseName = RemoveInvalidFilenameChars(value);
        var num = 0;
        string dirName;

        do
        {
            dirName = num == 0 ? baseName : $"{baseName}({num.ToString(CultureInfo.InvariantCulture)})";

            // If it's over 9000.
            if (num > 9000)
            {
                return string.Empty;
            }

            num++;
        }
        while (PathExists(PathCombine(inDir, dirName)));

        return dirName;
    }

    /// <summary>A '!' anywhere in the path breaks Java's jar handling.</summary>
    public static bool CheckProblematicPathJava(string folder)
        => CleanPath(Path.GetFullPath(folder)).Contains('!', StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks a non-colliding name for a resource, preferring the enabled (non-.disabled) form.</summary>
    public static string GetUniqueResourceName(string filePath)
    {
        if (!filePath.EndsWith(".disabled", StringComparison.Ordinal))
        {
            return filePath; // Prioritize enabled mods.
        }

        var enabledName = filePath[..^".disabled".Length];

        if (!File.Exists(enabledName))
        {
            return filePath;
        }

        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var directory = AbsolutePath(filePath);

        var counter = 1;
        string candidate;

        do
        {
            var suffix = counter == 1 ? ".duplicate" : $".duplicate{counter.ToString(CultureInfo.InvariantCulture)}";
            candidate = PathCombine(directory, baseName + suffix);
            counter++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    // ================================================================== file I/O

    /// <exception cref="FileSystemException">The directory could not be created.</exception>
    public static void EnsureExists(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new FileSystemException($"Unable to create folder {directory}", e);
        }
    }

    /// <summary>Writes <paramref name="data"/> atomically: to a temporary file, then renamed into place.</summary>
    /// <remarks>
    /// Stands in for QSaveFile. The temporary file is created beside the target so the final move
    /// stays on one volume and is therefore atomic; a cross-volume move would not be.
    /// </remarks>
    /// <exception cref="FileSystemException">Writing or committing failed.</exception>
    public static void Write(string filename, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var directory = DirectoryPart(filename);
        EnsureExists(directory);

        var temporary = PathCombine(directory, $".{Path.GetFileName(filename)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, filename, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new FileSystemException($"Error writing data to {filename}: {e.Message}", e);
        }
    }

    /// <summary>Read-modify-write append that keeps the atomic-commit guarantee of <see cref="Write"/>.</summary>
    /// <exception cref="FileSystemException">Writing or committing failed.</exception>
    public static void AppendSafe(string filename, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        EnsureExists(DirectoryPart(filename));

        byte[] buffer;

        try
        {
            buffer = Read(filename);
        }
        catch (FileSystemException)
        {
            buffer = [];
        }

        var combined = new byte[buffer.Length + data.Length];
        buffer.CopyTo(combined, 0);
        data.CopyTo(combined, buffer.Length);

        Write(filename, combined);
    }

    /// <summary>Plain, non-atomic append.</summary>
    /// <exception cref="FileSystemException">Writing failed.</exception>
    public static void Append(string filename, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        EnsureExists(DirectoryPart(filename));

        try
        {
            using var stream = new FileStream(filename, FileMode.Append, FileAccess.Write);
            stream.Write(data, 0, data.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FileSystemException($"Couldn't open {filename} for writing: {e.Message}", e);
        }
    }

    /// <exception cref="FileSystemException">The file could not be read.</exception>
    public static byte[] Read(string filename)
    {
        try
        {
            return File.ReadAllBytes(filename);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new FileSystemException($"Unable to open {filename} for reading: {e.Message}", e);
        }
    }

    /// <summary>Touches an existing file's last-write time.</summary>
    public static bool UpdateTimestamp(string filename)
    {
        try
        {
            File.SetLastWriteTimeUtc(filename, DateTime.UtcNow);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Creates the parent directories of a file path. The last segment is treated as a filename.</summary>
    public static bool EnsureFilePathExists(string filenamepath)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPart(filenamepath));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Creates a directory path. The last segment is treated as a directory and created.</summary>
    public static bool EnsureFolderPathExists(string folderPathName)
    {
        if (Directory.Exists(folderPathName))
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(folderPathName);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Renames, falling back to copy-then-delete when the rename fails (e.g. across volumes).</summary>
    public static bool Move(string source, string dest)
    {
        EnsureFilePathExists(dest);

        try
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, dest);
            }
            else
            {
                File.Move(source, dest, overwrite: true);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return MoveByCopy(source, dest);
        }
    }

    /// <summary>Deletes a file, or a directory and everything under it.</summary>
    public static bool DeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Overlays <paramref name="overridePath"/> onto <paramref name="overwrittenPath"/>, keeping files
    /// that exist only in the destination.
    /// </summary>
    public static bool OverrideFolder(string overwrittenPath, string overridePath)
    {
        if (!EnsureFolderPathExists(overwrittenPath))
        {
            return false;
        }

        try
        {
            CopyRecursive(overridePath, overwrittenPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string GetDesktopDir() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    // ================================================================== filesystem probing

    public static string GetFilesystemTypeName(FilesystemType type)
    {
        foreach (var (candidate, names) in FilesystemTypeNames)
        {
            if (candidate == type)
            {
                return names[0];
            }
        }

        return "UNKNOWN";
    }

    /// <summary>Exact, case-insensitive lookup of a reported filesystem name.</summary>
    public static FilesystemType GetFilesystemType(string name)
    {
        var upper = name.ToUpperInvariant();

        foreach (var (type, names) in FilesystemTypeNames)
        {
            if (names.Contains(upper, StringComparer.Ordinal))
            {
                return type;
            }
        }

        return FilesystemType.Unknown;
    }

    /// <summary>Containment lookup, returning the first entry in enum order whose name is a substring.</summary>
    /// <remarks>
    /// QUIRK, preserved: because <see cref="FilesystemType.Ext"/> precedes
    /// <see cref="FilesystemType.Ext234"/> in the enum and "EXT4" contains "EXT", a reported "EXT4"
    /// resolves to <see cref="FilesystemType.Ext"/>, not <see cref="FilesystemType.Ext234"/>. The
    /// ordering of <c>FilesystemTypeNames</c> is therefore load-bearing; do not sort it.
    /// </remarks>
    public static FilesystemType GetFilesystemTypeFuzzy(string name)
    {
        var upper = name.ToUpperInvariant();

        foreach (var (type, names) in FilesystemTypeNames)
        {
            foreach (var candidate in names)
            {
                if (upper.Contains(candidate, StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }

        return FilesystemType.Unknown;
    }

    /// <summary>Walks up until an existing directory is found.</summary>
    public static string NearestExistentAncestor(string path)
    {
        if (PathExists(path))
        {
            return path;
        }

        string current;

        try
        {
            current = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }

        while (true)
        {
            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                return Directory.Exists(current) ? current : string.Empty;
            }

            current = parent;

            if (Directory.Exists(current))
            {
                return current;
            }
        }
    }

    public static FilesystemInfo StatFs(string path)
    {
        var info = new FilesystemInfo();
        var ancestor = NearestExistentAncestor(path);

        if (string.IsNullOrEmpty(ancestor))
        {
            return info;
        }

        var drive = FindDrive(ancestor);

        if (drive is null)
        {
            return info;
        }

        try
        {
            info.FsTypeName = drive.DriveFormat;
            info.FsType = GetFilesystemTypeFuzzy(info.FsTypeName);
            info.BytesAvailable = drive.AvailableFreeSpace;
            info.BytesFree = drive.TotalFreeSpace;
            info.BytesTotal = drive.TotalSize;
            info.RootPath = drive.RootDirectory.FullName;
            info.Name = drive.VolumeLabel;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DriveNotFoundException)
        {
            // Leave whatever was already filled in; upstream tolerates partial QStorageInfo too.
        }

        return info;
    }

    public static bool CanCloneOnFs(string path) => CanCloneOnFs(StatFs(path));

    public static bool CanCloneOnFs(FilesystemInfo info) => CanCloneOnFs(info.FsType);

    public static bool CanCloneOnFs(FilesystemType type) => CloneFilesystems.Contains(type);

    /// <summary>Both ends must be clone-capable and on the same device.</summary>
    public static bool CanClone(string src, string dst)
    {
        var source = StatFs(src);
        var destination = StatFs(dst);

        var sameDevice = string.Equals(source.RootPath, destination.RootPath, StringComparison.Ordinal);

        return sameDevice && CanCloneOnFs(source) && CanCloneOnFs(destination);
    }

    public static bool CanLinkOnFs(string path) => CanLinkOnFs(StatFs(path));

    public static bool CanLinkOnFs(FilesystemInfo info) => CanLinkOnFs(info.FsType);

    public static bool CanLinkOnFs(FilesystemType type) => !NonLinkFilesystems.Contains(type);

    /// <summary>Both ends must be link-capable and on the same device.</summary>
    public static bool CanLink(string src, string dst)
    {
        var source = StatFs(src);
        var destination = StatFs(dst);

        var sameDevice = string.Equals(source.RootPath, destination.RootPath, StringComparison.Ordinal);

        return sameDevice && CanLinkOnFs(source) && CanLinkOnFs(destination);
    }

    // ================================================================== internals

    /// <summary>Equivalent of <c>QFileInfo::path()</c>: everything before the last separator.</summary>
    private static string DirectoryPart(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return ".";
        }

        var index = path.LastIndexOfAny(SeparatorChars);

        if (index < 0)
        {
            return ".";
        }

        if (index == 0)
        {
            return "/";
        }

        // "C:/foo.txt" -> "C:/", not "C:".
        if (index == 2 && path.Length > 2 && path[1] == ':')
        {
            return path[..3];
        }

        return path[..index];
    }

    /// <summary>Splits on either separator, dropping empty segments.</summary>
    private static List<string> SplitPath(string path)
        => [.. path.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries)];

    private static string ReplaceChars(string value, string invalidChars, char replaceWith)
    {
        var buffer = value.ToCharArray();

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] < ' ' || invalidChars.Contains(buffer[i], StringComparison.Ordinal))
            {
                buffer[i] = replaceWith;
            }
        }

        return new string(buffer);
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }

    private static bool MoveByCopy(string source, string dest)
    {
        try
        {
            if (Directory.Exists(source))
            {
                CopyRecursive(source, dest, overwrite: true);
            }
            else
            {
                EnsureExists(DirectoryPart(dest));
                File.Copy(source, dest, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return DeletePath(source);
    }

    private static void CopyRecursive(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyRecursive(directory, Path.Combine(destination, Path.GetFileName(directory)), overwrite);
        }
    }

    private static string? FindExecutableOnPath(string name)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), name + extension);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static DriveInfo? FindDrive(string path)
    {
        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        DriveInfo? best = null;

        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;

            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                root = drive.RootDirectory.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Longest matching mount point wins, which matters on Unix where mounts nest.
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (best is null || root.Length > best.RootDirectory.FullName.Length))
            {
                best = drive;
            }
        }

        return best;
    }
}
