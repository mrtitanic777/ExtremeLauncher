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
 * For ModDependencies.NewRequiredDependencies (GetModDependenciesTask::getDependenciesForVersion): the
 * filter that decides which of a version's dependencies still need fetching. The rules it encodes are
 * each pinned here — only required ones, deduplicated, the loader override applied, and anything already
 * present skipped — plus the Modrinth version-only matching path.
 */

using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class ModDependenciesTests
{
    private static IndexedVersion Version(params Dependency[] deps)
    {
        var version = new IndexedVersion();
        version.Dependencies.AddRange(deps);

        return version;
    }

    private static Dependency Dep(string addonId, DependencyType type = DependencyType.Required, string version = "")
        => new() { AddonId = addonId, Type = type, Version = version };

    [Fact]
    public void OnlyRequiredDependenciesAreReturned()
    {
        var version = Version(
            Dep("a"),
            Dep("b", DependencyType.Optional),
            Dep("c", DependencyType.Incompatible),
            Dep("d", DependencyType.Embedded));

        var result = ModDependencies.NewRequiredDependencies(version, ResourceProvider.Flame, ModLoaderTypes.Forge, []);

        Assert.Equal(["a"], result.Select(d => d.AddonId));
    }

    [Fact]
    public void DuplicatesWithinTheVersionAreCollapsed()
    {
        var version = Version(Dep("a"), Dep("a"), Dep("b"));

        var result = ModDependencies.NewRequiredDependencies(version, ResourceProvider.Flame, ModLoaderTypes.Forge, []);

        Assert.Equal(["a", "b"], result.Select(d => d.AddonId));
    }

    [Fact]
    public void ADependencyAlreadyPresentForTheProviderIsSkipped()
    {
        var version = Version(Dep("a"), Dep("b"));
        var known = new[] { new KnownDependency(ResourceProvider.Flame, "a", string.Empty) };

        var result = ModDependencies.NewRequiredDependencies(version, ResourceProvider.Flame, ModLoaderTypes.Forge, known);

        Assert.Equal(["b"], result.Select(d => d.AddonId));
    }

    /// <summary>The "already present" match is per-provider: the same id on another provider does not count.</summary>
    [Fact]
    public void APresentDependencyOnAnotherProviderDoesNotCount()
    {
        var version = Version(Dep("a"));
        var known = new[] { new KnownDependency(ResourceProvider.Modrinth, "a", string.Empty) };

        var result = ModDependencies.NewRequiredDependencies(version, ResourceProvider.Flame, ModLoaderTypes.Forge, known);

        Assert.Equal(["a"], result.Select(d => d.AddonId));
    }

    /// <summary>On Quilt, a Fabric-API dependency is redirected to the Quilt package by the override.</summary>
    [Fact]
    public void TheLoaderOverrideIsAppliedBeforeMatching()
    {
        var overrides = ModIndex.GetOverrideDependencies();
        var fabricApi = overrides.First(o => o.Provider == ResourceProvider.Modrinth);

        var version = Version(Dep(fabricApi.Fabric));

        var result = ModDependencies.NewRequiredDependencies(
            version, ResourceProvider.Modrinth, ModLoaderTypes.Quilt, []);

        Assert.Equal(fabricApi.Quilt, Assert.Single(result).AddonId);
    }

    /// <summary>A Modrinth dependency with no addon id is matched by version instead.</summary>
    [Fact]
    public void AModrinthVersionOnlyDependencyIsMatchedByVersion()
    {
        var version = Version(Dep(string.Empty, version: "v1"), Dep(string.Empty, version: "v2"));
        var known = new[] { new KnownDependency(ResourceProvider.Modrinth, string.Empty, "v1") };

        var result = ModDependencies.NewRequiredDependencies(version, ResourceProvider.Modrinth, ModLoaderTypes.Fabric, known);

        Assert.Equal(["v2"], result.Select(d => d.Version));
    }

    [Fact]
    public void NothingIsReturnedWhenThereAreNoDependencies()
        => Assert.Empty(ModDependencies.NewRequiredDependencies(
            Version(), ResourceProvider.Flame, ModLoaderTypes.Forge, []));
}
