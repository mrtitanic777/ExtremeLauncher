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
 * Real zips, built in memory. The ORDER of the checks is what most of these test, because getting it
 * wrong does not error — it imports a Modrinth pack as a CurseForge one and then fails much later
 * with a message about a manifest that was never the pack's.
 */

using System.IO.Compression;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class PackTypeDetectorTests
{
    /// <summary>Builds a zip containing exactly these paths.</summary>
    private static ZipArchive Zip(params string[] paths)
    {
        var buffer = new MemoryStream();

        using (var writing = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in paths)
            {
                using var stream = writing.CreateEntry(path).Open();

                stream.WriteByte((byte)'x');
            }
        }

        buffer.Position = 0;

        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    // ================================================================== the plain cases

    [Fact]
    public void AModrinthPackIsRecognisedByItsIndex()
    {
        using var zip = Zip("modrinth.index.json", "overrides/config/a.cfg");

        Assert.Equal(new PackDetection(ModpackType.Modrinth, string.Empty), PackTypeDetector.Detect(zip));
    }

    [Theory]
    [InlineData("bin/modpack.jar")]
    [InlineData("bin/version.json")]
    public void ATechnicPackIsRecognisedByItsBinFolder(string marker)
    {
        using var zip = Zip(marker, "mods/a.jar");

        var detection = PackTypeDetector.Detect(zip);

        Assert.Equal(ModpackType.Technic, detection.Type);

        /*
         * A Technic archive IS the game directory rather than an instance containing one, so it is
         * extracted a level down. Nothing else needs a subdirectory.
         */
        Assert.Equal("minecraft", detection.ExtractSubdirectory);
    }

    [Fact]
    public void ACurseforgePackIsRecognisedByItsManifest()
    {
        using var zip = Zip("manifest.json", "modlist.html", "overrides/mods/a.jar");

        Assert.Equal(new PackDetection(ModpackType.Flame, string.Empty), PackTypeDetector.Detect(zip));
    }

    [Fact]
    public void AnExportedInstanceIsRecognisedByItsConfig()
    {
        using var zip = Zip("instance.cfg", "mmc-pack.json", "minecraft/options.txt");

        Assert.Equal(new PackDetection(ModpackType.MultiMc, string.Empty), PackTypeDetector.Detect(zip));
    }

    [Fact]
    public void AnArchiveWithNoMarkerIsUnknown()
    {
        using var zip = Zip("readme.txt", "pictures/screenshot.png");

        Assert.Equal(ModpackType.Unknown, PackTypeDetector.Detect(zip).Type);
    }

    [Fact]
    public void AnEmptyArchiveIsUnknown()
    {
        using var zip = Zip();

        Assert.Equal(ModpackType.Unknown, PackTypeDetector.Detect(zip).Type);
    }

    // ================================================================== the ordering

    /*
     * THE REASON THE ORDER EXISTS, in upstream's own words: "especially Flame has a very common
     * filename for its manifest, which may appear inside overrides". A Modrinth pack shipping a
     * CurseForge manifest among its files is a Modrinth pack.
     */
    [Fact]
    public void AModrinthPackShippingAManifestIsStillAModrinthPack()
    {
        using var zip = Zip("modrinth.index.json", "overrides/config/manifest.json");

        Assert.Equal(ModpackType.Modrinth, PackTypeDetector.Detect(zip).Type);
    }

    [Fact]
    public void ATechnicPackShippingAManifestIsStillATechnicPack()
    {
        using var zip = Zip("bin/modpack.jar", "config/manifest.json");

        Assert.Equal(ModpackType.Technic, PackTypeDetector.Detect(zip).Type);
    }

    /// <summary>Anything under overrides is the pack's payload, not its metadata.</summary>
    [Theory]
    [InlineData("overrides/manifest.json")]
    [InlineData("overrides/config/instance.cfg")]
    [InlineData("MyPack/overrides/manifest.json")]
    public void AMarkerUnderOverridesIsIgnored(string path)
    {
        using var zip = Zip("readme.txt", path);

        Assert.Equal(ModpackType.Unknown, PackTypeDetector.Detect(zip).Type);
    }

    /// <summary>An exported instance carrying a CurseForge manifest is still an instance.</summary>
    [Fact]
    public void AnInstanceBeatsAManifestAtTheSameDepth()
    {
        using var zip = Zip("manifest.json", "instance.cfg");

        Assert.Equal(ModpackType.MultiMc, PackTypeDetector.Detect(zip).Type);
    }

    // ================================================================== wrapping folders

    /*
     * Packs are routinely zipped with a wrapping folder — a user re-zipping one from their file
     * manager gets this by default. The root is reported so the extraction can strip it, otherwise
     * the instance ends up one directory deeper than anything expects.
     */
    [Fact]
    public void AWrappingFolderIsReportedAsTheRoot()
    {
        using var zip = Zip("MyPack/manifest.json", "MyPack/overrides/mods/a.jar");

        Assert.Equal(new PackDetection(ModpackType.Flame, "MyPack/"), PackTypeDetector.Detect(zip));
    }

    [Fact]
    public void SeveralWrappingFoldersAreAllStripped()
    {
        using var zip = Zip("Downloads/MyPack/instance.cfg");

        Assert.Equal(new PackDetection(ModpackType.MultiMc, "Downloads/MyPack/"), PackTypeDetector.Detect(zip));
    }

    /// <summary>Shallower wins, so a nested example pack cannot hijack the real one.</summary>
    [Fact]
    public void TheShallowestMarkerWins()
    {
        using var zip = Zip("manifest.json", "examples/other/manifest.json");

        Assert.Equal(string.Empty, PackTypeDetector.Detect(zip).Root);
    }

    [Fact]
    public void AWrappedModrinthPackIsNotFoundBecauseItsMarkerIsRootOnly()
    {
        // Upstream checks the Modrinth marker at the root only; a wrapped .mrpack is not recognised.
        using var zip = Zip("MyPack/modrinth.index.json");

        Assert.Equal(ModpackType.Unknown, PackTypeDetector.Detect(zip).Type);
    }

    // ================================================================== paths as written

    /// <summary>Some zip writers use backslashes; the entry names are normalised before matching.</summary>
    [Fact]
    public void BackslashEntryNamesAreUnderstood()
    {
        using var zip = Zip(@"MyPack\manifest.json");

        Assert.Equal(new PackDetection(ModpackType.Flame, "MyPack/"), PackTypeDetector.Detect(zip));
    }
}
