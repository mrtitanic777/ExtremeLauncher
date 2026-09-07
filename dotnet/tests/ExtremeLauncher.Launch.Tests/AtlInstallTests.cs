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
 * For the pure decision helpers lifted out of ATLPackInstallTask: getDirForModType (where each mod
 * type is placed) and detectLibrary (a pack library's Gradle coordinate). The mod-type table is a
 * delivery instruction, so each row is named by the input that motivates it; the coordinate detection
 * has three branches — server path, known filename, md5 fallback — and each is pinned here.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AtlInstallTests
{
    // ================================================================== getDirForModType

    [Theory]
    [InlineData(AtlModType.Mods, "mods")]
    [InlineData(AtlModType.Jar, "jarmods")]
    [InlineData(AtlModType.Forge, "jarmods")]
    [InlineData(AtlModType.Flan, "Flan")]
    [InlineData(AtlModType.Ic2Lib, "mods/ic2")]
    [InlineData(AtlModType.DenLib, "mods/denlib")]
    [InlineData(AtlModType.Coremods, "coremods")]
    [InlineData(AtlModType.Plugins, "plugins")]
    [InlineData(AtlModType.TexturePack, "texturepacks")]
    [InlineData(AtlModType.ResourcePack, "resourcepacks")]
    [InlineData(AtlModType.ShaderPack, "shaderpacks")]
    public void EachPlacedTypeGoesToItsFolder(AtlModType type, string expected)
        => Assert.Equal(expected, AtlInstall.GetDirForModType(type, type.ToString(), "1.7.10"));

    /// <summary>A dependency lands in a mods subfolder named for the Minecraft version.</summary>
    [Fact]
    public void ADependencyLandsInAPerVersionModsFolder()
        => Assert.Equal("mods/1.12.2", AtlInstall.GetDirForModType(AtlModType.Dependency, "dependency", "1.12.2"));

    [Theory]
    [InlineData(AtlModType.Root)]
    [InlineData(AtlModType.Extract)]
    [InlineData(AtlModType.Decomp)]
    [InlineData(AtlModType.TexturePackExtract)]
    [InlineData(AtlModType.ResourcePackExtract)]
    [InlineData(AtlModType.Mcpc)]
    [InlineData(AtlModType.Millenaire)]
    public void TypesHandledElsewhereOrUnsupportedHaveNoFolder(AtlModType type)
        => Assert.Null(AtlInstall.GetDirForModType(type, type.ToString(), "1.7.10"));

    /// <summary>An unknown type is a fatal install error, as upstream treats it.</summary>
    [Fact]
    public void AnUnknownTypeThrows()
    {
        var error = Assert.Throws<LauncherException>(
            () => AtlInstall.GetDirForModType(AtlModType.Unknown, "banana", "1.7.10"));

        Assert.Contains("banana", error.Message, StringComparison.Ordinal);
    }

    // ================================================================== detectLibrary

    /// <summary>A server path spells out the whole coordinate; the group's slashes become dots.</summary>
    [Fact]
    public void AServerPathBecomesItsCoordinate()
    {
        var library = new AtlVersionLibrary
        {
            Server = "net/minecraftforge/forge/14.23.5.2860/forge-14.23.5.2860.jar",
            File = "forge.jar",
            Md5 = "abc",
        };

        Assert.Equal("net.minecraftforge:forge:14.23.5.2860", AtlInstall.DetectLibrary(library));
    }

    [Theory]
    [InlineData("guava-21.0.jar", "com.google.guava:guava:21.0")]
    [InlineData("commons-lang3-3.12.0.jar", "org.apache.commons:commons-lang3:3.12.0")]
    public void AKnownFilenameBecomesItsCoordinate(string file, string expected)
    {
        var library = new AtlVersionLibrary { File = file, Md5 = "abc" };

        Assert.Equal(expected, AtlInstall.DetectLibrary(library));
    }

    /// <summary>An unrecognised library falls back to a synthetic coordinate keyed by its md5.</summary>
    [Fact]
    public void AnUnrecognisedLibraryFallsBackToItsMd5()
    {
        var library = new AtlVersionLibrary { File = "mystery.jar", Md5 = "deadbeef" };

        Assert.Equal("org.multimc.atlauncher:deadbeef:1", AtlInstall.DetectLibrary(library));
    }

    /// <summary>A short server path (under three segments) is not trusted; detection moves on.</summary>
    [Fact]
    public void AShortServerPathIsIgnored()
    {
        var library = new AtlVersionLibrary { Server = "host/file.jar", File = "mystery.jar", Md5 = "cafe" };

        Assert.Equal("org.multimc.atlauncher:cafe:1", AtlInstall.DetectLibrary(library));
    }
}
