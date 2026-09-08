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
 * For AtlPackBuilder.BuildInstance, the staging tail of ATLPackInstallTask's install(): a resolved
 * version becomes a pack profile and instance.cfg. What is pinned is the components (Minecraft plus the
 * loader), any jar mods laid in, and the managed-pack fields that let the instance be updated later.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AtlPackBuilderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-atlinst-" + Guid.NewGuid().ToString("N"));

    public AtlPackBuilderTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static RuntimeContext Context() => new()
    {
        System = "windows",
        JavaArchitecture = "64",
        JavaRealArchitecture = "x86_64",
    };

    private InstancePaths NewInstance()
    {
        var root = Path.Combine(_temp, "instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        return new InstancePaths(root);
    }

    private static AtlPackVersion Version(string minecraft, string loaderType = "", string loaderVersion = "")
    {
        var version = new AtlPackVersion { Minecraft = minecraft };
        version.Loader.Type = loaderType;
        version.Loader.Version = loaderVersion;

        return version;
    }

    [Fact]
    public void AForgePackGetsItsComponentsAndManagedPackFields()
    {
        var paths = NewInstance();

        AtlPackBuilder.BuildInstance(
            paths, Version("1.12.2", "forge", "14.23.5.2860"), Context(),
            packName: "Sky Factory 4", packSafeName: "SkyFactory4", versionName: "4.2.2");

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.12.2", profile.GetComponentVersion("net.minecraft"));
        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
        Assert.Equal("14.23.5.2860", profile.GetComponentVersion("net.minecraftforge"));

        var cfg = File.ReadAllText(paths.ConfigPath);
        Assert.Contains("name=Sky Factory 4", cfg, StringComparison.Ordinal);
        Assert.Contains("InstanceType=OneSix", cfg, StringComparison.Ordinal);
        Assert.Contains("ManagedPack=true", cfg, StringComparison.Ordinal);
        Assert.Contains("ManagedPackType=atlauncher", cfg, StringComparison.Ordinal);
        Assert.Contains("ManagedPackID=SkyFactory4", cfg, StringComparison.Ordinal);
        Assert.Contains("ManagedPackName=Sky Factory 4", cfg, StringComparison.Ordinal);
        Assert.Contains("ManagedPackVersionID=4.2.2", cfg, StringComparison.Ordinal);
        Assert.Contains("ManagedPackVersionName=4.2.2", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void JarModsAreLaidIn()
    {
        var paths = NewInstance();

        var jar = Path.Combine(_temp, "oldskool.jar");
        File.WriteAllText(jar, "PK-not-really");

        AtlPackBuilder.BuildInstance(
            paths, Version("1.4.7"), Context(),
            packName: "Tekkit", packSafeName: "Tekkit", versionName: "1.0", jarMods: [jar]);

        Assert.Single(Directory.GetFiles(paths.JarModsDir, "*.jar"));

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.4.7", profile.GetComponentVersion("net.minecraft"));
        Assert.Contains(profile.Components, c => c.Uid.StartsWith("custom.jarmod.", StringComparison.Ordinal));
    }

    [Fact]
    public void ATypedNameOverridesThePackName()
    {
        var paths = NewInstance();

        AtlPackBuilder.BuildInstance(
            paths, Version("1.20.1"), Context(),
            packName: "Pack Name", packSafeName: "PackName", versionName: "1", displayName: "My Instance");

        Assert.Contains("name=My Instance", File.ReadAllText(paths.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIconKeyIsWrittenWhenNotDefault()
    {
        var paths = NewInstance();

        AtlPackBuilder.BuildInstance(
            paths, Version("1.20.1"), Context(),
            packName: "P", packSafeName: "P", versionName: "1", iconKey: "atlauncher_logo");

        Assert.Contains("iconKey=atlauncher_logo", File.ReadAllText(paths.ConfigPath), StringComparison.Ordinal);
    }
}
