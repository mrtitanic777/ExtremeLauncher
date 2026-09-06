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
 * Characterization tests for the dependency resolver. There is no upstream Qt test for
 * ComponentUpdateTask, and it is the most intricate logic in the port, so the decision table is
 * covered case by case.
 */

using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class DependencyResolverTests
{
    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    private static Component Component(
        string uid,
        string version,
        bool dependencyOnly = false,
        bool isVolatile = false,
        bool custom = false,
        params Require[] requires)
    {
        var file = new VersionFile { Uid = uid, Version = version, Name = uid, IsVolatile = isVolatile };

        foreach (var requirement in requires)
        {
            file.Requires.Add(requirement);
        }

        var component = custom
            ? new Component(uid, file)
            : new Component(uid) { MetaVersion = new MetaVersion(uid, version) { Data = file } };

        component.Version = version;
        component.IsDependencyOnly = dependencyOnly;
        component.UpdateCachedData();

        return component;
    }

    private static PackProfile Profile(params Component[] components)
    {
        var profile = new PackProfile(Context());

        foreach (var component in components)
        {
            profile.AppendComponent(component);
        }

        return profile;
    }

    // ================================================================== composing

    [Fact]
    public void AnEmptyPinYieldsToAConcreteOne()
    {
        Assert.True(DependencyResolver.TryCompose(
            new Requirement("a"),
            new Requirement("a", "1.0"),
            out var result));

        Assert.Equal("1.0", result.EqualsVersion);
    }

    [Fact]
    public void IdenticalPinsCompose()
    {
        Assert.True(DependencyResolver.TryCompose(
            new Requirement("a", "1.0"),
            new Requirement("a", "1.0"),
            out var result));

        Assert.Equal("1.0", result.EqualsVersion);
    }

    [Fact]
    public void DisagreeingPinsAreAConflict()
        => Assert.False(DependencyResolver.TryCompose(
            new Requirement("a", "1.0"),
            new Requirement("a", "2.0"),
            out _));

    [Fact]
    public void DisagreeingSuggestionsTakeTheHigherVersion()
    {
        Assert.True(DependencyResolver.TryCompose(
            new Requirement("a", suggests: "1.9"),
            new Requirement("a", suggests: "1.10"),
            out var result));

        // Compared with the launcher's FlexVer comparator, so 1.10 beats 1.9 rather than losing
        // a lexical comparison.
        Assert.Equal("1.10", result.Suggests);
    }

    [Fact]
    public void TheEarliestDependeeIndexWins()
    {
        Assert.True(DependencyResolver.TryCompose(
            new Requirement("a", indexOfFirstDependee: 5),
            new Requirement("a", indexOfFirstDependee: 2),
            out var result));

        // Decides where a newly added dependency gets inserted.
        Assert.Equal(2, result.IndexOfFirstDependee);
    }

    [Fact]
    public void ComposingAcrossPackagesIsRejected()
        => Assert.Throws<ArgumentException>(
            () => DependencyResolver.TryCompose(new Requirement("a"), new Requirement("b"), out _));

    // ================================================================== gathering

    [Fact]
    public void RequirementsFromEveryComponentAreCollected()
    {
        var stack = Profile(
            Component("net.minecraft", "1.20.1"),
            Component("net.fabricmc.fabric-loader", "0.14.21", requires: new Require("net.minecraft", "1.20.1")),
            Component("some.mod", "1.0", requires: new Require("net.fabricmc.fabric-loader")));

        Assert.True(DependencyResolver.TryGatherRequirements(stack.Components, out var requirements, out var problems));

        Assert.Equal(2, requirements.Count);
        Assert.Empty(problems);
    }

    [Fact]
    public void TwoComponentsPinningDifferentVersionsConflict()
    {
        var stack = Profile(
            Component("mod.a", "1.0", requires: new Require("net.minecraft", "1.20.1")),
            Component("mod.b", "1.0", requires: new Require("net.minecraft", "1.19.4")));

        Assert.False(DependencyResolver.TryGatherRequirements(stack.Components, out _, out var problems));

        var problem = Assert.Single(problems);
        Assert.Equal("net.minecraft", problem.Uid);
    }

    // ================================================================== the decision table

    [Fact]
    public void AMissingRequirementIsAdded()
    {
        var stack = Profile(
            Component("net.fabricmc.fabric-loader", "0.14.21", requires: new Require("net.minecraft", "1.20.1")));

        var result = DependencyResolver.Resolve(stack);

        Assert.True(result.Succeeded);
        Assert.Equal("net.minecraft", Assert.Single(result.ToAdd).Uid);
    }

    [Fact]
    public void AnUnpinnedRequirementIsMetByAnyInstalledVersion()
    {
        var stack = Profile(
            Component("net.minecraft", "1.19.4"),
            Component("some.mod", "1.0", requires: new Require("net.minecraft")));

        var result = DependencyResolver.Resolve(stack);

        Assert.True(result.Succeeded);
        Assert.Empty(result.ToAdd);
        Assert.Empty(result.ToChange);
    }

    [Fact]
    public void AMatchingPinIsMet()
    {
        var stack = Profile(
            Component("net.minecraft", "1.20.1"),
            Component("some.mod", "1.0", requires: new Require("net.minecraft", "1.20.1")));

        var result = DependencyResolver.Resolve(stack);

        Assert.True(result.Succeeded);
        Assert.Empty(result.ToChange);
    }

    [Fact]
    public void ADependencyInstalledAtTheWrongVersionCanBeChanged()
    {
        var stack = Profile(
            Component("net.fabricmc.intermediary", "1.19.4", dependencyOnly: true),
            Component("some.mod", "1.0", requires: new Require("net.fabricmc.intermediary", "1.20.1")));

        var result = DependencyResolver.Resolve(stack);

        Assert.True(result.Succeeded);
        Assert.Equal("net.fabricmc.intermediary", Assert.Single(result.ToChange).Uid);
    }

    [Fact]
    public void AUserInstalledComponentAtTheWrongVersionIsAConflict()
    {
        var stack = Profile(
            // Not dependencyOnly: the user chose this one.
            Component("net.minecraft", "1.19.4"),
            Component("some.mod", "1.0", requires: new Require("net.minecraft", "1.20.1")));

        var result = DependencyResolver.Resolve(stack);

        // Changing it silently underneath the user is not on the table.
        Assert.False(result.Succeeded);
        Assert.Empty(result.ToChange);
        Assert.Equal("net.minecraft", Assert.Single(result.Problems).Uid);
    }

    [Fact]
    public void ACustomisedComponentIsNeverChangedAutomatically()
    {
        var stack = Profile(
            Component("net.fabricmc.intermediary", "1.19.4", dependencyOnly: true, custom: true),
            Component("some.mod", "1.0", requires: new Require("net.fabricmc.intermediary", "1.20.1")));

        var result = DependencyResolver.Resolve(stack);

        // Dependency-only, but the user has edited it, so their edit wins over the requirement.
        Assert.False(result.Succeeded);
        Assert.Empty(result.ToChange);
    }

    // ================================================================== removals

    [Fact]
    public void AnUnneededVolatileDependencyIsRemoved()
    {
        var stack = Profile(
            Component("net.minecraft", "1.20.1"),
            Component("net.fabricmc.intermediary", "1.20.1", dependencyOnly: true, isVolatile: true));

        var result = DependencyResolver.Resolve(stack);

        Assert.Equal("net.fabricmc.intermediary", Assert.Single(result.ToRemove));
    }

    [Fact]
    public void AStillNeededVolatileDependencyStays()
    {
        var stack = Profile(
            Component("net.fabricmc.intermediary", "1.20.1", dependencyOnly: true, isVolatile: true),
            Component("net.fabricmc.fabric-loader", "0.14.21", requires: new Require("net.fabricmc.intermediary")));

        Assert.Empty(DependencyResolver.Resolve(stack).ToRemove);
    }

    [Fact]
    public void BothFlagsAreNeededForRemoval()
    {
        // Dependency-only but not volatile: the user may have come to rely on it.
        var notVolatile = Profile(Component("a", "1", dependencyOnly: true));
        Assert.Empty(DependencyResolver.Resolve(notVolatile).ToRemove);

        // Volatile but user-installed: theirs to remove.
        var notDependency = Profile(Component("b", "1", isVolatile: true));
        Assert.Empty(DependencyResolver.Resolve(notDependency).ToRemove);

        // Both: removable.
        var both = Profile(Component("c", "1", dependencyOnly: true, isVolatile: true));
        Assert.Single(DependencyResolver.Resolve(both).ToRemove);
    }

    // ================================================================== tree links

    [Fact]
    public void TreeLinksFindBothDirections()
    {
        var stack = Profile(
            Component("net.minecraft", "1.20.1"),
            Component("net.fabricmc.intermediary", "1.20.1", requires: new Require("net.minecraft", "1.20.1")),
            Component("unrelated", "1.0"));

        // Anything depending on Minecraft.
        var dependees = DependencyResolver.CollectTreeLinked(stack, "net.minecraft");
        Assert.Contains(dependees, c => c.Uid == "net.fabricmc.intermediary");
        Assert.DoesNotContain(dependees, c => c.Uid == "unrelated");

        // ...and anything intermediary itself depends on.
        var dependencies = DependencyResolver.CollectTreeLinked(stack, "net.fabricmc.intermediary");
        Assert.Contains(dependencies, c => c.Uid == "net.minecraft");
    }

    // ================================================================== a realistic stack

    [Fact]
    public void AFabricStackMissingItsDependenciesResolvesCleanly()
    {
        var stack = Profile(
            Component(
                "net.fabricmc.fabric-loader",
                "0.14.21",
                requires: [new Require("net.minecraft", "1.20.1"), new Require("net.fabricmc.intermediary", "1.20.1")]));

        var result = DependencyResolver.Resolve(stack);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ToAdd.Count);
        Assert.Contains(result.ToAdd, r => r.Uid == "net.minecraft");
        Assert.Contains(result.ToAdd, r => r.Uid == "net.fabricmc.intermediary");
        Assert.Empty(result.ToRemove);
    }

    [Fact]
    public void ARemovedLoaderLeavesItsDependenciesRemovable()
    {
        var stack = Profile(
            Component("net.minecraft", "1.20.1"),
            Component("net.fabricmc.intermediary", "1.20.1", dependencyOnly: true, isVolatile: true));

        var result = DependencyResolver.Resolve(stack);

        // Fabric Loader is gone, so nothing requires intermediary any more.
        Assert.True(result.Succeeded);
        Assert.Equal("net.fabricmc.intermediary", Assert.Single(result.ToRemove));
    }
}
