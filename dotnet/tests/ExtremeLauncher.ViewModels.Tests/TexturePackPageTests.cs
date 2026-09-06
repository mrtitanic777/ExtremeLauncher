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
 * The legacy texture-pack page: the same ModsPageViewModel machinery pointed at the "texturepacks"
 * folder and the pre-1.6 pack.txt format. These pin the fourth ResourceFolderKind -- that it reads the
 * right folder, names itself the right thing, and manages files the same way the mods page does.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class TexturePackPageTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-tp-" + Guid.NewGuid().ToString("N"));

    private readonly string _folder;

    public TexturePackPageTests()
    {
        _folder = Path.Combine(_temp, "texturepacks");
        Directory.CreateDirectory(_folder);
    }

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

    /// <summary>A legacy texture pack as a zip: the format is a zip with a pack.txt inside.</summary>
    private string MakeZipPack(string fileName, string description)
    {
        var path = Path.Combine(_folder, fileName);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = archive.CreateEntry("pack.txt").Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(description);

        return path;
    }

    /// <summary>A legacy texture pack as a folder: a directory holding a pack.txt.</summary>
    private string MakeFolderPack(string name, string description)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "pack.txt"), description);

        return path;
    }

    private ModsPageViewModel Load()
    {
        var page = new ModsPageViewModel(ResourceFolderKind.TexturePacks);

        page.Load(_temp);

        return page;
    }

    // ================================================================== identity

    [Fact]
    public void ThePageNamesItselfAfterTheTextureFormat()
    {
        var page = Load();

        Assert.Equal("Texture packs", page.Title);
        Assert.Equal("Download texture packs", page.AddLabel);
    }

    [Fact]
    public void WithNoInstallerThereIsNoDownloadButton()
    {
        // The port has no download source for the legacy format, so the button is absent, not inert.
        Assert.False(Load().CanAdd);
    }

    // ================================================================== what it shows

    [Fact]
    public void BothZipAndFolderPacksAreListed()
    {
        // Users drop both kinds into the same folder; both are valid texture packs.
        MakeZipPack("Faithful.zip", "A faithful remake.");
        MakeFolderPack("MyPack", "Handmade.");

        var page = Load();

        Assert.Contains(page.Mods, m => m.FileName == "Faithful.zip");
        Assert.Contains(page.Mods, m => m.FileName == "MyPack");
    }

    [Fact]
    public void ItReadsTheTexturepacksFolderNotResourcepacks()
    {
        // A resource pack in the modern folder must NOT show up on the legacy page.
        var resourcePacks = Path.Combine(_temp, "resourcepacks");
        Directory.CreateDirectory(resourcePacks);
        File.WriteAllText(Path.Combine(resourcePacks, "modern.txt"), "not a texture pack");

        MakeZipPack("Legacy.zip", "legacy");

        var page = Load();

        Assert.Contains(page.Mods, m => m.FileName == "Legacy.zip");
        Assert.DoesNotContain(page.Mods, m => m.FileName == "modern.txt");
    }

    [Fact]
    public void AnEmptyFolderListsNothing()
    {
        Assert.Empty(Load().Mods);
    }

    // ================================================================== managing, like the mods page

    [Fact]
    public void DisablingRenamesThePackOnDisk()
    {
        var path = MakeZipPack("Faithful.zip", "faithful");

        var page = Load();
        page.Select(page.Mods[0].Path);
        page.ToggleSelected();

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".disabled"));
    }

    [Fact]
    public void DeletingRemovesThePackAndTheRow()
    {
        var path = MakeZipPack("Doomed.zip", "doomed");

        var page = Load();
        page.Select(page.Mods[0].Path);
        page.DeleteSelected();

        Assert.False(File.Exists(path));
        Assert.DoesNotContain(page.Mods, m => m.FileName == "Doomed.zip");
    }

    [Fact]
    public void RefreshingPicksUpAPackAddedFromOutside()
    {
        var page = Load();
        Assert.Empty(page.Mods);

        MakeZipPack("Added.zip", "added later");
        page.Refresh();

        Assert.Contains(page.Mods, m => m.FileName == "Added.zip");
    }
}
