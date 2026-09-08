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
 * For FlamePackBuilder.BuildFromExtracted, the no-network core of the CurseForge import: an extracted
 * pack (a parsed manifest plus an overrides folder) becomes a staged instance. These build a pack by
 * hand and check what the import decides — the components, the overrides becoming the game folder, the
 * manifest filed for updates, and the name-based icon defaults.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class FlamePackBuilderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-flameinst-" + Guid.NewGuid().ToString("N"));

    public FlamePackBuilderTests() => Directory.CreateDirectory(_temp);

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

    private static FlamePackManifest Manifest(string name, string mcVersion, string? loaderId)
    {
        var manifest = new FlamePackManifest { Name = name };
        manifest.Minecraft.Version = mcVersion;

        if (loaderId is not null)
        {
            manifest.Minecraft.ModLoaders.Add(new FlameModloader { Id = loaderId, Primary = true });
        }

        return manifest;
    }

    [Fact]
    public void AForgePackGetsMinecraftAndForgeAndMovesOverridesIntoTheGameFolder()
    {
        var paths = NewInstance();

        var overrides = Path.Combine(paths.InstanceRoot, "overrides");
        Directory.CreateDirectory(overrides);
        File.WriteAllText(Path.Combine(overrides, "options.txt"), "fov:1.0");
        File.WriteAllText(Path.Combine(paths.InstanceRoot, "manifest.json"), "{}");

        FlamePackBuilder.BuildFromExtracted(paths, Manifest("Some Pack", "1.20.1", "forge-47.1.0"), Context());

        // The overrides folder is now the game folder.
        Assert.True(File.Exists(Path.Combine(paths.GameRoot, "options.txt")));
        Assert.False(Directory.Exists(Path.Combine(paths.InstanceRoot, "overrides")));

        // The manifest is filed under flame/ for later update checks.
        Assert.True(File.Exists(Path.Combine(paths.InstanceRoot, "flame", "manifest.json")));
        Assert.False(File.Exists(Path.Combine(paths.InstanceRoot, "manifest.json")));

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.20.1", profile.GetComponentVersion("net.minecraft"));
        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
        Assert.Equal("47.1.0", profile.GetComponentVersion("net.minecraftforge"));

        var cfg = File.ReadAllText(paths.ConfigPath);
        Assert.Contains("name=Some Pack", cfg, StringComparison.Ordinal);
        Assert.Contains("InstanceType=OneSix", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypedNameOverridesTheManifestName()
    {
        var paths = NewInstance();
        Directory.CreateDirectory(Path.Combine(paths.InstanceRoot, "overrides"));

        FlamePackBuilder.BuildFromExtracted(
            paths, Manifest("Manifest Name", "1.20.1", null), Context(), displayName: "My Pack");

        Assert.Contains("name=My Pack", File.ReadAllText(paths.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingOverridesFolderIsNotFatal()
    {
        var paths = NewInstance();

        // No overrides folder at all — a pack imported before. Upstream warns and carries on.
        FlamePackBuilder.BuildFromExtracted(paths, Manifest("Bare", "1.7.10", null), Context());

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.7.10", profile.GetComponentVersion("net.minecraft"));
    }

    [Fact]
    public void ANeoForgePackUsesTheNeoForgeComponent()
    {
        var paths = NewInstance();
        Directory.CreateDirectory(Path.Combine(paths.InstanceRoot, "overrides"));

        FlamePackBuilder.BuildFromExtracted(paths, Manifest("NF", "1.20.4", "neoforge-20.4.190"), Context());

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("20.4.190", profile.GetComponentVersion("net.neoforged"));
    }

    [Theory]
    [InlineData("default", "All the Mods 9", "default")]
    [InlineData("default", "Direwolf20 1.20", "steve")]
    [InlineData("default", "FTB Skies", "ftb_logo")]
    [InlineData("default", "Feed The Beast Infinity", "ftb_logo")]
    [InlineData("my_icon", "Direwolf20 1.20", "my_icon")] // a chosen icon always wins
    public void TheIconDefaultsByPackName(string chosen, string packName, string expected)
        => Assert.Equal(expected, FlamePackBuilder.ResolveIconKey(chosen, packName));
}
