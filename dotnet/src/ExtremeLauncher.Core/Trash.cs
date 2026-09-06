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
 * Ported from FS::trash, which is one line of Qt -- QFile::moveToTrash -- and has no BCL equivalent at
 * all. This file is that missing line, three times over.
 *
 * WHY IT IS WORTH THE TROUBLE: the alternative is a Delete button that destroys a modded instance,
 * with its worlds, permanently and instantly. An instance directory holds saves nobody else has a copy
 * of. Recoverable deletion is the difference between a misclick and a loss.
 *
 * IT IS ALLOWED TO FAIL, and every caller must handle that. Upstream returns false outright on Flatpak
 * and on Windows Server, and a filesystem with no trash directory it can reach is a normal state, not
 * an error -- a launcher on a network share or a FAT USB stick is exactly this lineage's use case.
 * Callers fall back to asking the user whether to delete permanently.
 */

using System.Globalization;
using System.Text;

namespace ExtremeLauncher.Core;

public static class Trash
{
    /// <summary>
    /// Moves a file or directory to the desktop trash.
    /// </summary>
    /// <param name="trashedPath">
    /// Where it went, when that is knowable, else empty. Windows does not tell us, so an in-app undo
    /// is not offered there -- the Recycle Bin's own Restore is the platform's answer and users know
    /// where to find it.
    /// </param>
    /// <returns>False when this platform or filesystem has no trash to move it to.</returns>
    public static bool TryTrash(string path, out string trashedPath)
    {
        ArgumentNullException.ThrowIfNull(path);

        trashedPath = string.Empty;

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return TryTrashWindows(path);
            }

            if (OperatingSystem.IsMacOS())
            {
                return TryTrashByMove(
                    path,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash"),
                    out trashedPath);
            }

            return OperatingSystem.IsLinux() && TryTrashFreedesktop(path, out trashedPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            // The shell refused or the user cancelled its error dialog. Nothing has moved.
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            // Upstream returns false on Windows Server and under Flatpak for the same reason: there is
            // no bin to move it to. The caller asks about deleting instead.
            return false;
        }
    }

    // ================================================================== Windows

    /*
     * NOT HAND-ROLLED INTEROP. The first version of this called SHFileOperationW through a P/Invoke of
     * my own, and crashed the test host with an access violation: SHFILEOPSTRUCT is packed on x86 and
     * naturally aligned on x64, and getting that wrong corrupts the stack of a call whose job is to
     * DELETE THINGS.
     *
     * Microsoft.VisualBasic.Core ships in the shared framework and is the same shell call with the
     * marshalling already right -- including FOF_ALLOWUNDO, which is the entire point: without it this
     * is a permanent delete rather than the Recycle Bin. An odd-looking namespace is a small price for
     * not writing my own interop on the destructive path.
     *
     * Windows does not report where the item landed, so no in-app undo is offered there. The Recycle
     * Bin's own Restore is the platform's answer and users already know where it is.
     */
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryTrashWindows(string path)
    {
        if (Directory.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }
        else
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }

        // Believed only when the original is actually gone.
        return !File.Exists(path) && !Directory.Exists(path);
    }

    // ================================================================== macOS

    /// <summary>Moves into a trash directory that already exists, giving it a free name.</summary>
    private static bool TryTrashByMove(string path, string trashDirectory, out string trashedPath)
    {
        trashedPath = string.Empty;

        if (!Directory.Exists(trashDirectory))
        {
            return false;
        }

        trashedPath = FreeName(trashDirectory, Path.GetFileName(path.TrimEnd('/', '\\')));

        Move(path, trashedPath);

        return true;
    }

    // ================================================================== Linux

    /// <summary>
    /// The freedesktop.org trash specification: <c>files/</c> holds the item and <c>info/</c> holds a
    /// <c>.trashinfo</c> recording where it came from.
    /// </summary>
    /// <remarks>
    /// BOTH HALVES OR NEITHER. A file in <c>files/</c> with no matching <c>.trashinfo</c> is an
    /// orphan: the desktop's own trash viewer shows it with no original location and cannot restore
    /// it. So the info file is written FIRST -- if that fails, nothing has been moved yet and the
    /// caller can fall back cleanly.
    ///
    /// Only the HOME trash is used. The spec also defines per-volume trash directories at the mount
    /// point, which is what makes trashing work across filesystems; without them, an instance on
    /// another volume fails here and the caller asks about deleting instead. That is the honest
    /// outcome rather than a cross-device copy the user did not ask for.
    /// </remarks>
    private static bool TryTrashFreedesktop(string path, out string trashedPath)
    {
        trashedPath = string.Empty;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (home.Length == 0)
        {
            return false;
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : Path.Combine(home, ".local", "share");

        var trash = Path.Combine(dataHome, "Trash");
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");

        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var name = Path.GetFileName(path.TrimEnd('/'));
        var target = FreeName(files, name);
        var infoFile = Path.Combine(info, Path.GetFileName(target) + ".trashinfo");

        /*
         * The spec requires the original path to be URL-encoded, and the date to be local time in
         * ISO 8601 without a zone. A viewer that cannot parse either shows the entry as unrestorable.
         */
        File.WriteAllText(
            infoFile,
            "[Trash Info]\n"
            + $"Path={EncodePath(Path.GetFullPath(path))}\n"
            + $"DeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");

        try
        {
            Move(path, target);
        }
        catch (IOException)
        {
            // Do not leave an info file describing something that is not in the trash.
            TryDelete(infoFile);
            throw;
        }

        trashedPath = target;

        return true;
    }

    /// <summary>Percent-encodes a path for a .trashinfo file, leaving the separators alone.</summary>
    private static string EncodePath(string path)
    {
        var builder = new StringBuilder(path.Length);

        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;

            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return builder.ToString();
    }

    // ================================================================== shared

    /// <summary>A name in <paramref name="directory"/> that nothing already occupies.</summary>
    /// <remarks>
    /// Deleting two instances that happen to share a name must not have the second overwrite the
    /// first inside the trash, which would destroy the very thing being preserved.
    /// </remarks>
    private static string FreeName(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);

        for (var i = 1; File.Exists(candidate) || Directory.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{name}.{i}");
        }

        return candidate;
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // Best effort: this is already the failure path.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
