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
 * No inherited suite covers ModIndex, so these are characterization tests: they pin what upstream
 * does, including the parts that look like oversights, so a later change has to be deliberate.
 *
 * The strings matter more than they look. They are what goes into packwiz files on a user's disk and
 * into request paths, so "renaming" one is a compatibility break, not a tidy-up.
 */

using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class ModIndexTests
{
    [Theory]
    [InlineData(ResourceProvider.Modrinth, "modrinth", "Modrinth")]
    [InlineData(ResourceProvider.Flame, "curseforge", "CurseForge")]
    public void ProvidersKnowBothOfTheirNames(ResourceProvider provider, string name, string readable)
    {
        Assert.Equal(name, ProviderCapabilities.Name(provider));
        Assert.Equal(readable, ProviderCapabilities.ReadableName(provider));
    }

    /*
     * The launcher writes "curseforge" into packwiz files and reads it back, so this pairing is a
     * file-format guarantee. The enum is called Flame after CurseForge's old name, which is why the
     * two differ at all.
     */
    [Fact]
    public void TheFlameProviderIsSpelledCurseforgeOnDisk()
        => Assert.Equal("curseforge", ProviderCapabilities.Name(ResourceProvider.Flame));

    [Fact]
    public void HashTypesAreOrderedBestFirst()
    {
        Assert.Equal(["sha512", "sha1"], ProviderCapabilities.HashTypes(ResourceProvider.Modrinth));

        // murmur2 last: it is the legacy identification path and the most work to compute.
        Assert.Equal(["sha1", "md5", "murmur2"], ProviderCapabilities.HashTypes(ResourceProvider.Flame));
    }

    // ================================================================== version types

    [Theory]
    [InlineData(VersionType.Release, "release")]
    [InlineData(VersionType.Beta, "beta")]
    [InlineData(VersionType.Alpha, "alpha")]
    public void VersionTypesRoundTrip(VersionType type, string text)
    {
        Assert.Equal(text, ModIndex.ToString(type));
        Assert.Equal(type, ModIndex.VersionTypeFromString(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Release")]
    [InlineData("stable")]
    public void AnUnrecognisedVersionTypeIsUnknown(string text)
        => Assert.Equal(VersionType.Unknown, ModIndex.VersionTypeFromString(text));

    /// <summary>Unknown is a value, but it has no spelling of its own on the wire.</summary>
    [Fact]
    public void UnknownRendersAsUnknownAndDoesNotRoundTrip()
    {
        Assert.Equal("unknown", ModIndex.ToString(VersionType.Unknown));

        // Reading "unknown" back also gives Unknown, but only because everything unrecognised does.
        Assert.Equal(VersionType.Unknown, ModIndex.VersionTypeFromString("unknown"));
    }

    /*
     * The ordering is load-bearing: the mod browser filters on "at least as stable as", which is a
     * comparison, not a set membership test. Upstream numbers Release as 1 for exactly this, and
     * writes out all six relational operators to make it usable.
     */
    [Fact]
    public void VersionTypesOrderFromStableToUnstable()
    {
        Assert.True(VersionType.Release < VersionType.Beta);
        Assert.True(VersionType.Beta < VersionType.Alpha);
        Assert.True(VersionType.Alpha < VersionType.Unknown);
    }

    // ================================================================== mod loaders

    [Theory]
    [InlineData(ModLoaderTypes.NeoForge, "neoforge")]
    [InlineData(ModLoaderTypes.Forge, "forge")]
    [InlineData(ModLoaderTypes.Cauldron, "cauldron")]
    [InlineData(ModLoaderTypes.LiteLoader, "liteloader")]
    [InlineData(ModLoaderTypes.Fabric, "fabric")]
    [InlineData(ModLoaderTypes.Quilt, "quilt")]
    public void ModLoadersRoundTrip(ModLoaderTypes loader, string text)
    {
        Assert.Equal(text, ModIndex.ToString(loader));
        Assert.Equal(loader, ModIndex.ModLoaderFromString(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Forge")]
    [InlineData("rift")]
    public void AnUnrecognisedLoaderIsNone(string text)
        => Assert.Equal(ModLoaderTypes.None, ModIndex.ModLoaderFromString(text));

    /*
     * The flag values are written into packwiz files, so they are part of the on-disk format. A file
     * saved by upstream and read here has to mean the same thing, which pins the numbers, not just
     * the names.
     */
    [Fact]
    public void LoaderFlagValuesMatchUpstream()
    {
        Assert.Equal(1, (int)ModLoaderTypes.NeoForge);
        Assert.Equal(2, (int)ModLoaderTypes.Forge);
        Assert.Equal(4, (int)ModLoaderTypes.Cauldron);
        Assert.Equal(8, (int)ModLoaderTypes.LiteLoader);
        Assert.Equal(16, (int)ModLoaderTypes.Fabric);
        Assert.Equal(32, (int)ModLoaderTypes.Quilt);
    }

    [Fact]
    public void ACombinationOfLoadersHasNoName()
    {
        var both = ModLoaderTypes.Fabric | ModLoaderTypes.Quilt;

        // Silently empty, as upstream's switch is. HasSingleModLoader is the guard against relying
        // on this, which is why it is a public question and not an internal detail.
        Assert.Equal(string.Empty, ModIndex.ToString(both));
        Assert.False(ModIndex.HasSingleModLoader(both));
    }

    [Fact]
    public void HasSingleModLoaderIsExactlyOneBit()
    {
        Assert.False(ModIndex.HasSingleModLoader(ModLoaderTypes.None));
        Assert.True(ModIndex.HasSingleModLoader(ModLoaderTypes.Forge));
        Assert.True(ModIndex.HasSingleModLoader(ModLoaderTypes.Quilt));
        Assert.False(ModIndex.HasSingleModLoader(ModLoaderTypes.Forge | ModLoaderTypes.Fabric));
        Assert.False(ModIndex.HasSingleModLoader(
            ModLoaderTypes.Forge | ModLoaderTypes.Fabric | ModLoaderTypes.Quilt));
    }

    // ================================================================== urls and overrides

    [Fact]
    public void MetaUrlsPointAtTheProjectPage()
    {
        Assert.Equal(
            "https://modrinth.com/mod/P7dR8mSH",
            ModIndex.GetMetaUrl(ResourceProvider.Modrinth, "P7dR8mSH"));

        // A redirect to the real slug-based page: the numeric id is the only thing always known.
        Assert.Equal(
            "https://www.curseforge.com/projects/306612",
            ModIndex.GetMetaUrl(ResourceProvider.Flame, "306612"));
    }

    /*
     * Quilt runs Fabric mods, so a Fabric mod on Quilt asks for Fabric API -- the wrong package
     * there. These four ids identify real projects and are upstream's data verbatim; getting one
     * wrong installs a library that breaks the pack.
     */
    [Fact]
    public void TheQuiltOverridesCoverBothProvidersForBothLibraries()
    {
        var overrides = ModIndex.GetOverrideDependencies();

        Assert.Equal(4, overrides.Count);

        Assert.Contains(new OverrideDependency("qvIfYCYJ", "P7dR8mSH", "API", ResourceProvider.Modrinth), overrides);
        Assert.Contains(new OverrideDependency("634179", "306612", "API", ResourceProvider.Flame), overrides);

        foreach (var provider in (ReadOnlySpan<ResourceProvider>)[ResourceProvider.Flame, ResourceProvider.Modrinth])
        {
            foreach (var slug in (ReadOnlySpan<string>)["API", "KotlinLibraries"])
            {
                Assert.Single(overrides, o => o.Provider == provider && o.Slug == slug);
            }
        }
    }

    // ================================================================== pack state

    /*
     * "Not asked yet" and "asked, and there are none" are different states that an empty list cannot
     * tell apart. Both selection questions answer false while versions are unloaded, so a UI polling
     * mid-fetch never reads a half-filled pack as an empty one.
     */
    [Fact]
    public void SelectionQuestionsAreFalseUntilVersionsAreLoaded()
    {
        var pack = new IndexedPack();
        pack.Versions.Add(new IndexedVersion { IsCurrentlySelected = true });

        Assert.False(pack.VersionsLoaded);
        Assert.False(pack.IsVersionSelected(0));
        Assert.False(pack.IsAnyVersionSelected());

        pack.VersionsLoaded = true;

        Assert.True(pack.IsVersionSelected(0));
        Assert.True(pack.IsAnyVersionSelected());
    }

    /// <summary>Upstream's <c>versions.at(index)</c> would abort here; returning false is kinder.</summary>
    [Fact]
    public void AnOutOfRangeVersionIndexIsNotSelectedRatherThanFatal()
    {
        var pack = new IndexedPack { VersionsLoaded = true };

        Assert.False(pack.IsVersionSelected(0));
        Assert.False(pack.IsVersionSelected(-1));
    }

    /*
     * The two "loaded" flags default OPPOSITE ways, which reads like a mistake and is not. Versions
     * always arrive eventually, so false means "still coming". Extra data does not exist at all for
     * some providers, so true means "nothing more is coming" -- otherwise the UI waits on an answer
     * that will never arrive.
     */
    [Fact]
    public void TheTwoLoadedFlagsDefaultOppositeWays()
    {
        var pack = new IndexedPack();

        Assert.False(pack.VersionsLoaded);
        Assert.True(pack.ExtraDataLoaded);
    }

    /// <summary>A file is usable unless the provider says otherwise, so the default is true.</summary>
    [Fact]
    public void AVersionIsPreferredByDefault()
        => Assert.True(new IndexedVersion().IsPreferred);
}
