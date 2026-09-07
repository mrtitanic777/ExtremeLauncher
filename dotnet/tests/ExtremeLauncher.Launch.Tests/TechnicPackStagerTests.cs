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
 * For TechnicPackStager.StageAndBuild, the extract-and-build step shared by the single-zip and Solder
 * install tasks (technic/SingleZipPackInstallTask, technic/SolderPackInstallTask). No upstream unit
 * test exists; these drive it against hand-built archives and no network: one archive that carries the
 * whole pack, several archives layered as a Solder pack lays its mods, and a missing archive -- which
 * must fail.
 */

using System.IO.Compression;

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class TechnicPackStagerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-technicstage-" + Guid.NewGuid().ToString("N"));

    public TechnicPackStagerTests() => Directory.CreateDirectory(_temp);

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
    public void OneArchiveIsExtractedIntoTheGameFolderAndBuilt()
    {
        // A single-zip pack: the whole thing is one archive rooted at the game folder.
        var archive = MakeArchive("pack.zip", new Dictionary<string, string>
        {
            ["bin/version.json"] = """{ "inheritsFrom": "1.12.2" }""",
            ["config/foo.cfg"] = "hello",
        });

        var paths = NewInstance();
        TechnicPackStager.StageAndBuild(paths, Context(), [archive], "SingleZip Pack");

        Assert.True(File.Exists(Path.Combine(paths.GameRoot, "config", "foo.cfg")));

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.12.2", profile.GetComponentVersion("net.minecraft"));
    }

    [Fact]
    public void SeveralArchivesAreLayeredInOrder()
    {
        // A Solder pack: each mod is its own archive, extracted in order into the game folder.
        var first = MakeArchive("0.zip", new Dictionary<string, string>
        {
            ["bin/version.json"] = """{ "inheritsFrom": "1.7.10" }""",
            ["mods/a.jar"] = "aaa",
            ["config/shared.cfg"] = "from-first",
        });
        var second = MakeArchive("1.zip", new Dictionary<string, string>
        {
            ["mods/b.jar"] = "bbb",
            ["config/shared.cfg"] = "from-second",
        });

        var paths = NewInstance();
        TechnicPackStager.StageAndBuild(paths, Context(), [first, second], "Solder Pack");

        Assert.True(File.Exists(Path.Combine(paths.GameRoot, "mods", "a.jar")));
        Assert.True(File.Exists(Path.Combine(paths.GameRoot, "mods", "b.jar")));

        // A later archive wins where two collide.
        Assert.Equal("from-second", File.ReadAllText(Path.Combine(paths.GameRoot, "config", "shared.cfg")));

        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.7.10", profile.GetComponentVersion("net.minecraft"));
    }

    [Fact]
    public void AMissingArchiveFails()
    {
        var paths = NewInstance();

        Assert.Throws<LauncherException>(() => TechnicPackStager.StageAndBuild(
            paths, Context(), [Path.Combine(_temp, "does-not-exist.zip")], "Nope"));
    }
}
