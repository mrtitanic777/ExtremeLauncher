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
 * Characterization tests for LaunchProfile. There is no upstream Qt test for this file, but it is the
 * merge step every launch depends on, so the per-field rules are pinned individually.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class LaunchProfileTests
{
    private static RuntimeContext Context(string system = "linux")
        => new() { System = system, JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    private static VersionFile Patch(string uid, string version = "1.0")
        => new() { Uid = uid, Version = version };

    // ================================================================== string fields

    [Fact]
    public void LastNonEmptyStringWins()
    {
        var profile = new LaunchProfile();

        var minecraft = Patch("net.minecraft", "1.20.1");
        minecraft.MainClass = "net.minecraft.client.main.Main";
        profile.Apply(minecraft, Context());

        var forge = Patch("net.minecraftforge");
        forge.MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher";
        profile.Apply(forge, Context());

        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", profile.MainClass);
    }

    [Fact]
    public void AnEmptyStringDoesNotClearAnExistingValue()
    {
        var profile = new LaunchProfile();

        var minecraft = Patch("net.minecraft");
        minecraft.MainClass = "net.minecraft.client.main.Main";
        profile.Apply(minecraft, Context());

        // A patch that says nothing about the main class must leave it alone.
        profile.Apply(Patch("some.other.thing"), Context());

        Assert.Equal("net.minecraft.client.main.Main", profile.MainClass);
    }

    [Fact]
    public void OnlyMinecraftMaySetVersionTypeAndAssets()
    {
        var profile = new LaunchProfile();

        var forge = Patch("net.minecraftforge", "47.1.0");
        forge.Type = "release";
        forge.MojangAssetIndex = new MojangAssetIndexInfo("forge-assets");
        profile.Apply(forge, Context());

        // Nothing from a non-Minecraft patch reaches these three fields.
        Assert.Equal(string.Empty, profile.MinecraftVersion);
        Assert.Equal(string.Empty, profile.MinecraftVersionType);
        Assert.Null(profile.MinecraftAssets);

        var minecraft = Patch("net.minecraft", "1.20.1");
        minecraft.Type = "release";
        minecraft.MojangAssetIndex = new MojangAssetIndexInfo("1.20");
        profile.Apply(minecraft, Context());

        Assert.Equal("1.20.1", profile.MinecraftVersion);
        Assert.Equal("release", profile.MinecraftVersionType);
        Assert.Equal("1.20", profile.MinecraftAssets?.Id);
    }

    // ================================================================== accumulating fields

    [Fact]
    public void JvmArgumentsAccumulate()
    {
        var profile = new LaunchProfile();

        var a = Patch("a");
        a.AddnJvmArguments.Add("-Xss1M");
        profile.Apply(a, Context());

        var b = Patch("b");
        b.AddnJvmArguments.Add("-Dfoo=bar");
        profile.Apply(b, Context());

        Assert.Equal(["-Xss1M", "-Dfoo=bar"], profile.AddnJvmArguments);
    }

    [Fact]
    public void TraitsAreUnioned()
    {
        var profile = new LaunchProfile();

        var a = Patch("a");
        a.Traits.Add("legacyLaunch");
        profile.Apply(a, Context());

        var b = Patch("b");
        b.Traits.Add("legacyLaunch");
        b.Traits.Add("no-texturepacks");
        profile.Apply(b, Context());

        Assert.Equal(2, profile.Traits.Count);
        Assert.True(profile.HasTrait("legacyLaunch"));
        Assert.True(profile.HasTrait("no-texturepacks"));
    }

    [Fact]
    public void ARepeatedTweakerMovesToTheEndRatherThanStayingEarly()
    {
        var profile = new LaunchProfile();

        var a = Patch("a");
        a.AddTweakers.Add("FirstTweaker");
        a.AddTweakers.Add("SecondTweaker");
        profile.Apply(a, Context());

        var b = Patch("b");
        b.AddTweakers.Add("FirstTweaker");
        profile.Apply(b, Context());

        // Tweaker order is load order; re-declaring one asks for it to run later.
        Assert.Equal(["SecondTweaker", "FirstTweaker"], profile.Tweakers);
    }

    // ================================================================== libraries

    [Fact]
    public void LibrariesKeepOnlyTheHighestVersionOfAnArtifact()
    {
        var profile = new LaunchProfile();

        var a = Patch("a");
        a.Libraries.Add(new Library("com.google.guava:guava:17.0"));
        profile.Apply(a, Context());

        var b = Patch("b");
        b.Libraries.Add(new Library("com.google.guava:guava:31.1-jre"));
        profile.Apply(b, Context());

        var library = Assert.Single(profile.Libraries);
        Assert.Equal("31.1-jre", library.Version);
    }

    [Fact]
    public void AnOlderLibraryDoesNotDowngradeANewerOne()
    {
        var profile = new LaunchProfile();

        var a = Patch("a");
        a.Libraries.Add(new Library("com.google.guava:guava:31.1-jre"));
        profile.Apply(a, Context());

        var b = Patch("b");
        b.Libraries.Add(new Library("com.google.guava:guava:17.0"));
        profile.Apply(b, Context());

        Assert.Equal("31.1-jre", Assert.Single(profile.Libraries).Version);
    }

    [Fact]
    public void NativesGoIntoTheirOwnList()
    {
        var profile = new LaunchProfile();

        var patch = Patch("a");
        var native = new Library("org.lwjgl:lwjgl-platform:2.9.4");
        native.NativeClassifiers["linux"] = "natives-linux";
        patch.Libraries.Add(native);
        patch.Libraries.Add(new Library("plain:library:1.0"));

        profile.Apply(patch, Context());

        Assert.Single(profile.Libraries);
        Assert.Single(profile.NativeLibraries);
    }

    [Fact]
    public void InactiveLibrariesAreSkipped()
    {
        var profile = new LaunchProfile();

        var patch = Patch("a");
        var linuxOnly = new Library("linux:only:1.0");
        linuxOnly.SetRules([OsRule.Create(RuleAction.Allow, "linux")]);
        patch.Libraries.Add(linuxOnly);

        profile.Apply(patch, Context("windows"));

        Assert.Empty(profile.Libraries);
    }

    [Fact]
    public void MavenFilesAreNeitherDeduplicatedNorVersionResolved()
    {
        var profile = new LaunchProfile();

        var a = Patch("a");
        a.MavenFiles.Add(new Library("some:payload:1.0"));
        profile.Apply(a, Context());

        var b = Patch("b");
        b.MavenFiles.Add(new Library("some:payload:2.0"));
        profile.Apply(b, Context());

        // Deliberate: maven files are payloads to place on disk, not classpath entries.
        Assert.Equal(2, profile.MavenFiles.Count);
    }

    [Fact]
    public void AgentsSkipNativesAndInactiveLibraries()
    {
        var profile = new LaunchProfile();

        var patch = Patch("a");
        patch.Agents.Add(new Agent(new Library("good:agent:1.0"), "arg"));

        var nativeAgent = new Library("bad:agent:1.0");
        nativeAgent.NativeClassifiers["linux"] = "natives-linux";
        patch.Agents.Add(new Agent(nativeAgent));

        profile.Apply(patch, Context());

        Assert.Single(profile.Agents);
        Assert.Equal("arg", profile.Agents[0].Argument);
    }

    /// <remarks>
    /// UPSTREAM BUG, fixed. applyMods() returns instead of continuing after appending a new mod, so a
    /// patch contributing several previously-unseen mods only ever applied its first.
    /// </remarks>
    [Fact]
    public void EveryNewModIsApplied()
    {
        var profile = new LaunchProfile();

        var patch = Patch("a");
        patch.Mods.Add(new Library("mod:one:1.0"));
        patch.Mods.Add(new Library("mod:two:1.0"));
        patch.Mods.Add(new Library("mod:three:1.0"));

        profile.Apply(patch, Context());

        Assert.Equal(3, profile.Mods.Count);
    }

    [Fact]
    public void ModsAlsoResolveToTheHighestVersion()
    {
        var profile = new LaunchProfile();

        var a = Patch("a");
        a.Mods.Add(new Library("some:mod:1.0"));
        profile.Apply(a, Context());

        var b = Patch("b");
        b.Mods.Add(new Library("some:mod:2.0"));
        profile.Apply(b, Context());

        Assert.Equal("2.0", Assert.Single(profile.Mods).Version);
    }

    // ================================================================== problems and classpath

    [Fact]
    public void ProblemSeverityOnlyEscalates()
    {
        var profile = new LaunchProfile();

        var warned = Patch("a");
        warned.AddProblem(ProblemSeverity.Warning, "careful");
        profile.Apply(warned, Context());

        Assert.Equal(ProblemSeverity.Warning, profile.ProblemSeverity);

        // A clean patch afterwards must not reset it.
        profile.Apply(Patch("b"), Context());
        Assert.Equal(ProblemSeverity.Warning, profile.ProblemSeverity);

        var broken = Patch("c");
        broken.AddProblem(ProblemSeverity.Error, "broken");
        profile.Apply(broken, Context());

        Assert.Equal(ProblemSeverity.Error, profile.ProblemSeverity);
    }

    [Fact]
    public void ClasspathPutsTheMainJarLast()
    {
        var profile = new LaunchProfile();

        var patch = Patch("net.minecraft", "1.20.1");
        patch.Libraries.Add(new Library("com.google.guava:guava:31.1-jre"));
        patch.MainJar = new Library("com.mojang:minecraft:1.20.1:client");
        profile.Apply(patch, Context());

        List<string> jars = [], natives = [];
        profile.GetLibraryFiles(Context(), jars, natives);

        Assert.Equal(2, jars.Count);

        // Libraries first so they can shadow the game's own classes.
        Assert.Contains("guava", jars[0], StringComparison.Ordinal);
        Assert.Contains("minecraft", jars[1], StringComparison.Ordinal);
    }

    [Fact]
    public void JarModsReplaceTheMainJarWithTheMergedOne()
    {
        var profile = new LaunchProfile();

        var patch = Patch("net.minecraft", "1.6.4");
        patch.MainJar = new Library("com.mojang:minecraft:1.6.4:client");
        patch.JarMods.Add(new Library("org.multimc.jarmods:something:1"));
        profile.Apply(patch, Context());

        List<string> jars = [], natives = [];
        profile.GetLibraryFiles(Context(), jars, natives, tempPath: Path.GetTempPath());

        // The original main jar is deliberately absent; the assembled one stands in for it.
        Assert.Single(jars);
        Assert.EndsWith("minecraft.jar", jars[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ClearResetsEverything()
    {
        var profile = new LaunchProfile();

        var patch = Patch("net.minecraft", "1.20.1");
        patch.MainClass = "Main";
        patch.Traits.Add("t");
        patch.Libraries.Add(new Library("a:b:1"));
        profile.Apply(patch, Context());

        profile.Clear();

        Assert.Equal(string.Empty, profile.MinecraftVersion);
        Assert.Equal(string.Empty, profile.MainClass);
        Assert.Empty(profile.Traits);
        Assert.Empty(profile.Libraries);
        Assert.Null(profile.MainJar);
        Assert.Equal(ProblemSeverity.None, profile.ProblemSeverity);
    }
}
