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
 * These are transcription tests, and that is the honest description of them: the values are hardcoded
 * knowledge about Minecraft's past that exists nowhere else in the project, so what can be checked is
 * that they were copied correctly and that the one function using them behaves.
 *
 * The dates are asserted as instants rather than as strings, because the timezones differ between them
 * and a boundary that moves by two hours moves which snapshot falls on which side.
 */

using System.Text;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class VersionFilterDataTests
{
    // ================================================================== the constants

    /// <summary>Upstream records this one in +02:00, unlike the other three.</summary>
    [Fact]
    public void TheLegacyCutoffIsTheRecordedInstant()
        => Assert.Equal(
            new DateTimeOffset(2013, 6, 25, 13, 8, 56, TimeSpan.Zero),
            VersionFilterData.LegacyCutoffDate.ToUniversalTime());

    [Fact]
    public void TheJavaBoundaryDatesAreTheRecordedInstants()
    {
        Assert.Equal(
            new DateTimeOffset(2017, 3, 30, 9, 32, 19, TimeSpan.Zero),
            VersionFilterData.Java8BeginsDate.ToUniversalTime());

        Assert.Equal(
            new DateTimeOffset(2021, 5, 12, 11, 19, 15, TimeSpan.Zero),
            VersionFilterData.Java16BeginsDate.ToUniversalTime());

        Assert.Equal(
            new DateTimeOffset(2021, 11, 16, 17, 4, 48, TimeSpan.Zero),
            VersionFilterData.Java17BeginsDate.ToUniversalTime());
    }

    [Fact]
    public void TheLwjglWhitelistIsTheSixCoordinates()
        => Assert.Equal(
            [
                "net.java.jinput:jinput",
                "net.java.jinput:jinput-platform",
                "net.java.jutils:jutils",
                "org.lwjgl.lwjgl:lwjgl",
                "org.lwjgl.lwjgl:lwjgl-platform",
                "org.lwjgl.lwjgl:lwjgl_util",
            ],
            VersionFilterData.LwjglWhitelist.Order(StringComparer.Ordinal));

    [Fact]
    public void OnlyOneVersionsForgeInstallerIsBlacklisted()
    {
        Assert.True(VersionFilterData.IsForgeInstallerBlacklisted("1.5.2"));
        Assert.False(VersionFilterData.IsForgeInstallerBlacklisted("1.5.1"));
        Assert.False(VersionFilterData.IsForgeInstallerBlacklisted("1.7.10"));
    }

    /// <summary>Strictly before: a version released at the cutoff instant is not legacy.</summary>
    [Fact]
    public void TheLegacyCutoffIsExclusive()
    {
        Assert.True(VersionFilterData.IsLegacy(VersionFilterData.LegacyCutoffDate.AddSeconds(-1)));
        Assert.False(VersionFilterData.IsLegacy(VersionFilterData.LegacyCutoffDate));
        Assert.False(VersionFilterData.IsLegacy(VersionFilterData.LegacyCutoffDate.AddSeconds(1)));
    }

    // ================================================================== stripping LWJGL

    private static VersionFile PatchWith(params string[] libraryNames)
    {
        var patch = new VersionFile();

        foreach (var name in libraryNames)
        {
            patch.Libraries.Add(new Library(name));
        }

        return patch;
    }

    /*
     * Old version documents bundle LWJGL among Minecraft's own libraries; this launcher installs it as
     * its own component so it can be upgraded independently. Leaving both puts two LWJGLs on the
     * classpath, and which one wins is whichever the ordering happens to put first.
     */
    [Fact]
    public void BundledLwjglIsRemovedAndEverythingElseStays()
    {
        var patch = PatchWith(
            "org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209",
            "org.lwjgl.lwjgl:lwjgl_util:2.9.4-nightly-20150209",
            "net.java.jinput:jinput:2.0.5",
            "com.mojang:netty:1.6",
            "com.google.guava:guava:21.0");

        VersionFilterData.RemoveLwjglFromPatch(patch);

        Assert.Equal(
            ["com.mojang:netty", "com.google.guava:guava"],
            patch.Libraries.Select(l => l.Name.ArtifactPrefix));
    }

    /// <summary>Matched on the prefix, so every version and platform classifier goes.</summary>
    [Fact]
    public void EveryVersionAndClassifierOfALwjglLibraryIsRemoved()
    {
        var patch = PatchWith(
            "org.lwjgl.lwjgl:lwjgl-platform:2.9.0",
            "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209",
            "net.java.jinput:jinput-platform:2.0.5");

        VersionFilterData.RemoveLwjglFromPatch(patch);

        Assert.Empty(patch.Libraries);
    }

    /// <summary>The modern coordinates are a different group; a 1.13+ patch is untouched.</summary>
    [Fact]
    public void ModernLwjglIsNotMatchedByTheLegacyWhitelist()
    {
        var patch = PatchWith("org.lwjgl:lwjgl:3.3.1", "org.lwjgl:lwjgl-glfw:3.3.1");

        VersionFilterData.RemoveLwjglFromPatch(patch);

        Assert.Equal(2, patch.Libraries.Count);
    }

    /// <summary>Only the plain library list, matching upstream — jar mods and maven files stay.</summary>
    [Fact]
    public void OnlyThePlainLibraryListIsFiltered()
    {
        var patch = PatchWith("com.mojang:netty:1.6");
        patch.MavenFiles.Add(new Library("org.lwjgl.lwjgl:lwjgl:2.9.4"));
        patch.JarMods.Add(new Library("org.lwjgl.lwjgl:lwjgl_util:2.9.4"));

        VersionFilterData.RemoveLwjglFromPatch(patch);

        Assert.Single(patch.MavenFiles);
        Assert.Single(patch.JarMods);
    }

    [Fact]
    public void APatchWithNoLwjglIsUnchanged()
    {
        var patch = PatchWith("com.mojang:netty:1.6");

        VersionFilterData.RemoveLwjglFromPatch(patch);

        Assert.Single(patch.Libraries);
    }
}
