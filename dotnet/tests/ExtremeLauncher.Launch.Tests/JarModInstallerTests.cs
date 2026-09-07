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
 * For JarModInstaller (PackProfile::installJarMods_internal). No upstream unit test exists, so these pin
 * the observable result: the jar is copied under a fresh id into jarmods/, a component patch that names
 * it as a local jar-mod library is written into patches/ AND reads back through the same version-file
 * parser the launcher uses, and the component is appended to the profile and mmc-pack.json.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class JarModInstallerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-jarmod-" + Guid.NewGuid().ToString("N"));

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

    private string MakeJar(string name, byte[] bytes)
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllBytes(path, bytes);

        return path;
    }

    [Fact]
    public void InstallingAJarModCopiesItWritesItsPatchAndAppendsTheComponent()
    {
        var instanceRoot = Path.Combine(_temp, "instance");
        Directory.CreateDirectory(instanceRoot);

        var paths = new InstancePaths(instanceRoot);
        var profile = new PackProfile(Context());
        profile.SetComponentVersion("net.minecraft", "1.20.1", important: true);

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var jar = MakeJar("CoolMod.jar", payload);

        Assert.True(JarModInstaller.Install(paths, profile, [jar]));

        // The jar was copied into jarmods/ under a fresh id, byte-for-byte.
        var copied = Assert.Single(Directory.GetFiles(paths.JarModsDir, "*.jar"));
        Assert.Equal(payload, File.ReadAllBytes(copied));

        // The component was appended to the profile with a local jar-mod library.
        var component = Assert.Single(profile.Components, c => c.Uid.StartsWith("custom.jarmod.", StringComparison.Ordinal));
        var library = Assert.Single(component.LocalFile!.JarMods);

        Assert.True(library.IsLocal);
        Assert.Equal("CoolMod", library.DisplayNameOverride);
        Assert.Equal(Path.GetFileName(copied), library.Filename);

        // The patch file was written AND reads back through the launcher's own version-file parser.
        var patchPath = Path.Combine(paths.PatchesDir, component.Uid + ".json");
        Assert.True(File.Exists(patchPath));

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(patchPath))!;
        var reread = OneSixVersionFormat.VersionFileFromJson(root, patchPath);

        Assert.Equal(component.Uid, reread.Uid);
        var rereadLib = Assert.Single(reread.JarMods);
        Assert.Equal(library.Filename, rereadLib.Filename);
        Assert.True(rereadLib.IsLocal);

        // mmc-pack.json now lists the jar-mod component too.
        var reloaded = new PackProfile(Context());
        Assert.True(reloaded.Load(paths.PackProfilePath));
        Assert.Contains(reloaded.Components, c => c.Uid == component.Uid);
    }

    [Fact]
    public void EachJarGetsItsOwnComponentAndFile()
    {
        var instanceRoot = Path.Combine(_temp, "instance");
        Directory.CreateDirectory(instanceRoot);

        var paths = new InstancePaths(instanceRoot);
        var profile = new PackProfile(Context());

        Assert.True(JarModInstaller.Install(
            paths, profile, [MakeJar("a.jar", [1]), MakeJar("b.jar", [2])]));

        Assert.Equal(2, Directory.GetFiles(paths.JarModsDir, "*.jar").Length);
        Assert.Equal(2, profile.Components.Count(c => c.Uid.StartsWith("custom.jarmod.", StringComparison.Ordinal)));
    }

    [Fact]
    public void CompleteBaseNameKeepsInnerDotsInTheDisplayName()
    {
        var instanceRoot = Path.Combine(_temp, "instance");
        Directory.CreateDirectory(instanceRoot);

        var paths = new InstancePaths(instanceRoot);
        var profile = new PackProfile(Context());

        Assert.True(JarModInstaller.Install(paths, profile, [MakeJar("some.cool.mod.jar", [9])]));

        var component = Assert.Single(profile.Components, c => c.Uid.StartsWith("custom.jarmod.", StringComparison.Ordinal));

        // Only the final ".jar" is dropped, matching Qt's completeBaseName().
        Assert.Equal("some.cool.mod", Assert.Single(component.LocalFile!.JarMods).DisplayNameOverride);
    }
}
