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
 * Ported from launcher/java/JavaUtils.{h,cpp}.
 *
 * JVM discovery. Upstream is four #ifdef'd copies of FindJavaPaths(), one per platform; here that is
 * one method dispatching on OperatingSystem, which keeps the vendor list in a single readable place.
 *
 * On Windows the bulk of the work is registry scanning: every JDK vendor writes its install path
 * somewhere under HKCU/HKLM, and both the 32- and 64-bit views must be read separately.
 * KEY_WOW64_64KEY / KEY_WOW64_32KEY map onto RegistryView.Registry64 / Registry32.
 *
 * NOT PORTED here:
 *   - JavaChecker        -- spawns a bundled JavaCheck.jar to interrogate a JVM. Needs the jar as an
 *                           embedded resource plus process plumbing; it is a Task, so it fits the
 *                           wave-2 model, but the jar has to come across first.
 *   - JavaInstallList    -- a QAbstractListModel. UI wave.
 *   - JavaMetadata       -- describes downloadable runtimes; belongs with the meta index (wave 5).
 *   - java/download/*    -- ArchiveDownloadTask / ManifestDownloadTask / SymlinkTask, which need the
 *                           meta index and a BuildConfig equivalent.
 */

using System.Runtime.Versioning;
using ExtremeLauncher.Core;
using Microsoft.Win32;

namespace ExtremeLauncher.Java;

public static class JavaUtils
{
    /// <summary>Registry locations every known JDK vendor writes its install path to.</summary>
    /// <remarks>Kept in upstream's order, which is roughly "most likely to be a real install" first.</remarks>
    private static readonly (string Key, string ValueName, string SubkeySuffix)[] WindowsRegistryLocations =
    [
        // Oracle, pre-Java 9.
        (@"SOFTWARE\JavaSoft\Java Runtime Environment", "JavaHome", ""),
        (@"SOFTWARE\JavaSoft\Java Development Kit", "JavaHome", ""),

        // Oracle, Java 9 and newer.
        (@"SOFTWARE\JavaSoft\JRE", "JavaHome", ""),
        (@"SOFTWARE\JavaSoft\JDK", "JavaHome", ""),

        // AdoptOpenJDK.
        (@"SOFTWARE\AdoptOpenJDK\JRE", "Path", @"\hotspot\MSI"),
        (@"SOFTWARE\AdoptOpenJDK\JDK", "Path", @"\hotspot\MSI"),

        // Eclipse Foundation / Adoptium (AdoptOpenJDK's successors).
        (@"SOFTWARE\Eclipse Foundation\JDK", "Path", @"\hotspot\MSI"),
        (@"SOFTWARE\Eclipse Adoptium\JRE", "Path", @"\hotspot\MSI"),
        (@"SOFTWARE\Eclipse Adoptium\JDK", "Path", @"\hotspot\MSI"),

        // IBM Semeru (OpenJ9, not HotSpot).
        (@"SOFTWARE\Semeru\JRE", "Path", @"\openj9\MSI"),
        (@"SOFTWARE\Semeru\JDK", "Path", @"\openj9\MSI"),

        // Microsoft Build of OpenJDK.
        (@"SOFTWARE\Microsoft\JDK", "Path", @"\hotspot\MSI"),

        // Azul Zulu.
        (@"SOFTWARE\Azul Systems\Zulu", "InstallationPath", ""),

        // BellSoft Liberica.
        (@"SOFTWARE\BellSoft\Liberica", "InstallationPath", ""),
    ];

    /// <summary>Legacy Oracle install locations that predate consistent registry entries.</summary>
    private static readonly string[] WindowsLegacyPaths =
    [
        "C:/Program Files/Java/jre8/bin/javaw.exe",
        "C:/Program Files/Java/jre7/bin/javaw.exe",
        "C:/Program Files/Java/jre6/bin/javaw.exe",
        "C:/Program Files (x86)/Java/jre8/bin/javaw.exe",
        "C:/Program Files (x86)/Java/jre7/bin/javaw.exe",
        "C:/Program Files (x86)/Java/jre6/bin/javaw.exe",
    ];

    private static readonly string[] MacPaths =
    [
        "/Applications/Xcode.app/Contents/Applications/Application Loader.app/Contents/MacOS/itms/java/bin/java",
        "/Library/Internet Plug-Ins/JavaAppletPlugin.plugin/Contents/Home/bin/java",
        "/System/Library/Frameworks/JavaVM.framework/Versions/Current/Commands/java",
    ];

    private static readonly string[] LinuxSearchRoots =
    [
        "/usr/lib/jvm",
        "/usr/lib32/jvm",
        "/usr/lib64/jvm",
        "/opt/jdk",
        "/opt/jdks",
        "/app/jdk",
    ];

    /// <summary>Environment variable holding extra JVM paths, separated like PATH.</summary>
    public const string ExtraPathsVariable = "EXTREMELAUNCHER_JAVA_PATHS";

    /// <summary>The interpreter filename: <c>javaw.exe</c> on Windows, <c>java</c> elsewhere.</summary>
    public static string JavaExecutable => OperatingSystem.IsWindows() ? "javaw.exe" : "java";

    /// <summary>Whatever the PATH resolves to, used as a last-resort candidate.</summary>
    public static JavaInstall GetDefaultJava()
        => new("java", "unknown", OperatingSystem.IsWindows() ? "javaw" : "java");

    /// <summary>
    /// Every plausible JVM path on this machine, deduplicated and in preference order.
    /// </summary>
    /// <remarks>
    /// Deliberately does not check that the paths exist or work -- upstream hands the whole list to
    /// JavaChecker, which probes each one. Keeping that split means discovery stays cheap and
    /// testable while validation stays where the process spawning lives.
    /// </remarks>
    public static IReadOnlyList<string> FindJavaPaths()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // 64-bit before 32-bit, so a 64-bit JVM is preferred when both are installed.
            candidates.AddRange(FindJavaFromRegistry(RegistryView.Registry64));
            candidates.AddRange(FindJavaFromRegistry(RegistryView.Registry32));
            candidates.AddRange(WindowsLegacyPaths);
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.AddRange(MacPaths);
            candidates.AddRange(ScanForJvmRoots("/Library/Java/JavaVirtualMachines", "Contents/Home/bin/java"));
        }
        else
        {
            foreach (var root in LinuxSearchRoots)
            {
                candidates.AddRange(ScanForJvmRoots(root, "bin/java"));
                candidates.AddRange(ScanForJvmRoots(root, "jre/bin/java"));
            }
        }

        candidates.Add(GetDefaultJava().Path);
        candidates.AddRange(GetJavaPathsFromEnvironment());

        return Deduplicate(candidates);
    }

    /// <summary>
    /// Extra JVM paths from <see cref="ExtraPathsVariable"/>, plus every PATH entry with the
    /// interpreter name appended.
    /// </summary>
    public static IReadOnlyList<string> GetJavaPathsFromEnvironment()
    {
        var result = new List<string>();

        var extra = Environment.GetEnvironmentVariable(ExtraPathsVariable);

        if (!string.IsNullOrEmpty(extra))
        {
            result.AddRange(
                extra.Replace('\\', '/').Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        var path = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrEmpty(path))
        {
            foreach (var entry in path.Replace('\\', '/').Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(FileSystem.PathCombine(entry.Trim('"'), JavaExecutable));
            }
        }

        return result;
    }

    /// <summary>Reads every vendor's install path out of one registry view.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> FindJavaFromRegistry(RegistryView view)
    {
        var result = new List<string>();

        foreach (var (key, valueName, subkeySuffix) in WindowsRegistryLocations)
        {
            foreach (var hive in (ReadOnlySpan<RegistryHive>)[RegistryHive.CurrentUser, RegistryHive.LocalMachine])
            {
                result.AddRange(ReadJavaHomes(hive, view, key, valueName, subkeySuffix));
            }
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadJavaHomes(
        RegistryHive hive,
        RegistryView view,
        string keyName,
        string valueName,
        string subkeySuffix)
    {
        var result = new List<string>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var vendorKey = baseKey.OpenSubKey(keyName);

            if (vendorKey is null)
            {
                return result;
            }

            // Each installed version is its own subkey; the install path hangs off that.
            foreach (var versionName in vendorKey.GetSubKeyNames())
            {
                using var versionKey = vendorKey.OpenSubKey(versionName + subkeySuffix);

                if (versionKey?.GetValue(valueName) is not string home || home.Length == 0)
                {
                    continue;
                }

                result.Add(FileSystem.PathCombine(home.Replace('\\', '/'), "bin", JavaExecutable));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A vendor key we cannot read is not an error; there are a dozen more to try.
        }

        return result;
    }

    /// <summary>Finds JVMs laid out as <c>&lt;root&gt;/&lt;name&gt;/&lt;relativeExecutable&gt;</c>.</summary>
    private static IEnumerable<string> ScanForJvmRoots(string root, string relativeExecutable)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateDirectories(root)
                .Select(directory => FileSystem.PathCombine(directory.Replace('\\', '/'), relativeExecutable))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Order-preserving deduplication, matching upstream's removeDuplicates().</summary>
    private static List<string> Deduplicate(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var result = new List<string>();

        foreach (var path in paths)
        {
            if (path.Length != 0 && seen.Add(path))
            {
                result.Add(path);
            }
        }

        return result;
    }
}
