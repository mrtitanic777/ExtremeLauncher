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
 * The Java runtimes this launcher installed for itself.
 *
 * WAVE 22 COULD DOWNLOAD A JAVA AND NOTHING COULD FIND IT AGAIN. A runtime unpacked into <data>/java
 * was invisible to every launch, because the candidate list came from JavaUtils.FindJavaPaths, which
 * looks where a SYSTEM Java would be -- the registry, /usr/lib/jvm, the PATH -- and not in this
 * launcher's own folder. So the download worked, the probe passed, and pressing Play still said "no
 * compatible Java installation found".
 *
 * THEY GO FIRST in the candidate list. One the launcher fetched for a specific instance is a more
 * deliberate answer than whichever JDK happens to be on the PATH, and the machine that needed the
 * download is exactly the machine whose system Java was wrong.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Launch;

public static class ManagedJava
{
    /// <summary>
    /// Every java binary under the launcher's own java folder.
    /// </summary>
    /// <remarks>
    /// Searched recursively rather than at a fixed depth, because the two download types unpack
    /// differently: an archive usually has a version-named directory inside it, so the binary lands at
    /// <c>java/&lt;name&gt;/jdk-17.0.20+8-jre/bin/java.exe</c>, while a manifest install writes
    /// straight into <c>java/&lt;name&gt;/bin/java.exe</c>. Assuming either shape finds half of them.
    /// </remarks>
    public static IReadOnlyList<string> Find(string javaFolder)
    {
        if (javaFolder.Length == 0 || !Directory.Exists(javaFolder))
        {
            return [];
        }

        var binaryName = OperatingSystem.IsWindows() ? "java.exe" : "java";

        try
        {
            return Directory
                .EnumerateFiles(javaFolder, binaryName, SearchOption.AllDirectories)

                /*
                 * ONLY ONES IN A bin FOLDER. A JDK ships more than one thing called java -- there is a
                 * copy under jre/bin in older layouts, and unpacked source trees can contain others --
                 * and bin is where the one meant to be run lives.
                 */
                .Where(p => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(p)),
                    "bin",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The candidates a launch should try, launcher-installed runtimes first.
    /// </summary>
    /// <param name="systemCandidates">What the system search found, or null to run it.</param>
    public static IReadOnlyList<string> Candidates(
        string javaFolder,
        IReadOnlyList<string>? systemCandidates = null)
    {
        var managed = Find(javaFolder);
        var system = systemCandidates ?? Java.JavaUtils.FindJavaPaths();

        if (managed.Count == 0)
        {
            return system;
        }

        // Deduplicated: a launcher folder that happens to be on the PATH would otherwise be probed
        // twice, and probing is the slow part of choosing a Java.
        var seen = new HashSet<string>(managed, StringComparer.OrdinalIgnoreCase);

        return [.. managed, .. system.Where(p => seen.Add(p))];
    }
}
