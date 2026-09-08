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
 * For AtlModPlanner, the pure core of ATLPackInstallTask's downloadMods: which of a version's mods are
 * installed, where each is fetched from, and what becomes of it. The download-type routing, the
 * client/optional filtering, the placement folder, and the extract/decompile classification are what
 * this pins.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AtlModPlannerTests
{
    private const string Server = "https://atl.invalid/atl/";

    private static AtlVersionMod Mod(
        string name,
        AtlModType type = AtlModType.Mods,
        AtlDownloadType download = AtlDownloadType.Direct,
        string url = "https://direct.invalid/m.jar",
        string file = "m.jar",
        bool client = true,
        bool optional = false)
        => new()
        {
            Name = name,
            Type = type,
            TypeRaw = type.ToString().ToLowerInvariant(),
            Download = download,
            DownloadRaw = download.ToString().ToLowerInvariant(),
            Url = url,
            File = file,
            Md5 = "abc",
            Client = client,
            Optional = optional,
        };

    private static readonly HashSet<string> None = [];

    [Fact]
    public void AServerModHangsOffTheCdnAndADirectModUsesItsOwnUrl()
    {
        var mods = new[]
        {
            Mod("A", download: AtlDownloadType.Server, url: "packs/x/mods/a.jar"),
            Mod("B", download: AtlDownloadType.Direct, url: "https://direct.invalid/b.jar"),
        };

        var plan = AtlModPlanner.Build(mods, None, "1.20.1", Server);

        Assert.Equal("https://atl.invalid/atl/packs/x/mods/a.jar", plan.Downloads[0].Url);
        Assert.Equal("https://direct.invalid/b.jar", plan.Downloads[1].Url);
    }

    [Fact]
    public void ABrowserModIsBlocked()
    {
        var plan = AtlModPlanner.Build([Mod("A", download: AtlDownloadType.Browser)], None, "1.20.1", Server);

        Assert.Empty(plan.Downloads);
        Assert.Equal("A", Assert.Single(plan.Blocked).Name);
    }

    [Fact]
    public void AServerSideOnlyModIsSkipped()
    {
        var plan = AtlModPlanner.Build([Mod("A", client: false)], None, "1.20.1", Server);

        Assert.Empty(plan.Downloads);
        Assert.Empty(plan.Blocked);
    }

    [Fact]
    public void AnUnchosenOptionalModIsLeftOutEntirely()
    {
        var mods = new[] { Mod("Keep", file: "keep.jar"), Mod("Extra", optional: true, file: "extra.jar") };

        var plan = AtlModPlanner.Build(mods, None, "1.20.1", Server);

        // Only the required mod is installed; the unchosen optional is not present at all.
        Assert.Equal("minecraft/mods/keep.jar", Assert.Single(plan.Downloads).TargetPath);
    }

    [Fact]
    public void AChosenOptionalModIsInstalled()
    {
        var mods = new[] { Mod("Extra", optional: true) };

        var plan = AtlModPlanner.Build(mods, new HashSet<string> { "Extra" }, "1.20.1", Server);

        Assert.Single(plan.Downloads);
    }

    [Fact]
    public void APlainModLandsInItsTypeFolder()
    {
        var plan = AtlModPlanner.Build([Mod("A", type: AtlModType.Mods, file: "a.jar")], None, "1.20.1", Server);

        var download = Assert.Single(plan.Downloads);
        Assert.Equal(AtlModAction.Place, download.Action);
        Assert.Equal("minecraft/mods/a.jar", download.TargetPath);
        Assert.False(download.IsJarMod);
    }

    [Theory]
    [InlineData(AtlModType.Forge)]
    [InlineData(AtlModType.Jar)]
    public void AForgeOrJarModIsAJarMod(AtlModType type)
    {
        var plan = AtlModPlanner.Build([Mod("A", type: type, file: "a.jar")], None, "1.20.1", Server);

        var download = Assert.Single(plan.Downloads);
        Assert.Equal("minecraft/jarmods/a.jar", download.TargetPath);
        Assert.True(download.IsJarMod);
    }

    [Fact]
    public void ADependencyLandsInThePerVersionModsFolder()
    {
        var plan = AtlModPlanner.Build([Mod("A", type: AtlModType.Dependency, file: "a.jar")], None, "1.12.2", Server);

        Assert.Equal("minecraft/mods/1.12.2/a.jar", Assert.Single(plan.Downloads).TargetPath);
    }

    [Fact]
    public void AnExtractModIsClassifiedForExtractionWithNoTarget()
    {
        var plan = AtlModPlanner.Build([Mod("A", type: AtlModType.Extract)], None, "1.20.1", Server);

        var download = Assert.Single(plan.Downloads);
        Assert.Equal(AtlModAction.Extract, download.Action);
        Assert.Null(download.TargetPath);
    }

    [Fact]
    public void ADecompModIsClassifiedForDecompilation()
        => Assert.Equal(
            AtlModAction.Decompile,
            Assert.Single(AtlModPlanner.Build([Mod("A", type: AtlModType.Decomp)], None, "1.20.1", Server).Downloads).Action);

    [Fact]
    public void AnUnknownDownloadTypeThrows()
        => Assert.Throws<LauncherException>(
            () => AtlModPlanner.Build([Mod("A", download: AtlDownloadType.Unknown)], None, "1.20.1", Server));

    [Fact]
    public void OptionalModsListsTheOptionalOnesByName()
    {
        var mods = new[] { Mod("Core"), Mod("Extra", optional: true), Mod("Also", optional: true) };

        Assert.Equal(["Extra", "Also"], AtlModPlanner.OptionalMods(mods));
    }
}
