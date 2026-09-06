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
 * Ported from tests/GradleSpecifier_test.cpp.
 */

using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class GradleSpecifierTests
{
    [Theory]
    [InlineData("org.gradle.test.classifiers:service:1.0")]                 // 3 parter
    [InlineData("org.gradle.test.classifiers:service:1.0:jdk15")]           // classifier
    [InlineData("org.gradle.test.classifiers:service:1.0@jar")]             // jarextension
    [InlineData("org.gradle.test.classifiers:service:1.0:jdk15@jar")]       // jarboth
    [InlineData("org.gradle.test.classifiers:service:1.0:jdk15@jar.pack.xz")] // packxz
    public void SerializeRoundTrips(string through)
        => Assert.Equal(through, new GradleSpecifier(through).Serialize());

    [Theory]
    [InlineData("group.id:artifact:1.0", "group/id/artifact/1.0/artifact-1.0.jar")]
    [InlineData("id.software:doom:1.666:demons@wad", "id/software/doom/1.666/doom-1.666-demons.wad")]
    public void ToPathBuildsMavenLayout(string spec, string expected)
        => Assert.Equal(expected, new GradleSpecifier(spec).ToPath());

    [Theory]
    [InlineData("org:gradle.test:class:::ifiers:service:1.0::")] // too many :
    [InlineData("I like turtles")]                               // nonsense
    [InlineData("")]                                             // empty string
    [InlineData("herp.derp:artifact")]                           // missing version
    public void InvalidSpecifiersArePreservedVerbatim(string input)
    {
        var spec = new GradleSpecifier(input);

        Assert.False(spec.IsValid);
        Assert.Equal(input, spec.Serialize());
        Assert.Equal(string.Empty, spec.ToPath());
    }

    [Fact]
    public void ExplicitDefaultExtensionSurvivesSerialization()
    {
        // "@jar" is the default extension, but having been written explicitly it must come back.
        Assert.Equal("a:b:1.0@jar", new GradleSpecifier("a:b:1.0@jar").Serialize());
        Assert.Equal("a:b:1.0", new GradleSpecifier("a:b:1.0").Serialize());
    }

    [Fact]
    public void EqualityComparesEffectiveExtensionNotExplicitness()
    {
        // Matches the C++ operator==: these differ in serialized form but compare equal.
        Assert.Equal(new GradleSpecifier("a:b:1.0"), new GradleSpecifier("a:b:1.0@jar"));
        Assert.NotEqual(new GradleSpecifier("a:b:1.0"), new GradleSpecifier("a:b:1.0@wad"));
    }

    [Fact]
    public void GetFileNameDefaultsToJar()
    {
        Assert.Equal("artifact-1.0.jar", new GradleSpecifier("group.id:artifact:1.0").GetFileName());
        Assert.Equal("doom-1.666-demons.wad", new GradleSpecifier("id.software:doom:1.666:demons@wad").GetFileName());
    }

    [Fact]
    public void ToPathHonoursFileNameOverride()
    {
        Assert.Equal(
            "group/id/artifact/1.0/custom.jar",
            new GradleSpecifier("group.id:artifact:1.0").ToPath("custom.jar"));
    }

    [Fact]
    public void MatchNameIgnoresVersion()
    {
        var a = new GradleSpecifier("group.id:artifact:1.0");
        var b = new GradleSpecifier("group.id:artifact:2.0");
        var c = new GradleSpecifier("group.id:other:1.0");

        Assert.True(a.MatchName(b));
        Assert.False(a.MatchName(c));
    }

    [Fact]
    public void ArtifactPrefixDropsVersion()
        => Assert.Equal("group.id:artifact", new GradleSpecifier("group.id:artifact:1.0").ArtifactPrefix);
}
