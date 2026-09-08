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
 * For ModIndex.ApplyLoaderOverride (GetModDependenciesTask::getOverride): the Fabric/Quilt API
 * substitution. The ids are upstream's real project ids, so the swaps are pinned with those exact
 * values — on Quilt a Fabric-API request becomes QSL, on Fabric the reverse, and everything else is
 * left alone.
 */

using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class LoaderOverrideTests
{
    // Upstream's Flame ids: Quilted Fabric API (634179) replaces Fabric API (306612).
    private const string FlameQuiltApi = "634179";
    private const string FlameFabricApi = "306612";

    // Modrinth: QSL (qvIfYCYJ) replaces Fabric API (P7dR8mSH).
    private const string ModrinthQuiltApi = "qvIfYCYJ";
    private const string ModrinthFabricApi = "P7dR8mSH";

    private static Dependency Dep(string addonId) => new()
    {
        AddonId = addonId,
        Type = DependencyType.Required,
        Version = "1.0",
    };

    [Fact]
    public void OnQuiltAFabricApiRequestBecomesQsl()
    {
        var result = ModIndex.ApplyLoaderOverride(Dep(FlameFabricApi), ResourceProvider.Flame, ModLoaderTypes.Quilt);

        Assert.Equal(FlameQuiltApi, result.AddonId);
        Assert.Equal(DependencyType.Required, result.Type);
    }

    [Fact]
    public void OnFabricAQuiltApiRequestBecomesFabricApi()
    {
        var result = ModIndex.ApplyLoaderOverride(Dep(FlameQuiltApi), ResourceProvider.Flame, ModLoaderTypes.Fabric);

        Assert.Equal(FlameFabricApi, result.AddonId);
    }

    [Fact]
    public void TheModrinthTableIsUsedForModrinthDependencies()
    {
        var result = ModIndex.ApplyLoaderOverride(Dep(ModrinthFabricApi), ResourceProvider.Modrinth, ModLoaderTypes.Quilt);

        Assert.Equal(ModrinthQuiltApi, result.AddonId);
    }

    /// <summary>Quilt wins when both loader bits are set, so a Fabric request still becomes QSL.</summary>
    [Fact]
    public void QuiltTakesPrecedenceWhenBothBitsAreSet()
    {
        var result = ModIndex.ApplyLoaderOverride(
            Dep(FlameFabricApi), ResourceProvider.Flame, ModLoaderTypes.Quilt | ModLoaderTypes.Fabric);

        Assert.Equal(FlameQuiltApi, result.AddonId);
    }

    [Fact]
    public void AProviderMismatchIsNotSubstituted()
    {
        // The id is a Flame override id, but the dependency is Modrinth's — no match.
        var dep = Dep(FlameFabricApi);
        var result = ModIndex.ApplyLoaderOverride(dep, ResourceProvider.Modrinth, ModLoaderTypes.Quilt);

        Assert.Same(dep, result);
    }

    [Fact]
    public void WithoutFabricOrQuiltNothingIsSubstituted()
    {
        var dep = Dep(FlameFabricApi);
        var result = ModIndex.ApplyLoaderOverride(dep, ResourceProvider.Flame, ModLoaderTypes.Forge);

        Assert.Same(dep, result);
    }

    [Fact]
    public void AnUnlistedDependencyIsReturnedUntouchedWithItsVersion()
    {
        var dep = Dep("999999");
        var result = ModIndex.ApplyLoaderOverride(dep, ResourceProvider.Flame, ModLoaderTypes.Quilt);

        Assert.Same(dep, result);
        Assert.Equal("1.0", result.Version);
    }

    /// <summary>A substituted dependency keeps its type but drops the version, as upstream builds it.</summary>
    [Fact]
    public void ASubstitutionDropsTheVersion()
    {
        var result = ModIndex.ApplyLoaderOverride(Dep(FlameFabricApi), ResourceProvider.Flame, ModLoaderTypes.Quilt);

        Assert.Equal(string.Empty, result.Version);
    }
}
