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
 * Ported from launcher/tools/{MCEditTool,JProfiler,JVisualVM,GenericProfiler}. Upstream wraps each
 * external tool in a QObject that owns a QProcess and talks over signals; almost all of that is glue.
 * The parts worth porting -- and the only parts that can be tested without launching a real profiler
 * -- are pure: does a path look like a valid install, where inside it is the runnable file, and what
 * arguments does the tool take to attach to a running game. Those are here as static helpers; the
 * process launching and the settings plumbing belong with the runtime that calls them.
 *
 * The OS matters to path resolution (a .exe on Windows, a shell script on Linux, an .app bundle on
 * macOS), so the resolvers take a ToolPlatform and default to the host. That is also what lets a
 * Windows test check the Linux resolution and vice versa.
 */

using System.Runtime.InteropServices;

namespace ExtremeLauncher.Launch;

/// <summary>The OS an external-tool path is being resolved for.</summary>
public enum ToolPlatform
{
    Windows,
    MacOs,
    Linux,
}

/// <summary>The host OS, so callers can resolve tool paths for the machine they run on.</summary>
public static class ToolPlatformInfo
{
    public static ToolPlatform Current
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ToolPlatform.Windows;
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ToolPlatform.MacOs : ToolPlatform.Linux;
        }
    }
}

/// <summary>
/// MCEdit, a world editor launched against an instance's saves. Ported from tools/MCEditTool. The
/// install is a directory; which file inside it is runnable depends on the OS.
/// </summary>
public static class McEditTool
{
    /// <summary>
    /// Whether <paramref name="toolPath"/> looks like an MCEdit install, and if not, why. The markers
    /// are OS-independent — upstream accepts any of them regardless of platform — so a directory
    /// carrying a build for another OS still validates.
    /// </summary>
    public static bool Check(string toolPath, out string error)
    {
        if (string.IsNullOrEmpty(toolPath))
        {
            error = "Path is empty";
            return false;
        }

        if (!Directory.Exists(toolPath))
        {
            error = "Path does not exist";
            return false;
        }

        string[] markers = ["mcedit.sh", "mcedit.py", "mcedit.exe", "Contents", "mcedit2.exe"];

        if (!markers.Any(marker => Exists(toolPath, marker)))
        {
            error = "Path does not seem to be a MCEdit path";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The runnable file inside an MCEdit install for the given OS, or empty when none is present. On
    /// macOS the install path is itself the thing to open (an .app bundle), so it is returned as-is.
    /// </summary>
    public static string GetProgramPath(string toolPath, ToolPlatform platform = ToolPlatform.Linux)
    {
        if (platform == ToolPlatform.MacOs)
        {
            return toolPath;
        }

        if (platform == ToolPlatform.Windows)
        {
            if (Exists(toolPath, "mcedit.exe"))
            {
                return Path.Combine(toolPath, "mcedit.exe");
            }

            return Exists(toolPath, "mcedit2.exe") ? Path.Combine(toolPath, "mcedit2.exe") : string.Empty;
        }

        // Linux and the BSDs: a shell launcher, or the Python entry point.
        if (Exists(toolPath, "mcedit.sh"))
        {
            return Path.Combine(toolPath, "mcedit.sh");
        }

        return Exists(toolPath, "mcedit.py") ? Path.Combine(toolPath, "mcedit.py") : string.Empty;
    }

    private static bool Exists(string dir, string name)
        => File.Exists(Path.Combine(dir, name)) || Directory.Exists(Path.Combine(dir, name));
}

/// <summary>
/// JProfiler, a commercial Java profiler attached via its jpenable helper. Ported from tools/JProfiler.
/// </summary>
public static class JProfilerTool
{
    /// <summary>
    /// Whether <paramref name="path"/> is a JProfiler install: it must have a <c>bin</c> holding the
    /// jprofiler binary (either name) and <c>agent.jar</c>.
    /// </summary>
    public static bool Check(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return false;
        }

        var bin = Path.Combine(path, "bin");

        if (!Directory.Exists(bin))
        {
            return false;
        }

        var hasBinary = File.Exists(Path.Combine(bin, "jprofiler")) || File.Exists(Path.Combine(bin, "jprofiler.exe"));

        return hasBinary && File.Exists(Path.Combine(bin, "agent.jar"));
    }

    /// <summary>The jpenable program inside a JProfiler install, for the given OS.</summary>
    public static string ProgramPath(string basePath, ToolPlatform platform = ToolPlatform.Linux)
        => Path.Combine(basePath, "bin", platform == ToolPlatform.Windows ? "jpenable.exe" : "jpenable");

    /// <summary>The jpenable arguments that attach it, headless-GUI, to a running game on a port.</summary>
    public static string[] BuildArguments(int pid, int port)
        => ["-d", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--gui",
            "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)];
}

/// <summary>
/// VisualVM, the JDK's bundled profiler. Ported from tools/JVisualVM. Here the path is the executable
/// itself, not a directory.
/// </summary>
public static class JVisualVmTool
{
    /// <summary>
    /// Whether <paramref name="path"/> is a usable VisualVM: an executable file whose name contains
    /// "visualvm" (covering both <c>visualvm</c> and <c>jvisualvm</c>).
    /// </summary>
    public static bool Check(string path, ToolPlatform platform = ToolPlatform.Linux)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);

        return name.Contains("visualvm", StringComparison.Ordinal) && IsExecutable(path, platform);
    }

    /// <summary>The VisualVM arguments that open it on a running game's process id.</summary>
    public static string[] BuildArguments(int pid)
        => ["--openpid", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary>
    /// Whether a file is executable, the way upstream's QFileInfo::isExecutable decides: on Windows by
    /// extension, on Unix by the execute permission bit.
    /// </summary>
    private static bool IsExecutable(string path, ToolPlatform platform)
    {
        if (platform == ToolPlatform.Windows)
        {
            var extension = Path.GetExtension(path);

            return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".com", StringComparison.OrdinalIgnoreCase);
        }

        var mode = File.GetUnixFileMode(path);

        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}
