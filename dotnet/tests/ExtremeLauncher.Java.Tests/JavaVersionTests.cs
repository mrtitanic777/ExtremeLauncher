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
 * Ported from tests/JavaVersion_test.cpp -- all of test_Parse, test_Sort and test_PermGen.
 */

using Xunit;

namespace ExtremeLauncher.Java.Tests;

public sealed class JavaVersionTests
{
    [Theory]
    // string, major, minor, security, prerelease
    [InlineData("1.6.0_33", 6, 0, 33, "")]        // old format
    [InlineData("1.9.0_1-ea", 9, 0, 1, "ea")]     // old format prerelease
    [InlineData("9", 9, 0, 0, "")]                // new format major
    [InlineData("9.1", 9, 1, 0, "")]              // new format minor
    [InlineData("9.0.1", 9, 0, 1, "")]            // new format security
    [InlineData("9-ea", 9, 0, 0, "ea")]           // new format prerelease
    [InlineData("9.0.1-ea", 9, 0, 1, "ea")]       // new format long prerelease
    public void Parse(string versionString, int major, int minor, int security, string prerelease)
    {
        var version = new JavaVersion(versionString);

        Assert.Equal(versionString, version.ToString());
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(security, version.Security);
        Assert.Equal(prerelease, version.Build);
    }

    [Theory]
    // lhs, rhs, smaller, equal, bigger
    // Old and new format describe the same release.
    [InlineData("1.6.0_33", "6.0.33", false, true, false)]
    // Old format major version.
    [InlineData("1.5.0_33", "1.6.0_33", true, false, false)]
    // First release vs first security patch.
    [InlineData("9", "9.0.1", true, false, false)]
    [InlineData("9.0.1", "9", false, false, true)]
    // First minor vs first release/security patch.
    [InlineData("9.1", "9.0.1", false, false, true)]
    [InlineData("9.0.1", "9.1", true, false, false)]
    [InlineData("9.1", "9", false, false, true)]
    [InlineData("9", "9.1", true, false, false)]
    // Omitted numbers are zero.
    [InlineData("9", "9.0", false, true, false)]
    [InlineData("9", "9.0.0", false, true, false)]
    [InlineData("9.0", "9.0.0", false, true, false)]
    // Early access and prereleases against the final release.
    [InlineData("9-ea", "9", true, false, false)]
    [InlineData("9", "9.0.1-ea", true, false, false)]
    [InlineData("9.0.1-ea", "9", false, false, true)]
    // Prerelease-only differences.
    [InlineData("9-1", "9-1", false, true, false)]
    [InlineData("9-1", "9-2", true, false, false)]
    [InlineData("9-5", "9-20", true, false, false)]
    [InlineData("9-rc1", "9-rc2", true, false, false)]
    [InlineData("9-rc5", "9-rc20", true, false, false)]
    [InlineData("9-rc", "9-rc2", true, false, false)]
    [InlineData("9-ea", "9-rc", true, false, false)]
    public void Sort(string lhs, string rhs, bool smaller, bool equal, bool bigger)
    {
        var left = new JavaVersion(lhs);
        var right = new JavaVersion(rhs);

        Assert.Equal(smaller, left < right);
        Assert.Equal(equal, left == right);
        Assert.Equal(bigger, left > right);
    }

    [Theory]
    [InlineData("1.6.0_33", true)]
    [InlineData("1.7.0_60", true)]
    [InlineData("1.8.0_22", false)]
    [InlineData("9-ea", false)]
    [InlineData("9.2.4", false)]
    public void PermGen(string version, bool needsPermGen)
        => Assert.Equal(needsPermGen, new JavaVersion(version).RequiresPermGen);

    [Fact]
    public void CapabilityFlagsFollowTheMajorVersion()
    {
        // The module system arrived in 9, UTF-8 became the default in 18 (JEP 400).
        Assert.False(new JavaVersion("1.8.0_22").IsModular);
        Assert.True(new JavaVersion("9").IsModular);

        Assert.False(new JavaVersion("17").DefaultsToUtf8);
        Assert.True(new JavaVersion("18").DefaultsToUtf8);
        Assert.True(new JavaVersion("21.0.1").DefaultsToUtf8);
    }

    [Fact]
    public void UnparseableVersionsFallBackToNaturalStringOrder()
    {
        var garbage = new JavaVersion("not a version");

        Assert.False(garbage.IsParseable);

        // Unparseable means "assume it is ancient", so PermGen is still required.
        Assert.True(garbage.RequiresPermGen);
        Assert.False(garbage.IsModular);
    }

    [Fact]
    public void ConstructingFromComponentsRebuildsTheString()
    {
        Assert.Equal("9", new JavaVersion(9, 0, 0).ToString());
        Assert.Equal("9.1", new JavaVersion(9, 1, 0).ToString());
        Assert.Equal("9.0.1", new JavaVersion(9, 0, 1).ToString());
        Assert.Equal("9.0.1.2", new JavaVersion(9, 0, 1, 2).ToString());
    }

    [Fact]
    public void EqualVersionsHashIdentically()
    {
        // Required for the two formats to be interchangeable as dictionary keys.
        Assert.Equal(new JavaVersion("1.6.0_33").GetHashCode(), new JavaVersion("6.0.33").GetHashCode());
        Assert.Equal(new JavaVersion("9").GetHashCode(), new JavaVersion("9.0.0").GetHashCode());
    }

    [Fact]
    public void SortsIntoAscendingOrder()
    {
        var versions = new List<JavaVersion>
        {
            new("17.0.1"),
            new("1.8.0_302"),
            new("9-ea"),
            new("21"),
            new("9"),
        };

        versions.Sort();

        Assert.Equal(
            ["1.8.0_302", "9-ea", "9", "17.0.1", "21"],
            versions.Select(v => v.ToString()));
    }
}

public sealed class JavaInstallTests
{
    [Fact]
    public void OrdersByArchitectureThenIdThenPath()
    {
        var a = new JavaInstall("17.0.1", "32", "/a/java");
        var b = new JavaInstall("17.0.1", "64", "/a/java");

        // Architecture wins over everything else, so the two bitnesses group together.
        Assert.True(a < b);

        var c = new JavaInstall("17.0.1", "64", "/b/java");
        Assert.True(b < c);
    }

    [Fact]
    public void EqualityCoversAllThreeFields()
    {
        var a = new JavaInstall("17", "64", "/x/java");

        Assert.Equal(a, new JavaInstall("17", "64", "/x/java"));
        Assert.NotEqual(a, new JavaInstall("17", "32", "/x/java"));
        Assert.NotEqual(a, new JavaInstall("18", "64", "/x/java"));
        Assert.NotEqual(a, new JavaInstall("17", "64", "/y/java"));
    }

    [Fact]
    public void ExposesTheParsedVersionOfItsId()
    {
        var eight = new JavaInstall("1.8.0_302", "64", "/x/java");

        Assert.Equal(8, eight.Version.Major);

        // PermGen was removed *in* 8, so 8 is the first version that does not need it.
        Assert.False(eight.Version.RequiresPermGen);

        var seven = new JavaInstall("1.7.0_80", "64", "/x/java");

        Assert.Equal(7, seven.Version.Major);
        Assert.True(seven.Version.RequiresPermGen);
    }
}
