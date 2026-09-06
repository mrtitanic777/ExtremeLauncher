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
 * Every box is INVERTED -- copying works by walking the source and skipping matches -- so the tests
 * are mostly "unticking this excludes exactly these paths and nothing else". Getting one wrong copies
 * a user's saves into a fresh instance, or leaves them behind when they asked for them.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstanceCopyPrefsTests
{
    /// <summary>Everything ticked is the default, and it excludes nothing at all.</summary>
    [Fact]
    public void CopyingEverythingProducesNoPattern()
        => Assert.Equal(string.Empty, new InstanceCopyPrefs().GetSelectedFiltersAsRegex());

    [Fact]
    public void UntickingSavesExcludesOnlySaves()
        => Assert.Equal(
            "[.]?minecraft/saves",
            new InstanceCopyPrefs { CopySaves = false }.GetSelectedFiltersAsRegex());

    /// <summary>Two names for one thing: Minecraft renamed texture packs to resource packs in 1.6.</summary>
    [Fact]
    public void UntickingResourcePacksExcludesBothNames()
        => Assert.Equal(
            "[.]?minecraft/resourcepacks|[.]?minecraft/texturepacks",
            new InstanceCopyPrefs { CopyResourcePacks = false }.GetSelectedFiltersAsRegex());

    /// <summary>Including the backup Minecraft writes, which would otherwise restore the list.</summary>
    [Fact]
    public void UntickingServersExcludesTheBackupToo()
        => Assert.Equal(
            "[.]?minecraft/servers.dat|[.]?minecraft/servers.dat_old|[.]?minecraft/server-resource-packs",
            new InstanceCopyPrefs { CopyServers = false }.GetSelectedFiltersAsRegex());

    /*
     * CONFIG GOES WITH THE MODS. Configs for mods that are not there produce an instance that fails
     * differently from a clean one, which is harder to diagnose than either.
     */
    [Fact]
    public void UntickingModsAlsoExcludesConfig()
    {
        var pattern = new InstanceCopyPrefs { CopyMods = false }.GetSelectedFiltersAsRegex();

        Assert.Equal(
            "[.]?minecraft/coremods|[.]?minecraft/mods|[.]?minecraft/config",
            pattern);
    }

    /// <summary>The anchor leads and rejoins between every alternative.</summary>
    [Fact]
    public void EveryAlternativeIsAnchoredUnderTheGameDirectory()
    {
        var prefs = new InstanceCopyPrefs { CopySaves = false, CopyScreenshots = false, CopyShaderPacks = false };

        foreach (var alternative in prefs.GetSelectedFiltersAsRegex().Split('|'))
        {
            Assert.StartsWith(InstanceCopyPrefs.MinecraftRoot, alternative, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AdditionalFiltersAreAnchoredTheSameWay()
        => Assert.Equal(
            "[.]?minecraft/saves|[.]?minecraft/custom",
            new InstanceCopyPrefs { CopySaves = false }.GetSelectedFiltersAsRegex(["custom"]));

    /// <summary>Additional filters alone still produce a pattern.</summary>
    [Fact]
    public void AdditionalFiltersWorkWithNothingUnticked()
        => Assert.Equal(
            "[.]?minecraft/custom",
            new InstanceCopyPrefs().GetSelectedFiltersAsRegex(["custom"]));

    // ================================================================== matching

    /*
     * The dot is OPTIONAL in the anchor, because the folder is ".minecraft" in some layouts and
     * "minecraft" in others. Both have to match, or a copy silently brings everything.
     */
    [Theory]
    [InlineData(".minecraft/saves/World")]
    [InlineData("minecraft/saves/World")]
    public void BothSpellingsOfTheGameDirectoryMatch(string path)
        => Assert.True(new InstanceCopyPrefs { CopySaves = false }.IsExcluded(path));

    /// <summary>An unanchored pattern would match a user's own folder anywhere in the tree.</summary>
    [Fact]
    public void APathOutsideTheGameDirectoryIsNotExcluded()
        => Assert.False(new InstanceCopyPrefs { CopySaves = false }.IsExcluded("backups/saves/World"));

    [Fact]
    public void AnUntickedBoxDoesNotExcludeTheTickedOnes()
    {
        var prefs = new InstanceCopyPrefs { CopySaves = false };

        Assert.True(prefs.IsExcluded(".minecraft/saves/World"));
        Assert.False(prefs.IsExcluded(".minecraft/mods/a.jar"));
        Assert.False(prefs.IsExcluded(".minecraft/screenshots/a.png"));
    }

    [Fact]
    public void WithNothingExcludedNoPathMatches()
        => Assert.False(new InstanceCopyPrefs().IsExcluded(".minecraft/saves/World"));

    /// <summary>Windows separators are normalised before matching, since the pattern uses slashes.</summary>
    [Fact]
    public void BackslashPathsAreMatchedToo()
        => Assert.True(new InstanceCopyPrefs { CopyMods = false }.IsExcluded(@".minecraft\mods\a.jar"));
}
