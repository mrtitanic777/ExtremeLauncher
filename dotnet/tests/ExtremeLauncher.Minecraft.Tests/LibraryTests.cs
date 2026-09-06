// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * Ported from tests/Library_test.cpp -- the path, rule and native-resolution half. The cases that
 * exercise getDownloads() are not ported yet; that method needs MojangLibraryDownloadInfo, which is
 * the next slice.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class LibraryTests
{
    private const string StoragePrefix = "libraries/";

    private static RuntimeContext Context(string system = "linux", string arch = "64", string realArch = "amd64")
        => new() { System = system, JavaArchitecture = arch, JavaRealArchitecture = realArch };

    /// <summary>Absolute path a library is expected to land at, given the default prefix.</summary>
    private static string Storage(string relative)
        => FileSystem.CleanPath(Path.GetFullPath(FileSystem.PathCombine(StoragePrefix, relative)));

    private static (List<string> Jar, List<string> Native, List<string> Native32, List<string> Native64)
        Resolve(Library library, RuntimeContext context, string overridePath = "")
    {
        List<string> jar = [], native = [], native32 = [], native64 = [];
        library.GetApplicableFiles(context, jar, native, native32, native64, overridePath);
        return (jar, native, native32, native64);
    }

    [Fact]
    public void Legacy()
    {
        var test = new Library("test.package:testname:testversion");

        Assert.Equal("test.package:testname", test.ArtifactPrefix);
        Assert.False(test.IsNative);

        var (jar, native, native32, native64) = Resolve(test, Context());

        Assert.Equal([Storage("test/package/testname/testversion/testname-testversion.jar")], jar);
        Assert.Empty(native);
        Assert.Empty(native32);
        Assert.Empty(native64);
    }

    [Fact]
    public void LegacyNative()
    {
        var test = new Library("test.package:testname:testversion");
        test.NativeClassifiers["linux"] = "linux";

        Assert.True(test.IsNative);

        var (jar, native, native32, native64) = Resolve(test, Context());

        // A native contributes to `native`, never to `jar`.
        Assert.Empty(jar);
        Assert.Equal([Storage("test/package/testname/testversion/testname-testversion-linux.jar")], native);
        Assert.Empty(native32);
        Assert.Empty(native64);
    }

    [Fact]
    public void LegacyNativeArchTokenExpandsToBothBitnesses()
    {
        var test = new Library("test.package:testname:testversion");
        test.NativeClassifiers["linux"] = "linux-${arch}";

        var (jar, native, native32, native64) = Resolve(test, Context());

        // The "${arch}" hack: one entry, two concrete files.
        Assert.Empty(jar);
        Assert.Empty(native);
        Assert.Equal([Storage("test/package/testname/testversion/testname-testversion-linux-32.jar")], native32);
        Assert.Equal([Storage("test/package/testname/testversion/testname-testversion-linux-64.jar")], native64);
    }

    [Fact]
    public void NativeOnAnUnsupportedPlatformResolvesToInvalid()
    {
        var test = new Library("test.package:testname:testversion");
        test.NativeClassifiers["linux"] = "linux";

        // Asking for the Windows native of a Linux-only library.
        var (_, native, _, _) = Resolve(test, Context("windows", "64", "amd64"));

        // Upstream substitutes "INVALID" rather than failing, so the wrongness is visible in the path.
        Assert.Equal([Storage("test/package/testname/testversion/testname-testversion-INVALID.jar")], native);
        Assert.False(test.IsActive(Context("windows", "64", "amd64")));
    }

    [Fact]
    public void LocalLibrariesComeFromTheOverridePath()
    {
        var test = new Library("com.paulscode:codecwav:20101023") { Hint = "local" };

        Assert.True(test.IsLocal);

        var overridePath = Path.Combine(Path.GetTempPath(), "el-lib-override");
        var (jar, _, _, _) = Resolve(test, Context(), overridePath);

        // Flattened to just the filename under the override root, not the Maven layout.
        Assert.Equal(
            [FileSystem.CleanPath(Path.GetFullPath(Path.Combine(overridePath, "codecwav-20101023.jar")))],
            jar);
    }

    [Fact]
    public void NonLocalLibrariesIgnoreTheOverridePath()
    {
        var test = new Library("com.paulscode:codecwav:20101023");

        var (jar, _, _, _) = Resolve(test, Context(), Path.Combine(Path.GetTempPath(), "ignored"));

        Assert.Equal([Storage("com/paulscode/codecwav/20101023/codecwav-20101023.jar")], jar);
    }

    [Fact]
    public void StoragePrefixCanBeOverridden()
    {
        var test = new Library("test.package:testname:testversion");
        test.SetStoragePrefix("custom-libs/");

        var (jar, _, _, _) = Resolve(test, Context());

        Assert.Equal(
            [FileSystem.CleanPath(Path.GetFullPath("custom-libs/test/package/testname/testversion/testname-testversion.jar"))],
            jar);
    }

    // ================================================================== rules

    [Fact]
    public void NoRulesMeansAlwaysActive()
        => Assert.True(new Library("a:b:1").IsActive(Context()));

    [Fact]
    public void AnAllowRuleForOneOsExcludesEveryOther()
    {
        var test = new Library("a:b:1");
        test.SetRules([OsRule.Create(RuleAction.Allow, "linux")]);

        // The verdict starts at Disallow, so "allow on linux" means "linux only".
        Assert.True(test.IsActive(Context("linux")));
        Assert.False(test.IsActive(Context("windows")));
        Assert.False(test.IsActive(Context("osx")));
    }

    [Fact]
    public void TheLastApplicableRuleWins()
    {
        var test = new Library("a:b:1");

        // Allow everywhere, then take osx back away.
        test.SetRules([
            ImplicitRule.Create(RuleAction.Allow),
            OsRule.Create(RuleAction.Disallow, "osx"),
        ]);

        Assert.True(test.IsActive(Context("linux")));
        Assert.True(test.IsActive(Context("windows")));
        Assert.False(test.IsActive(Context("osx")));
    }

    [Fact]
    public void RulesParseFromMojangJson()
    {
        var json = Json.RequireObject(Json.RequireDocument("""
            {
              "rules": [
                { "action": "allow" },
                { "action": "disallow", "os": { "name": "osx", "version": "^10\\.5\\.\\d$" } }
              ]
            }
            """));

        var rules = RuleParser.RulesFromJsonV4(json);

        Assert.Equal(2, rules.Count);
        Assert.IsType<ImplicitRule>(rules[0]);

        var osRule = Assert.IsType<OsRule>(rules[1]);
        Assert.Equal("osx", osRule.System);
        Assert.Equal(@"^10\.5\.\d$", osRule.VersionRegex);

        var library = new Library("a:b:1");
        library.SetRules(rules);

        Assert.True(library.IsActive(Context("linux")));
        Assert.False(library.IsActive(Context("osx")));
    }

    // ================================================================== runtime context

    [Theory]
    [InlineData("amd64", "x86_64")]
    [InlineData("i386", "x86")]
    [InlineData("i686", "x86")]
    [InlineData("aarch64", "arm64")]
    [InlineData("arm", "arm32")]
    [InlineData("armhf", "arm32")]
    [InlineData("riscv64", "riscv64")]
    public void ArchitectureNamesMapToMojangSpelling(string reported, string expected)
        => Assert.Equal(expected, new RuntimeContext { JavaRealArchitecture = reported }.MappedJavaRealArchitecture());

    [Fact]
    public void LegacyArchitecturesMatchABareOsClassifier()
    {
        var legacy = Context("linux", "64", "amd64");

        Assert.Equal("linux-x86_64", legacy.GetClassifier());
        Assert.True(legacy.IsLegacyArch());

        // Old version JSONs just say "linux".
        Assert.True(legacy.ClassifierMatches("linux"));
        Assert.True(legacy.ClassifierMatches("linux-x86_64"));
        Assert.False(legacy.ClassifierMatches("windows"));
    }

    [Fact]
    public void ModernArchitecturesRequireThePreciseClassifier()
    {
        var arm = Context("linux", "64", "aarch64");

        Assert.Equal("linux-arm64", arm.GetClassifier());
        Assert.False(arm.IsLegacyArch());

        // No fallback here: an arm64 machine must not pick up an x86 native.
        Assert.False(arm.ClassifierMatches("linux"));
        Assert.True(arm.ClassifierMatches("linux-arm64"));
    }

    [Fact]
    public void NativeLookupFallsBackOnLegacyArchOnly()
    {
        var test = new Library("a:b:1");
        test.NativeClassifiers["linux"] = "natives-linux";

        // x86_64 gets the bare "linux" entry via the legacy fallback...
        Assert.Equal("natives-linux", test.GetCompatibleNative(Context("linux", "64", "amd64")));

        // ...but arm64 does not.
        Assert.Null(test.GetCompatibleNative(Context("linux", "64", "aarch64")));
    }

    [Fact]
    public void PreciseNativeClassifiersWinWhenPresent()
    {
        var test = new Library("a:b:1");
        test.NativeClassifiers["linux"] = "natives-linux";
        test.NativeClassifiers["linux-arm64"] = "natives-linux-arm64";

        Assert.Equal("natives-linux-arm64", test.GetCompatibleNative(Context("linux", "64", "aarch64")));
        Assert.Equal("natives-linux", test.GetCompatibleNative(Context("linux", "64", "amd64")));
    }

    // ================================================================== naming

    [Fact]
    public void FilenameFollowsTheCoordinateUnlessOverridden()
    {
        var test = new Library("test.package:testname:testversion");

        Assert.Equal("testname-testversion.jar", test.GetFilename(Context()));
        Assert.Equal("testname-testversion.jar", test.GetDisplayName(Context()));

        test.DisplayNameOverride = "Pretty Name";
        Assert.Equal("Pretty Name", test.GetDisplayName(Context()));

        test.Filename = "custom.jar";
        Assert.Equal("custom.jar", test.GetFilename(Context()));
    }

    [Fact]
    public void NativeFilenameUsesTheResolvedClassifier()
    {
        var test = new Library("test.package:testname:testversion");
        test.NativeClassifiers["linux"] = "linux";

        Assert.Equal("testname-testversion-linux.jar", test.GetFilename(Context()));
    }

    /*
     * ONE ASSERT PER FIELD upstream's limitedCopy touches (Library.h), because this is a hand-written
     * copy: a field added to Library and forgotten here is silently dropped, with no compiler error.
     *
     * That already happened once. The downloads block was missing, so every library in every launch
     * profile fell back to deriving its URL from the Maven coordinate -- correct for almost all of
     * them, and wrong for exactly the repackaged LWJGL natives, which 404'd at launch.
     */
    [Fact]
    public void LimitedCopyCarriesEveryFieldUpstreamCopies()
    {
        var downloads = new MojangLibraryDownloadInfo
        {
            Artifact = new MojangDownloadInfo { Url = "https://example.invalid/real.jar" },
        };

        var original = new Library("a:b:1")
        {
            Hint = "local",
            RepositoryUrl = "file://foo",
            AbsoluteUrl = "https://example.invalid/absolute.jar",
            Filename = "override.jar",
            MojangDownloads = downloads,
        };

        original.SetStoragePrefix("prefix/");
        original.NativeClassifiers["linux"] = "linux";
        original.ExtractExcludes.Add("META-INF/");
        original.SetRules([OsRule.Create(RuleAction.Allow, "linux")]);

        var copy = Library.LimitedCopy(original);

        Assert.Equal("a:b:1", copy.Name.Serialize());
        Assert.Equal("local", copy.Hint);
        Assert.Equal("file://foo", copy.RepositoryUrl);
        Assert.Equal("https://example.invalid/absolute.jar", copy.AbsoluteUrl);
        Assert.Equal("override.jar", copy.Filename);
        Assert.Equal("prefix/", copy.StoragePrefix);
        Assert.Equal(["META-INF/"], copy.ExtractExcludes);
        Assert.True(copy.IsNative);
        Assert.Single(copy.Rules);

        // Shared rather than cloned, matching upstream's shared_ptr assignment.
        Assert.Same(downloads, copy.MojangDownloads);
    }
}
