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
 * For AtlModExtractor (ATLPackInstallTask::extractMods): the mod types that are unpacked rather than
 * dropped in. An extract mod's contents (or one folder of them) go to a target from its extractTo; a
 * texture/resource-pack extract goes to a fixed "extracted" folder; a decomp mod yields one named file.
 */

using System.IO.Compression;

using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AtlModExtractorTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-atlextract-" + Guid.NewGuid().ToString("N"));

    public AtlModExtractorTests() => Directory.CreateDirectory(_temp);

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

    private InstancePaths NewInstance()
    {
        var root = Path.Combine(_temp, "instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        return new InstancePaths(root);
    }

    private string MakeArchive(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_temp, name);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entryPath, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(entryPath).Open());
            w.Write(content);
        }

        return path;
    }

    private static AtlVersionMod ExtractMod(string extractTo = "mods", string extractFolder = "")
        => new()
        {
            Name = "X",
            Type = AtlModType.Extract,
            ExtractTo = extractTo == "mods" ? AtlModType.Mods : AtlModType.Coremods,
            ExtractToRaw = extractTo,
            ExtractFolder = extractFolder,
        };

    private static string InGame(InstancePaths paths, string relative)
        => Path.Combine(paths.GameRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void AnExtractModUnpacksTheWholeArchiveIntoItsTargetFolder()
    {
        var paths = NewInstance();
        var archive = MakeArchive("m.zip", ("a.txt", "1"), ("sub/b.txt", "2"));

        AtlModExtractor.Extract(paths, ExtractMod(extractTo: "mods"), archive, "1.12.2");

        Assert.True(File.Exists(InGame(paths, "mods/a.txt")));
        Assert.True(File.Exists(InGame(paths, "mods/sub/b.txt")));
    }

    [Fact]
    public void AnExtractModCanRestrictItselfToOneFolderOfTheArchive()
    {
        var paths = NewInstance();
        var archive = MakeArchive("m.zip", ("config/x.cfg", "1"), ("other/y.txt", "2"));

        // A leading slash on the extract folder is tolerated, as upstream strips it.
        AtlModExtractor.Extract(paths, ExtractMod(extractTo: "coremods", extractFolder: "/config"), archive, "1.12.2");

        // The "config/" prefix is stripped: the file lands directly under the target.
        Assert.True(File.Exists(InGame(paths, "coremods/x.cfg")));
        Assert.False(File.Exists(InGame(paths, "coremods/other/y.txt")));
    }

    [Theory]
    [InlineData(AtlModType.TexturePackExtract, "texturepacks/extracted")]
    [InlineData(AtlModType.ResourcePackExtract, "resourcepacks/extracted")]
    public void TexturePackAndResourcePackExtractsGoToFixedFolders(AtlModType type, string expected)
    {
        var paths = NewInstance();
        var archive = MakeArchive("m.zip", ("pack.png", "1"));

        AtlModExtractor.Extract(paths, new AtlVersionMod { Name = "X", Type = type }, archive, "1.12.2");

        Assert.True(File.Exists(InGame(paths, $"{expected}/pack.png")));
    }

    [Fact]
    public void ADecompModTakesTheOneNamedFileOut()
    {
        var paths = NewInstance();
        var archive = MakeArchive("m.zip", ("inner.jar", "jardata"), ("ignored.txt", "no"));

        var mod = new AtlVersionMod
        {
            Name = "X",
            Type = AtlModType.Decomp,
            DecompType = AtlModType.Mods,
            DecompTypeRaw = "mods",
            DecompFile = "inner.jar",
        };

        AtlModExtractor.Decompile(paths, mod, archive, "1.12.2");

        Assert.Equal("jardata", File.ReadAllText(InGame(paths, "mods/inner.jar")));
        Assert.False(File.Exists(InGame(paths, "mods/ignored.txt")));
    }

    [Fact]
    public void ExtractTargetFolderIsFixedForTexturePacks()
        => Assert.Equal(
            "texturepacks/extracted",
            AtlModExtractor.ExtractTargetFolder(new AtlVersionMod { Type = AtlModType.TexturePackExtract }, "1.12.2"));
}
