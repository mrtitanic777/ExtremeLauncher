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
 * The hard-link primitive, extracted so the two callers share one declaration.
 *
 * THERE IS NO BCL EQUIVALENT of std::filesystem::create_hard_link. .NET exposes symlinks
 * (File.CreateSymbolicLink) but not hard links, so this drops to the platform call directly.
 *
 * HARD LINKS RATHER THAN SYMLINKS IS THE POINT, and it is upstream's choice in both callers. Creating
 * a symlink on Windows needs either Developer Mode or elevation; creating a hard link needs neither.
 * A launcher that asked for administrator rights to unpack a Java runtime would be a worse launcher.
 */

using System.Runtime.InteropServices;

namespace ExtremeLauncher.Core;

public static partial class NativeLink
{
    /// <summary>Creates a hard link at <paramref name="destination"/> pointing at <paramref name="source"/>.</summary>
    /// <exception cref="IOException">The platform refused, with its error code attached.</exception>
    public static void CreateHardLink(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkW(destination, source, IntPtr.Zero))
            {
                throw new IOException($"CreateHardLink failed for {destination}", Marshal.GetLastWin32Error());
            }

            return;
        }

        if (Link(source, destination) != 0)
        {
            throw new IOException($"link() failed for {destination}", Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>The same, reporting failure rather than throwing.</summary>
    public static bool TryCreateHardLink(string source, string destination)
    {
        try
        {
            CreateHardLink(source, destination);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EntryPointNotFoundException
                                      or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Note the argument order: the NEW link name comes first.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string oldPath, string newPath);
}
