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
 * Ported from launcher/JavaCommon.cpp's checkJVMArgs. The rest of JavaCommon is dialog boxes and a
 * Qt task; the one piece of real logic is the validation of a user's extra JVM arguments, which the
 * settings pages run before accepting them. Two things are refused: memory options that duplicate the
 * launcher's own memory controls (they would silently override the boxes in the Java settings), and
 * pinning a required Java version on the command line (unsafe). Everything else is allowed through.
 */

namespace ExtremeLauncher.Java;

/// <summary>What, if anything, is wrong with a set of user-supplied JVM arguments.</summary>
public enum JvmArgsProblem
{
    /// <summary>The arguments are acceptable.</summary>
    None,

    /// <summary>They set memory manually — that belongs in the Memory boxes on the Java settings tab.</summary>
    ManualMemory,

    /// <summary>They pin a required Java version with <c>-version:</c>, which is not allowed.</summary>
    RequiredVersion,
}

/// <summary>Validation of user-supplied JVM arguments.</summary>
public static class JavaArguments
{
    /// <summary>
    /// Checks a JVM argument string for the two things the launcher refuses. Memory is checked first,
    /// matching upstream, so an argument string with both problems reports the memory one.
    /// </summary>
    public static JvmArgsProblem CheckJvmArgs(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // The memory options the launcher owns. "-Xm[sx]" covers -Xms and -Xmx; "-XX-MaxHeapSize" is
        // upstream's own typo (the real flag is -XX:MaxHeapSize) and is kept as written so behaviour
        // matches exactly.
        if (arguments.Contains("-XX:PermSize=", StringComparison.Ordinal)
            || arguments.Contains("-Xms", StringComparison.Ordinal)
            || arguments.Contains("-Xmx", StringComparison.Ordinal)
            || arguments.Contains("-XX-MaxHeapSize", StringComparison.Ordinal)
            || arguments.Contains("-XX:InitialHeapSize", StringComparison.Ordinal))
        {
            return JvmArgsProblem.ManualMemory;
        }

        if (arguments.Contains("-version:", StringComparison.Ordinal))
        {
            return JvmArgsProblem.RequiredVersion;
        }

        return JvmArgsProblem.None;
    }

    /// <summary>Whether a JVM argument string is safe to accept.</summary>
    public static bool AreJvmArgsSafe(string arguments) => CheckJvmArgs(arguments) == JvmArgsProblem.None;

    /// <summary>The warning to show the user for a given problem, or empty for <see cref="JvmArgsProblem.None"/>.</summary>
    public static string WarningFor(JvmArgsProblem problem) => problem switch
    {
        JvmArgsProblem.ManualMemory =>
            "You tried to manually set a JVM memory option (using \"-XX:PermSize\", \"-XX-MaxHeapSize\", "
            + "\"-XX:InitialHeapSize\", \"-Xmx\" or \"-Xms\").\n"
            + "There are dedicated boxes for these in the settings (Java tab, in the Memory group at the top).\n"
            + "This message will be displayed until you remove them from the JVM arguments.",

        JvmArgsProblem.RequiredVersion =>
            "You tried to pass required Java version argument to the JVM (using \"-version:xxx\"). This is not "
            + "safe and will not be allowed.\n"
            + "This message will be displayed until you remove this from the JVM arguments.",

        _ => string.Empty,
    };
}
