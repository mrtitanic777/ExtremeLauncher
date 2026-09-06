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
 * Which Java runtimes can be installed, and where each one goes.
 *
 * EVERY FIXTURE HERE IS SHAPED LIKE THE REAL METADATA, because the real metadata is what taught this
 * file what to test. Three of these tests exist only because a probe against meta.prismlauncher.org
 * showed behaviour no reasonable guess would have produced.
 */

using ExtremeLauncher.Java;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class JavaRuntimeSourceTests
{
    /// <summary>Mojang's shape: a version object WITH a name.</summary>
    private static InstallableJava Mojang(string name = "21.0.7", string hash = "06a3884df3d9d4072b9b6")
        => new(
            "net.minecraft.java",
            "java21",
            new JavaMetadata
            {
                Name = "java-runtime-delta",
                Vendor = "mojang",
                Url = "https://piston-meta.mojang.com/v1/packages/" + hash + "/manifest.json",
                DownloadType = DownloadType.Manifest,
                PackageType = "jre",
                RuntimeOS = "windows-x64",
                ChecksumType = "sha1",
                ChecksumHash = hash,
                Version = new JavaVersion(21, 0, 7, name: name),
            });

    /// <summary>Azul's shape: major/minor/security and NO name.</summary>
    private static InstallableJava Azul(int major = 8, int security = 504, string hash = "a4f32724c6d819c20372")
        => new(
            "com.azul.java",
            "java8",
            new JavaMetadata
            {
                Name = "azul_zulu_jre8.0.504",
                Vendor = "azul",
                Url = "https://cdn.azul.com/zulu/bin/zulu8-ca-jre" + security + "-win_x64.zip",
                DownloadType = DownloadType.Archive,
                PackageType = "jre",
                RuntimeOS = "windows-x64",
                ChecksumType = "sha256",
                ChecksumHash = hash,
                Version = new JavaVersion(major, 0, security),
            });

    [Fact]
    public void AVendorWithNoVersionNameStillGetsAReadableOne()
    {
        /*
         * FOUND BY READING THE REAL METADATA. Only Mojang publishes a `name`:
         *
         *     mojang   "version": { "major": 21, "minor": 0, "name": "21.0.7", "security": 7 }
         *     azul     "version": { "major": 8,  "minor": 0, "security": 504 }
         *
         * Taking Name alone displayed every non-Mojang entry as "Java  — Azul (Zulu)".
         */
        Assert.Equal("8.0.504", Azul().VersionLabel);
        Assert.Equal("Java 8.0.504 — Azul (Zulu)", Azul().DisplayName);

        // Mojang's own name is preferred where there is one.
        Assert.Equal("21.0.7", Mojang().VersionLabel);
    }

    [Fact]
    public void TwoRuntimesOfTheSameVendorAndVersionDoNotShareAFolder()
    {
        /*
         * ALSO FOUND IN THE REAL METADATA: com.ibm.java carries two different 25.0.2 builds for
         * windows-x64 with different downloads. Without a discriminator the second install silently
         * overwrites the first, and the launcher then points at a runtime that is not the one it
         * recorded -- which is the worst kind of wrong, because nothing reports it.
         */
        var first = Azul(hash: "aaaaaaaaaaaaaaaaaaaa");
        var second = Azul(hash: "bbbbbbbbbbbbbbbbbbbb");

        Assert.NotEqual(JavaRuntimeSource.FolderNameFor(first), JavaRuntimeSource.FolderNameFor(second));
    }

    [Fact]
    public void TheSameRuntimeAlwaysGetsTheSameFolder()
    {
        // Deterministic, so reinstalling reuses the folder rather than accumulating copies of one JRE.
        Assert.Equal(
            JavaRuntimeSource.FolderNameFor(Azul()),
            JavaRuntimeSource.FolderNameFor(Azul()));
    }

    [Fact]
    public void TheFolderNamesTheVendorAndTheVersion()
    {
        // A folder called "java21" that could be any of four vendors is one somebody has to open to
        // identify.
        var folder = JavaRuntimeSource.FolderNameFor(Azul());

        Assert.StartsWith("azul-8.0.504-", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolderIsAValidFileName()
    {
        // A vendor or version carrying something a filesystem refuses would otherwise fail at
        // extraction time, long after the download.
        var awkward = new InstallableJava(
            "com.azul.java",
            "java8",
            new JavaMetadata
            {
                Vendor = "we/ird:vendor",
                Url = "https://example.invalid/x.zip",
                ChecksumHash = "abcdef0123",
                Version = new JavaVersion(8, 0, 1),
            });

        var folder = JavaRuntimeSource.FolderNameFor(awkward);

        Assert.DoesNotContain(folder, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Fact]
    public void AllFourVendorsAreOffered()
    {
        // They are not interchangeable: Mojang's are what the game is tested against, and the others
        // cover platforms Mojang publishes nothing for.
        Assert.Equal(
            ["net.minecraft.java", "net.adoptium.java", "com.azul.java", "com.ibm.java"],
            JavaRuntimeSource.Packages);
    }

    [Fact]
    public void MojangIsListedFirst()
    {
        // The default, because it is what the game is tested against.
        Assert.Equal("net.minecraft.java", JavaRuntimeSource.Packages[0]);
    }

    [Fact]
    public void TheVendorLabelIsReadableRatherThanAUid()
    {
        Assert.Equal("Mojang", Mojang().VendorLabel);
        Assert.Equal("Azul (Zulu)", Azul().VendorLabel);
    }

    [Fact]
    public void AJdkIsRecognisedAsOneRatherThanBeingHidden()
    {
        // Some people want a JDK, and refusing it would be the launcher deciding something it has no
        // business deciding.
        var jdk = new InstallableJava(
            "net.adoptium.java",
            "java21",
            new JavaMetadata { PackageType = "jdk", Version = new JavaVersion(21, 0, 1) });

        Assert.True(jdk.IsJdk);
        Assert.False(Azul().IsJdk);
    }

    [Fact]
    public void TheInstallTaskMatchesTheDownloadTypeTheMetadataDeclares()
    {
        /*
         * The two are NOT interchangeable: "archive" is a tarball to unpack, "manifest" is Mojang's
         * per-file listing that has to be walked. Choosing wrongly produces a download that succeeds
         * and leaves nothing runnable behind -- and Mojang is the only one using manifest, so a
         * launcher that assumed archive would work for three vendors out of four.
         */
        var paths = new LauncherPaths(Path.Combine(Path.GetTempPath(), "el-jrt-" + Guid.NewGuid().ToString("N")));

        using var client = new HttpClient();

        var source = new JavaRuntimeSource(paths, client, "https://meta.invalid/v1/");

        Assert.IsType<ManifestDownloadTask>(source.CreateInstallTask(Mojang()));
        Assert.IsType<ArchiveDownloadTask>(source.CreateInstallTask(Azul()));
    }
}
