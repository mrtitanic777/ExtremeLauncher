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
 * For TechnicPackBuilder.BuildFromStaging (technic/TechnicPackProcessor). No upstream unit test exists;
 * these drive each of the branches the processor picks between against hand-built staging folders and
 * no network: a Solder bin/version.json, a modpack.jar carrying version.json (with the Minecraft
 * version hidden in fmlversion.properties), a pre-Forge modpack.jar that is itself a jar mod (Forge
 * read from forgeversion.properties), the Vanilla pack with no bin at all, and the jar-mod pack with
 * no Minecraft version -- which must fail the way upstream does.
 */

using System.IO.Compression;
using System.Text;

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class TechnicPackBuilderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-technicinst-" + Guid.NewGuid().ToString("N"));

    public TechnicPackBuilderTests() => Directory.CreateDirectory(_temp);

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

    /// <summary>A fresh instance whose staging already holds <c>minecraft/</c>, as the extractor leaves it.</summary>
    private (InstancePaths Paths, string Bin) NewStaging()
    {
        var root = Path.Combine(_temp, "instance-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "minecraft", "bin");
        Directory.CreateDirectory(bin);

        return (new InstancePaths(root), bin);
    }

    private static void WriteZip(string path, IReadOnlyDictionary<string, string> entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
    }

    private static PackProfile LoadProfile(InstancePaths paths)
    {
        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));

        return profile;
    }

    [Fact]
    public void ASolderVersionJsonOnDiskGivesMinecraftAndForge()
    {
        var (paths, bin) = NewStaging();
        File.WriteAllText(Path.Combine(bin, "version.json"),
            """
            {
              "inheritsFrom": "1.12.2",
              "libraries": [ { "name": "net.minecraftforge:forge:1.12.2-14.23.5.2860" } ]
            }
            """);

        TechnicPackBuilder.BuildFromStaging(paths, Context(), "Sky Factory");

        var profile = LoadProfile(paths);
        Assert.Equal("1.12.2", profile.GetComponentVersion("net.minecraft"));
        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
        Assert.Equal("14.23.5.2860", profile.GetComponentVersion("net.minecraftforge"));

        var cfg = File.ReadAllText(paths.ConfigPath);
        Assert.Contains("name=Sky Factory", cfg, StringComparison.Ordinal);
        Assert.Contains("InstanceType=OneSix", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void AModpackJarVersionJsonTakesItsMinecraftFromFmlProperties()
    {
        var (paths, bin) = NewStaging();

        // An old FML version.json omits inheritsFrom; the Minecraft version hides in fmlversion.properties.
        WriteZip(Path.Combine(bin, "modpack.jar"), new Dictionary<string, string>
        {
            ["version.json"] =
                """{ "libraries": [ { "name": "net.minecraftforge:forge:1.5.2-7.8.1.738" } ] }""",
            ["fmlversion.properties"] = "fmlbuild.mcversion=1.5.2\n",
        });

        TechnicPackBuilder.BuildFromStaging(paths, Context(), "Old FML Pack");

        var profile = LoadProfile(paths);
        Assert.Equal("1.5.2", profile.GetComponentVersion("net.minecraft"));
        Assert.Equal("7.8.1.738", profile.GetComponentVersion("net.minecraftforge"));
    }

    [Fact]
    public void APreForgeModpackJarBecomesAJarModWithForgeFromProperties()
    {
        var (paths, bin) = NewStaging();

        // No version.json inside: the jar itself is the jar mod, and Forge comes from properties.
        WriteZip(Path.Combine(bin, "modpack.jar"), new Dictionary<string, string>
        {
            ["forgeversion.properties"] =
                "forge.major.number=6\nforge.minor.number=6\nforge.revision.number=2\nforge.build.number=534\n",
            ["net/minecraft/Foo.class"] = "not really bytecode",
        });

        TechnicPackBuilder.BuildFromStaging(paths, Context(), "Tekkit Classic", minecraftVersion: "1.4.7");

        var profile = LoadProfile(paths);
        Assert.Equal("1.4.7", profile.GetComponentVersion("net.minecraft"));
        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
        Assert.Equal("6.6.2.534", profile.GetComponentVersion("net.minecraftforge"));
        Assert.Contains(profile.Components, c => c.Uid.StartsWith("custom.jarmod.", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(paths.JarModsDir, "*.jar"));
    }

    [Fact]
    public void TheVanillaPackWithNoBinIsJustMinecraft()
    {
        var root = Path.Combine(_temp, "instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "minecraft"));
        var paths = new InstancePaths(root);

        TechnicPackBuilder.BuildFromStaging(paths, Context(), "Vanilla", minecraftVersion: "1.6.4");

        var profile = LoadProfile(paths);
        Assert.Equal("1.6.4", profile.GetComponentVersion("net.minecraft"));
        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
        Assert.Equal(string.Empty, profile.GetComponentVersion("net.minecraftforge"));
    }

    [Fact]
    public void AJarModPackWithNoKnownMinecraftVersionFails()
    {
        var (paths, bin) = NewStaging();

        WriteZip(Path.Combine(bin, "modpack.jar"), new Dictionary<string, string>
        {
            ["net/minecraft/Foo.class"] = "not really bytecode",
        });

        Assert.Throws<LauncherException>(
            () => TechnicPackBuilder.BuildFromStaging(paths, Context(), "Broken", minecraftVersion: ""));
    }

    [Fact]
    public void AnIconKeyIsWrittenWhenItIsNotTheDefault()
    {
        var (paths, bin) = NewStaging();
        File.WriteAllText(Path.Combine(bin, "version.json"),
            """{ "inheritsFrom": "1.7.10" }""");

        TechnicPackBuilder.BuildFromStaging(paths, Context(), "Iconned", iconKey: "technic_logo");

        var cfg = File.ReadAllText(paths.ConfigPath);
        Assert.Contains("iconKey=technic_logo", cfg, StringComparison.Ordinal);
    }
}
