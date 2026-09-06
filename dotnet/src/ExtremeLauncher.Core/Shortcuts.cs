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
 * Ported from FS::createShortcut in launcher/FileSystem.cpp.
 *
 * A SHORTCUT THAT STARTS ONE INSTANCE. It works because `--launch <id>` has been a supported command
 * line since wave 10 and is tested: the shortcut is only a file that types it for you. That is also
 * why this is worth having rather than a novelty -- it is the difference between "open the launcher,
 * find the instance, press play" and "double-click the thing on your desktop".
 *
 * THREE PLATFORMS, THREE ENTIRELY DIFFERENT FILES, which is upstream's shape too:
 *
 *   Linux    a .desktop file, marked executable
 *   macOS    a .app bundle -- directories, an Info.plist, and a Run.command inside
 *   Windows  a .lnk, which is a binary format nothing writes by hand
 *
 * THE WINDOWS ONE GOES THROUGH THE SHELL rather than hand-rolled COM interop. Upstream uses IShellLink
 * directly, which in C# means P/Invoke against a COM interface -- and an earlier wave of this port
 * hand-rolled a P/Invoke for SHFileOperation that crashed the test host outright with an access
 * violation, because the struct packing was wrong on x64. WScript.Shell does the same job through a
 * mechanism that cannot corrupt this process's memory, and the cost is one short-lived subprocess on
 * a button nobody presses twice.
 */

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ExtremeLauncher.Core;

public static class Shortcuts
{
    /// <summary>Where a desktop shortcut goes on this machine.</summary>
    public static string DesktopDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// The file name a shortcut for this name would get, without a directory.
    /// </summary>
    /// <remarks>
    /// The extension is the platform's, which is also how the caller can show the user what is about
    /// to appear rather than just saying "a shortcut".
    /// </remarks>
    public static string FileNameFor(string name)
    {
        var stem = name;

        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(bad, '-');
        }

        if (stem.Trim().Length == 0)
        {
            stem = "instance";
        }

        return stem + (OperatingSystem.IsWindows() ? ".lnk" : OperatingSystem.IsMacOS() ? ".app" : ".desktop");
    }

    /// <summary>
    /// Writes a shortcut that runs a program with arguments.
    /// </summary>
    /// <param name="destination">The full path to write, extension included.</param>
    /// <param name="iconPath">An icon file, or empty for none. Ignored where the platform cannot use it.</param>
    /// <returns>An empty string on success, or why it failed.</returns>
    public static string Create(
        string destination,
        string target,
        IReadOnlyList<string> arguments,
        string name,
        string iconPath = "")
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (destination.Length == 0 || target.Length == 0)
        {
            return "No destination or target was given.";
        }

        if (!File.Exists(target))
        {
            // Checked here rather than left to fail later: a shortcut to a program that is not there
            // is a file that does nothing when double-clicked, with no clue why.
            return $"There is no program at {target}.";
        }

        try
        {
            FileSystem.EnsureFilePathExists(destination);

            if (OperatingSystem.IsWindows())
            {
                return CreateWindowsLink(destination, target, arguments, iconPath);
            }

            return OperatingSystem.IsMacOS()
                ? CreateMacApplication(destination, target, arguments, name)
                : CreateDesktopEntry(destination, target, arguments, name, iconPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return e.Message;
        }
    }

    // ================================================================== Linux

    /// <summary>
    /// A freedesktop .desktop entry, in upstream's exact shape.
    /// </summary>
    /// <remarks>
    /// The Categories line is upstream's too. It is what puts the shortcut under Games in a desktop
    /// menu rather than in the "Other" bucket nobody looks in.
    /// </remarks>
    private static string CreateDesktopEntry(
        string destination,
        string target,
        IReadOnlyList<string> arguments,
        string name,
        string iconPath)
    {
        var entry = new StringBuilder();

        entry.AppendLine("[Desktop Entry]");
        entry.AppendLine("Type=Application");
        entry.AppendLine("Categories=Game;ActionGame;AdventureGame;Simulation");

        // Single-quoted, as upstream does: an instance id or a data directory can contain spaces.
        var argumentText = arguments.Count == 0
            ? string.Empty
            : " '" + string.Join("' '", arguments) + "'";

        entry.AppendLine($"Exec=\"{target}\"{argumentText}");
        entry.AppendLine($"Name={name}");

        if (iconPath.Length != 0)
        {
            entry.AppendLine($"Icon={iconPath}");
        }

        File.WriteAllText(destination, entry.ToString());

        MakeExecutable(destination);

        return string.Empty;
    }

    // ================================================================== macOS

    /// <summary>
    /// A minimal .app bundle: Contents/MacOS/Run.command plus an Info.plist.
    /// </summary>
    /// <remarks>
    /// A bare shell script would be simpler and would not appear in Launchpad, get an icon, or behave
    /// like an application when double-clicked -- which is the entire point of making one.
    /// </remarks>
    private static string CreateMacApplication(
        string destination,
        string target,
        IReadOnlyList<string> arguments,
        string name)
    {
        if (Directory.Exists(destination))
        {
            return "An application of that name already exists.";
        }

        var contents = FileSystem.PathCombine(destination, "Contents");
        var binaries = FileSystem.PathCombine(contents, "MacOS");

        Directory.CreateDirectory(FileSystem.PathCombine(contents, "Resources"));
        Directory.CreateDirectory(binaries);

        var command = FileSystem.PathCombine(binaries, "Run.command");

        var script = new StringBuilder();

        script.AppendLine("#!/bin/bash");
        script.AppendLine($"\"{target}\" {string.Join(" ", arguments.Select(Quote))}");

        File.WriteAllText(command, script.ToString());

        MakeExecutable(command);

        File.WriteAllText(
            FileSystem.PathCombine(contents, "Info.plist"),
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <!DOCTYPE plist PUBLIC "-//Apple Computer//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
             <plist version="1.0">
             <dict>
                 <key>CFBundleExecutable</key>
                 <string>Run.command</string>
                 <key>CFBundleIconFile</key>
                 <string>Icon.icns</string>
                 <key>CFBundleName</key>
                 <string>{name}</string>
                 <key>CFBundlePackageType</key>
                 <string>APPL</string>
                 <key>CFBundleShortVersionString</key>
                 <string>1.0</string>
                 <key>CFBundleVersion</key>
                 <string>1.0</string>
             </dict>
             </plist>
             """);

        return string.Empty;
    }

    // ================================================================== Windows

    /// <summary>
    /// A .lnk, written by the shell rather than by this process.
    /// </summary>
    /// <remarks>
    /// See the file header for why this is not COM interop. The script is fixed text with the values
    /// passed through single-quoted PowerShell literals, and a literal quote is doubled -- so a path
    /// containing one cannot end the string and become script.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string CreateWindowsLink(
        string destination,
        string target,
        IReadOnlyList<string> arguments,
        string iconPath)
    {
        var script = new StringBuilder();

        script.Append("$s=(New-Object -ComObject WScript.Shell).CreateShortcut(");
        script.Append(PowerShellLiteral(Path.GetFullPath(destination)));
        script.Append(");$s.TargetPath=");
        script.Append(PowerShellLiteral(Path.GetFullPath(target)));
        script.Append(";$s.Arguments=");
        script.Append(PowerShellLiteral(string.Join(" ", arguments.Select(Quote))));
        script.Append(";$s.WorkingDirectory=");
        script.Append(PowerShellLiteral(Path.GetDirectoryName(Path.GetFullPath(target)) ?? string.Empty));

        if (iconPath.Length != 0 && File.Exists(iconPath))
        {
            script.Append(";$s.IconLocation=");
            script.Append(PowerShellLiteral(Path.GetFullPath(iconPath)));
        }

        script.Append(";$s.Save()");

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script.ToString());

        using var process = Process.Start(start);

        if (process is null)
        {
            return "Could not run powershell to create the shortcut.";
        }

        var error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(destination))
        {
            return error.Length != 0 ? error.Trim() : "The shortcut could not be created.";
        }

        return string.Empty;
    }

    /// <summary>A single-quoted PowerShell literal, with any quote doubled.</summary>
    private static string PowerShellLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // ================================================================== shared

    /// <summary>Quotes an argument if it needs it, for a shell command line.</summary>
    private static string Quote(string argument)
        => argument.Contains(' ', StringComparison.Ordinal) ? "\"" + argument + "\"" : argument;

    /// <summary>
    /// Marks a file runnable.
    /// </summary>
    /// <remarks>
    /// A .desktop file without the execute bit is refused by every modern desktop with a warning
    /// about untrusted launchers, which reads as the launcher having produced something suspicious.
    /// </remarks>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);

            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A filesystem with no permission bits is not a reason to lose the shortcut.
        }
    }
}
