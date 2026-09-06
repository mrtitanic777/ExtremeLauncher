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
 * Ported from the FS::clone class and FS::clone_file in launcher/FileSystem.{h,cpp}.
 *
 * THE THIRD WAY TO DUPLICATE AN INSTANCE, and the best one where it works. A reflink -- copy-on-write
 * clone -- makes a real, independent file that shares its blocks with the original until one of them
 * is written to. Editing the copy does not touch the original, unlike a link, and it costs nothing
 * until it does, unlike a copy. The filesystem has to support it, which is why it is offered rather
 * than assumed.
 *
 * THREE DIFFERENT SYSCALLS FOR ONE IDEA: ioctl(FICLONE) on Linux, clonefile() on macOS, and
 * FSCTL_DUPLICATE_EXTENTS_TO_FILE on Windows ReFS. Nothing in .NET abstracts over them.
 *
 * ═══ VERIFICATION STATUS, stated plainly ═══
 * The traversal, the same-filesystem precondition and the fallback decision are tested and run. THE
 * THREE CLONE SYSCALLS ARE NOT: this port has only ever executed on Windows with NTFS, where reflinks
 * do not exist, so every one of those paths is unexercised code written from the platform
 * documentation. They must be checked on btrfs, XFS, APFS and ReFS before anyone relies on them.
 * `CanClone` returns false here, so the only path that actually runs is the refusal -- which is
 * tested.
 */

using System.Runtime.InteropServices;

namespace ExtremeLauncher.Core;

/// <summary>Copy-on-write cloning, where the filesystem provides it.</summary>
public sealed class FileClone
{
    private readonly string _source;
    private readonly string _destination;

    private IPathMatcher? _matcher;
    private bool _whitelist;

    public FileClone(string source, string destination)
    {
        _source = source;
        _destination = destination;
    }

    public FileClone Matcher(IPathMatcher? matcher)
    {
        _matcher = matcher;

        return this;
    }

    public FileClone Whitelist(bool whitelist)
    {
        _whitelist = whitelist;

        return this;
    }

    public int TotalCloned { get; private set; }

    public List<string> Failed { get; } = [];

    /// <summary>The reason cloning is unavailable, or an empty string.</summary>
    public string FailReason { get; private set; } = string.Empty;

    public event EventHandler<LinkPair>? FileCloned;

    /// <summary>
    /// Clones the tree.
    /// </summary>
    /// <param name="dryRun">Count what would be cloned without writing anything.</param>
    /// <returns>Whether every file was cloned.</returns>
    /// <remarks>
    /// THE PRECONDITION IS CHECKED ONCE, UP FRONT, rather than per file. A reflink cannot cross
    /// filesystems, so if the pair is unsuitable it is unsuitable for every file -- and finding that
    /// out on file one of three hundred, after having cloned none of them, is the same answer sooner.
    /// </remarks>
    public bool Run(bool dryRun = false, string offset = "")
    {
        TotalCloned = 0;
        FailReason = string.Empty;
        Failed.Clear();

        var source = FileSystem.PathCombine(_source, offset);
        var destination = FileSystem.PathCombine(_destination, offset);

        if (!dryRun && !CanCloneBetween(source, destination, out var reason))
        {
            FailReason = reason;

            return false;
        }

        if (File.Exists(source))
        {
            CloneOne(source, string.Empty, destination, dryRun);

            return Failed.Count == 0;
        }

        if (!Directory.Exists(source))
        {
            return true;
        }

        var root = Path.GetFullPath(source);

        // Files only, as FileCopy does: directories exist because a file inside needed a parent.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            CloneOne(file, Path.GetRelativePath(root, file), destination, dryRun);
        }

        return Failed.Count == 0;
    }

    /// <summary>Whether a reflink between these two paths is possible at all.</summary>
    /// <remarks>
    /// The destination may not exist yet, so its nearest existing ancestor is what gets asked -- a
    /// staging directory is created empty and cloned into.
    /// </remarks>
    public static bool CanCloneBetween(string source, string destination, out string reason)
    {
        var destinationProbe = NearestExisting(destination);

        if (destinationProbe is null)
        {
            reason = "The destination does not exist yet and has no existing parent.";

            return false;
        }

        if (!FileSystem.CanClone(source, destinationProbe))
        {
            /*
             * Both halves matter and the message says which: a reflink needs a filesystem that
             * supports it AND both ends on the same one. Upstream logs "reflink/clone must be to the
             * same device and filesystem"; naming the actual filesystems is what lets a user tell
             * "wrong disk" from "wrong filesystem".
             */
            reason = $"Cannot reflink from {FileSystem.StatFs(source).FsType} to "
                + $"{FileSystem.StatFs(destinationProbe).FsType}: both ends must be the same "
                + "copy-on-write filesystem on the same device.";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    private static string? NearestExisting(string path)
    {
        var current = Path.GetFullPath(path);

        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);

            if (parent is null || parent == current)
            {
                return null;
            }

            current = parent;
        }

        return current;
    }

    private void CloneOne(string sourcePath, string relativePath, string destinationRoot, bool dryRun)
    {
        if (_matcher is not null && _matcher.Matches(relativePath.Replace('\\', '/')) != _whitelist)
        {
            return;
        }

        var destinationPath = relativePath.Length == 0
            ? destinationRoot
            : FileSystem.PathCombine(destinationRoot, relativePath);

        if (!dryRun)
        {
            try
            {
                FileSystem.EnsureFilePathExists(destinationPath);

                CloneFile(sourcePath, destinationPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                        or PlatformNotSupportedException or LauncherException)
            {
                Failed.Add(destinationPath);

                if (FailReason.Length == 0)
                {
                    FailReason = e.Message;
                }

                return;
            }
        }

        TotalCloned++;
        FileCloned?.Invoke(this, new LinkPair(sourcePath, destinationPath));
    }

    /// <summary>
    /// Clones one file, block-sharing it with the original.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">On a platform with no reflink call.</exception>
    /// <remarks>
    /// UNVERIFIED. See the note at the top of this file: none of these three paths has been executed,
    /// because the only machine this port has run on is Windows with NTFS.
    /// </remarks>
    public static void CloneFile(string source, string destination)
    {
        if (OperatingSystem.IsLinux())
        {
            LinuxFiclone(source, destination);

            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            MacosClonefile(source, destination);

            return;
        }

        /*
         * Windows reflinks exist only on ReFS, and on btrfs through a third-party driver. Upstream
         * points at github.com/maharmstone/btrfs for the latter. Not implemented rather than
         * implemented untested: FSCTL_DUPLICATE_EXTENTS_TO_FILE has alignment and file-size
         * preconditions that are easy to get subtly wrong, and a subtly wrong reflink produces a file
         * that looks right and is not. CanClone already returns false on NTFS, so nothing reaches here
         * on an ordinary Windows machine.
         */
        throw new PlatformNotSupportedException(
            "Copy-on-write cloning is not implemented on this platform. Use a copy or a link instead.");
    }

    // ================================================================== Linux

    private const int FiClone = 0x40049409;

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int LinuxIoctl(int fd, ulong request, int argument);

    private static void LinuxFiclone(string source, string destination)
    {
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destinationStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        var sourceHandle = sourceStream.SafeFileHandle.DangerousGetHandle().ToInt32();
        var destinationHandle = destinationStream.SafeFileHandle.DangerousGetHandle().ToInt32();

        if (LinuxIoctl(destinationHandle, FiClone, sourceHandle) != 0)
        {
            throw new IOException(
                $"FICLONE failed with errno {Marshal.GetLastWin32Error()}.",
                Marshal.GetLastWin32Error());
        }
    }

    // ================================================================== macOS

    /// <summary>CLONE_NOFOLLOW is 1; upstream passes 0, following symlinks.</summary>
    private const int CloneFlagsNone = 0;

    [DllImport("libSystem.dylib", SetLastError = true, EntryPoint = "clonefile")]
    private static extern int MacosCloneFile(string source, string destination, int flags);

    private static void MacosClonefile(string source, string destination)
    {
        // clonefile refuses an existing destination, unlike every other call here.
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        if (MacosCloneFile(source, destination, CloneFlagsNone) != 0)
        {
            throw new IOException(
                $"clonefile failed with errno {Marshal.GetLastWin32Error()}.",
                Marshal.GetLastWin32Error());
        }
    }
}
