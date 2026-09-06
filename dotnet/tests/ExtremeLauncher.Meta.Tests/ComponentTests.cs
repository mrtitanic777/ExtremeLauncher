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
 * Characterization tests for Component. There is no upstream Qt test for this file.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class ComponentTests
{
    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    private static VersionFile File(string uid, string version = "1.0", string name = "")
        => new() { Uid = uid, Version = version, Name = name };

    private static Component WithMeta(string uid, VersionFile file)
    {
        var version = new MetaVersion(uid, file.Version) { Data = file };
        return new Component(uid) { MetaVersion = version };
    }

    // ================================================================== backing store

    [Fact]
    public void AMetadataBackedComponentIsNotCustom()
    {
        var component = WithMeta("net.minecraft", File("net.minecraft", "1.20.1"));

        Assert.False(component.IsCustom);
        Assert.True(component.IsCustomizable);
        Assert.NotNull(component.GetVersionFile());
    }

    [Fact]
    public void ALocalOverrideMakesAComponentCustom()
    {
        var component = new Component("net.minecraft", File("net.minecraft", "1.20.1"));

        Assert.True(component.IsCustom);

        // Nothing to customise from: there is no metadata behind it.
        Assert.False(component.IsCustomizable);
        Assert.NotNull(component.GetVersionFile());
    }

    [Fact]
    public void RevertingNeedsBothAnOverrideAndAKnownUid()
    {
        var index = new Index([new VersionList("net.minecraft")]);

        var custom = new Component("net.minecraft", File("net.minecraft"));
        Assert.True(custom.IsRevertible(index));

        // Metadata-backed: nothing to revert.
        Assert.False(WithMeta("net.minecraft", File("net.minecraft")).IsRevertible(index));

        // Custom, but the index has never heard of it, so there is nothing to revert *to*.
        Assert.False(new Component("com.example.homebrew", File("com.example.homebrew")).IsRevertible(index));
    }

    // ================================================================== enable / disable

    [Fact]
    public void AnImportantComponentCannotBeRemovedOrDisabled()
    {
        var component = WithMeta("net.minecraft", File("net.minecraft"));
        component.IsImportant = true;
        component.IsDisabled = true;

        Assert.False(component.IsRemovable);
        Assert.False(component.CanBeDisabled);

        // The disabled flag is ignored when the component cannot be disabled in the first place.
        Assert.True(component.IsEnabled);
    }

    [Fact]
    public void ADependencyOnlyComponentCannotBeDisabled()
    {
        var component = WithMeta("net.fabricmc.intermediary", File("net.fabricmc.intermediary"));
        component.IsDependencyOnly = true;
        component.IsDisabled = true;

        Assert.False(component.CanBeDisabled);
        Assert.True(component.IsEnabled);
    }

    [Fact]
    public void AnOrdinaryComponentRespectsTheDisabledFlag()
    {
        var component = WithMeta("some.mod", File("some.mod"));

        Assert.True(component.CanBeDisabled);
        Assert.True(component.IsEnabled);

        component.IsDisabled = true;
        Assert.False(component.IsEnabled);
    }

    // ================================================================== applying

    [Fact]
    public void ApplyingContributesThePatch()
    {
        var file = File("net.minecraft", "1.20.1");
        file.MainClass = "net.minecraft.client.main.Main";

        var profile = new LaunchProfile();
        WithMeta("net.minecraft", file).ApplyTo(profile, Context());

        Assert.Equal("net.minecraft.client.main.Main", profile.MainClass);
        Assert.Equal("1.20.1", profile.MinecraftVersion);
    }

    [Fact]
    public void ADisabledComponentContributesNothing()
    {
        var file = File("some.mod");
        file.MainClass = "should.not.appear";

        var component = WithMeta("some.mod", file);
        component.IsDisabled = true;

        var profile = new LaunchProfile();
        component.ApplyTo(profile, Context());

        Assert.Equal(string.Empty, profile.MainClass);
    }

    [Fact]
    public void AComponentWithNoFileStillSurfacesItsProblems()
    {
        var component = new Component("broken.thing");
        component.AddComponentProblem(ProblemSeverity.Error, "metadata is missing");

        var profile = new LaunchProfile();
        component.ApplyTo(profile, Context());

        // No patch to apply, but the severity must not be swallowed.
        Assert.Equal(ProblemSeverity.Error, profile.ProblemSeverity);
    }

    // ================================================================== cached data

    [Fact]
    public void CachedDataIsRefreshedFromTheVersionFile()
    {
        var file = File("net.minecraft", "1.20.1", "Minecraft");
        file.IsVolatile = true;
        file.Requires.Add(new Require("some.dep", "2.0"));

        var component = WithMeta("net.minecraft", file);

        Assert.True(component.UpdateCachedData());

        Assert.Equal("Minecraft", component.CachedName);
        Assert.Equal("1.20.1", component.CachedVersion);
        Assert.True(component.CachedVolatile);
        Assert.Single(component.CachedRequires);
    }

    [Fact]
    public void UpdatingCachedDataTwiceReportsNoSecondChange()
    {
        var component = WithMeta("net.minecraft", File("net.minecraft", "1.20.1", "Minecraft"));

        Assert.True(component.UpdateCachedData());
        Assert.False(component.UpdateCachedData());
    }

    [Fact]
    public void DataChangedFiresOnlyWhenSomethingActuallyChanges()
    {
        var component = WithMeta("net.minecraft", File("net.minecraft", "1.20.1", "Minecraft"));

        var events = 0;
        component.DataChanged += (_, _) => events++;

        component.UpdateCachedData();
        component.UpdateCachedData();

        Assert.Equal(1, events);
    }

    [Fact]
    public void LosingTheVersionFileClearsTheCachedRequirements()
    {
        var file = File("net.minecraft", "1.20.1");
        file.Requires.Add(new Require("some.dep"));

        var component = WithMeta("net.minecraft", file);
        component.UpdateCachedData();
        Assert.Single(component.CachedRequires);

        // Metadata went away.
        component.MetaVersion = null;
        component.UpdateCachedData();

        Assert.Empty(component.CachedRequires);
    }

    [Fact]
    public void ARequirementChangingOnlyItsVersionStillCountsAsAChange()
    {
        var file = File("net.fabricmc.fabric-loader", "0.14.21");
        file.Requires.Add(new Require("net.minecraft", "1.20.1"));

        var component = WithMeta("net.fabricmc.fabric-loader", file);
        component.UpdateCachedData();

        // Same uid, different pin. Plain set equality would call these identical, because Require is
        // keyed by uid alone — DeepCompare is what catches it.
        var updated = File("net.fabricmc.fabric-loader", "0.14.21");
        updated.Requires.Add(new Require("net.minecraft", "1.19.4"));
        component.MetaVersion = new MetaVersion("net.fabricmc.fabric-loader", "0.14.21") { Data = updated };

        Assert.True(component.UpdateCachedData());
        Assert.Equal("1.19.4", component.CachedRequires.First().EqualsVersion);
    }

    [Fact]
    public void DeepCompareDistinguishesSetsThatArePlainlyEqual()
    {
        RequireSet a = [new Require("x", "1.0")];
        RequireSet b = [new Require("x", "2.0")];

        // SetEquals would say true; the pins differ.
        Assert.True(a.SetEquals(b));
        Assert.False(Component.DeepCompare(a, b));
    }

    [Fact]
    public void ProblemSeverityCombinesTheComponentAndItsFile()
    {
        var file = File("a");
        file.AddProblem(ProblemSeverity.Warning, "from the file");

        var component = WithMeta("a", file);
        Assert.Equal(ProblemSeverity.Warning, component.GetProblemSeverity());

        component.AddComponentProblem(ProblemSeverity.Error, "from the component");
        Assert.Equal(ProblemSeverity.Error, component.GetProblemSeverity());
        Assert.Equal(2, component.GetProblems().Count);

        component.ResetComponentProblems();
        Assert.Equal(ProblemSeverity.Warning, component.GetProblemSeverity());
    }
}
