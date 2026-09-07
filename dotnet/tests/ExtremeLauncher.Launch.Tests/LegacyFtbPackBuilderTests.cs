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
 * For LegacyFtbPackBuilder.BuildFromArchive (legacy_ftb/PackInstallTask). No upstream unit test exists;
 * these drive the three outcomes against hand-built archives and no network: a Forge pack (pack.json),
 * a jar-mod pack (instMods/), and a pack with neither -- which must fail the way upstream does.
 */

using System.IO.Compression;
using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LegacyFtbPackBuilderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-ftbinst-" + Guid.NewGuid().ToString("N"));

    public LegacyFtbPackBuilderTests() => Directory.CreateDirectory(_temp);

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

    private string MakeArchive(string name, IReadOnlyDictionary<string, string> entries)
    {
        var path = Path.Combine(_temp, name);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entryName, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(content);
        }

        return path;
    }

    private InstancePaths NewInstance()
    {
        var root = Path.Combine(_temp, "instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        return new InstancePaths(root);
    }

    [Fact]
    public void AForgePackGetsMinecraftAndForgeComponents()
    {
        var archive = MakeArchive("forge.zip", new Dictionary<string, string>
        {
            ["minecraft/pack.json"] =
                """{ "libraries": [ { "name": "net.minecraftforge:forge:1.20.1-47.1.0" } ] }""",
            ["minecraft/options.txt"] = "fov:1.0",
        });

        var paths = NewInstance();
        var pack = new LegacyFtbModpack { Name = "Direwolf20", McVersion = "1.20.1", Type = LegacyFtbPackType.Public };

        LegacyFtbPackBuilder.BuildFromArchive(paths, archive, pack, Context());

        // The game folder was moved up, and the consumed pack.json was removed.
        Assert.True(File.Exists(Path.Combine(paths.InstanceRoot, "minecraft", "options.txt")));
        Assert.False(File.Exists(Path.Combine(paths.InstanceRoot, "minecraft", "pack.json")));
        Assert.False(Directory.Exists(Path.Combine(paths.InstanceRoot, "unzip")));

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.20.1", profile.GetComponentVersion("net.minecraft"));
        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
        Assert.Equal("47.1.0", profile.GetComponentVersion("net.minecraftforge"));

        var cfg = File.ReadAllText(Path.Combine(paths.InstanceRoot, "instance.cfg"));
        Assert.Contains("name=Direwolf20", cfg, StringComparison.Ordinal);
        Assert.Contains("InstanceType=OneSix", cfg, StringComparison.Ordinal);
        Assert.Contains("iconKey=ftb_logo", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void AJarModPackInstallsItsInstMods()
    {
        var archive = MakeArchive("jarmod.zip", new Dictionary<string, string>
        {
            ["instMods/oldskool.jar"] = "PK-not-really",
            ["minecraft/options.txt"] = "fov:1.0",
        });

        var paths = NewInstance();
        var pack = new LegacyFtbModpack { Name = "Old Pack", McVersion = "1.4.7", Type = LegacyFtbPackType.Public };

        LegacyFtbPackBuilder.BuildFromArchive(paths, archive, pack, Context());

        Assert.Single(Directory.GetFiles(paths.JarModsDir, "*.jar"));

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.4.7", profile.GetComponentVersion("net.minecraft"));
        Assert.Contains(profile.Components, c => c.Uid.StartsWith("custom.jarmod.", StringComparison.Ordinal));
    }

    [Fact]
    public void APackWithNoInstallMethodFails()
    {
        var archive = MakeArchive("bare.zip", new Dictionary<string, string>
        {
            ["minecraft/options.txt"] = "fov:1.0",
        });

        var paths = NewInstance();
        var pack = new LegacyFtbModpack { Name = "Bare", McVersion = "1.20.1", Type = LegacyFtbPackType.Public };

        Assert.Throws<LauncherException>(
            () => LegacyFtbPackBuilder.BuildFromArchive(paths, archive, pack, Context()));
    }
}
