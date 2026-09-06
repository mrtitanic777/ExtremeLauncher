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
 * Ported from launcher/minecraft/VersionFilterData.{h,cpp} and the LWJGL half of ProfileUtils.cpp.
 * The FML library mapping, which lives in the same upstream file, is already ported inside
 * Launch/UpdateTasks.cs beside the task that uses it.
 *
 * HARDCODED KNOWLEDGE ABOUT MINECRAFT'S PAST. None of this is derivable and none of it is documented
 * anywhere but here: which Maven coordinates count as LWJGL, the day Mojang started needing Java 8,
 * the version whose Forge installer is known broken. It exists because the metadata does not say, and
 * because these facts stopped changing years ago.
 *
 * TRANSCRIBED EXACTLY, INCLUDING THE TIMEZONES. Two of the four dates are recorded in UTC and one in
 * +02:00, which is upstream's own mix and presumably whatever the release announcements said. Converted
 * rather than reinterpreted, because a date that moves by two hours moves the boundary for whichever
 * snapshot was released that afternoon.
 */

using System.Globalization;

namespace ExtremeLauncher.Minecraft;

public static class VersionFilterData
{
    /// <summary>
    /// The Maven coordinates that belong to LWJGL rather than to Minecraft.
    /// </summary>
    /// <remarks>
    /// Matched on the ARTIFACT PREFIX -- group and artifact, no version and no classifier -- because
    /// the same library appears under several versions and with per-platform classifiers, and all of
    /// them are LWJGL's.
    /// </remarks>
    public static readonly IReadOnlySet<string> LwjglWhitelist = new HashSet<string>(StringComparer.Ordinal)
    {
        "net.java.jinput:jinput",
        "net.java.jinput:jinput-platform",
        "net.java.jutils:jutils",
        "org.lwjgl.lwjgl:lwjgl",
        "org.lwjgl.lwjgl:lwjgl_util",
        "org.lwjgl.lwjgl:lwjgl-platform",
    };

    /// <summary>
    /// Minecraft versions whose Forge installer must not be used.
    /// </summary>
    /// <remarks>
    /// Upstream's comment is simply "don't use installers for those". 1.5.2's is known broken; the
    /// launcher installs Forge by other means for that version.
    /// </remarks>
    public static readonly IReadOnlySet<string> ForgeInstallerBlacklist = new HashSet<string>(StringComparer.Ordinal)
    {
        "1.5.2",
    };

    /// <summary>
    /// Before this, mods went in the game jar rather than a mods folder.
    /// </summary>
    /// <remarks>
    /// Upstream marks it "FIXME: remove, used for deciding when core mods should display" -- the UI
    /// shows the jar-mods and core-mods tabs only for versions older than this. Kept because the
    /// behaviour it drives is still wanted; the FIXME is about where the constant lives, not what it
    /// says.
    /// </remarks>
    public static readonly DateTimeOffset LegacyCutoffDate = Parse("2013-06-25T15:08:56+02:00");

    /*
     * THE THREE JAVA BOUNDARY DATES HAVE NO CONSUMER, in upstream or here. Nothing reads them: the
     * launcher takes a version's Java requirement from the javaVersion block in its metadata, which
     * every version has carried since 1.17, and for older ones it does not guess. They are carried for
     * fidelity and because they are the only written record of these boundaries anywhere in the
     * project -- not because anything depends on them.
     *
     * I nearly wrote a GetRequiredJavaMajor() around them. It would have been invented behaviour
     * dressed as a port, resting on constants that are already vestigial, and the giveaway was that
     * its pre-Java-8 branch had nothing meaningful to return.
     */

    /// <summary>The release of 17w13a, the first version to require Java 8.</summary>
    public static readonly DateTimeOffset Java8BeginsDate = Parse("2017-03-30T09:32:19+00:00");

    /// <summary>The release of 21w19a, the first version to require Java 16.</summary>
    public static readonly DateTimeOffset Java16BeginsDate = Parse("2021-05-12T11:19:15+00:00");

    /// <summary>The release of 1.18 Pre-Release 2, the first version to require Java 17.</summary>
    public static readonly DateTimeOffset Java17BeginsDate = Parse("2021-11-16T17:04:48+00:00");

    /// <summary>Whether a release is old enough to put its mods in the game jar.</summary>
    public static bool IsLegacy(DateTimeOffset releaseDate) => releaseDate < LegacyCutoffDate;

    /// <summary>Whether this version's Forge installer is known broken.</summary>
    public static bool IsForgeInstallerBlacklisted(string minecraftVersion)
        => ForgeInstallerBlacklist.Contains(minecraftVersion);

    /// <summary>
    /// Removes the libraries a separate LWJGL component supplies.
    /// </summary>
    /// <remarks>
    /// Ported from ProfileUtils::removeLwjglFromPatch. Old version documents bundle LWJGL among
    /// Minecraft's own libraries; this launcher installs it as its own component (org.lwjgl /
    /// org.lwjgl3) so it can be upgraded independently -- for a native build on a platform the
    /// original never shipped for, most often. Leaving both in place puts two LWJGLs on the classpath,
    /// and which one wins is whichever the ordering happens to put first.
    /// </remarks>
    public static void RemoveLwjglFromPatch(VersionFile patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        // Only the plain library list, matching upstream: native and maven-file lists are left alone.
        patch.Libraries.RemoveAll(library => LwjglWhitelist.Contains(library.Name.ArtifactPrefix));
    }

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
