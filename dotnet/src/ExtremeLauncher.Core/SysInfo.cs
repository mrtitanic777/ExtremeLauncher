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
 * Ported from launcher/SysInfo.{h,cpp}.
 *
 * What the launcher is running on, in the vocabulary the metadata uses. Two different vocabularies,
 * in fact, and keeping them apart matters:
 *
 *   CurrentSystem / CurrentArchitecture -- the RULE vocabulary, matched against a version JSON's
 *       os.name and os.arch. "osx", "windows", "linux"; "x86_64", "i386", "arm64".
 *   SupportedJavaArchitecture           -- the RUNTIME-DOWNLOAD vocabulary, matched against a Java
 *       metadata entry's runtimeOS. "linux-x64", "windows-x86", "mac-os-arm64".
 *
 * They look similar and are not interchangeable: "osx" versus "mac-os", "x86_64" versus "x64".
 */

namespace ExtremeLauncher.Core;

public static class SysInfo
{
    /// <summary>The OS name the version metadata's rules use.</summary>
    public static string CurrentSystem()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "osx";
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsFreeBSD())
        {
            return "freebsd";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        return "unknown";
    }

    /// <summary>
    /// The CPU architecture, spelled the way Qt spells it.
    /// </summary>
    /// <remarks>
    /// Qt's QSysInfo::currentCpuArchitecture, whose names the rules are written against. .NET's
    /// Architecture enum uses different ones ("X64", "Arm64"), so they are translated rather than
    /// printed — a rule matching "x86_64" must not silently miss because the string said "X64".
    ///
    /// ROSETTA: upstream detects an x86_64 process running under translation on Apple Silicon and
    /// reports "arm64", so the launcher downloads a native runtime rather than a translated one.
    /// .NET reports the PROCESS architecture too, so the same care is needed; RuntimeInformation's
    /// OSArchitecture gives the machine's real one, which is what this uses.
    /// </remarks>
    public static string CurrentArchitecture()
        => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "i386",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };

    /// <summary>
    /// The <c>runtimeOS</c> string that identifies a downloadable Java runtime for this machine.
    /// </summary>
    /// <returns>An empty string when no runtime is published for this platform.</returns>
    /// <remarks>
    /// An EMPTY RESULT IS MEANINGFUL, not an error: FreeBSD and OpenBSD have no published runtimes, so
    /// automatic Java installation is skipped there and the user's own Java is used. Callers must
    /// check rather than passing the empty string on to a lookup.
    ///
    /// An unrecognised architecture falls through to "&lt;system&gt;-&lt;arch&gt;" rather than failing,
    /// which is how a new platform starts working the day the meta server publishes for it.
    /// </remarks>
    public static string SupportedJavaArchitecture()
    {
        var system = CurrentSystem();
        var arch = CurrentArchitecture();

        switch (system)
        {
            case "windows":
                return arch switch
                {
                    "x86_64" => "windows-x64",
                    "i386" => "windows-x86",
                    _ => "windows-" + arch,
                };

            case "osx":
                if (arch == "arm64")
                {
                    return "mac-os-arm64";
                }

                if (arch.Contains("64", StringComparison.Ordinal))
                {
                    return "mac-os-x64";
                }

                if (arch.Contains("86", StringComparison.Ordinal))
                {
                    return "mac-os-x86";
                }

                return "mac-os-" + arch;

            case "linux":
                return arch switch
                {
                    "x86_64" => "linux-x64",
                    "i386" => "linux-x86",

                    // Works for arm32 and arm64 alike.
                    _ => "linux-" + arch,
                };

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// A default maximum heap size, in MiB.
    /// </summary>
    /// <remarks>
    /// Under 6 GiB of RAM: two thirds of it, leaving room for the OS and everything else the user has
    /// open. At or above: 4 GiB flat, because more rarely helps and a larger heap means longer garbage
    /// collection pauses.
    /// </remarks>
    public static int SuitableMaxMemory()
    {
        var totalRam = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);

        return totalRam < 4096 * 1.5 ? (int)(totalRam / 1.5) : 4096;
    }
}
