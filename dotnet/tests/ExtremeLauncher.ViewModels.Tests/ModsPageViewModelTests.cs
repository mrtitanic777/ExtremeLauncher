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
 * NOTHING ON THIS PAGE IS DEFERRED: enabling a mod renames a file the moment it is pressed. So the
 * tests are all against the real folder, and the ones that matter are about a rename losing a file.
 */

using System.IO.Compression;
using System.Text;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ModsPageViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-mods-" + Guid.NewGuid().ToString("N"));

    private readonly string _mods;

    public ModsPageViewModelTests()
    {
        _mods = Path.Combine(_temp, "mods");
        Directory.CreateDirectory(_mods);
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

    /// <summary>A jar carrying a real fabric.mod.json, so the mod's declared name is used.</summary>
    private string MakeFabricMod(string fileName, string id, string name, string version)
    {
        var path = Path.Combine(_mods, fileName);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("fabric.mod.json");

            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);

            writer.Write($$"""
                {
                    "schemaVersion": 1,
                    "id": "{{id}}",
                    "name": "{{name}}",
                    "version": "{{version}}"
                }
                """);
        }

        return path;
    }

    /// <summary>A jar with no metadata at all, which still has to appear.</summary>
    private string MakeBareJar(string fileName)
    {
        var path = Path.Combine(_mods, fileName);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        archive.CreateEntry("nothing/useful.txt");

        return path;
    }

    private ModsPageViewModel Load()
    {
        var page = new ModsPageViewModel();

        page.Load(_temp);

        return page;
    }

    // ================================================================== what it shows

    /*
     * THE MOD'S OWN NAME, not its filename. "Sodium" rather than "sodium-fabric-0.5.3+mc1.20.1" is the
     * whole reason this page reads the jars at all rather than listing the directory.
     */
    [Fact]
    public void ModsAreListedByTheirDeclaredNameAndVersion()
    {
        MakeFabricMod("sodium-fabric-0.5.3+mc1.20.1.jar", "sodium", "Sodium", "0.5.3");

        var page = Load();

        var mod = Assert.Single(page.Mods);

        Assert.Equal("Sodium", mod.Name);
        Assert.Equal("0.5.3", mod.Version);
        Assert.True(mod.IsEnabled);
    }

    /*
     * A jar whose metadata will not parse STILL GETS A ROW, named after its file. A mod the launcher
     * cannot read is exactly the one a user came here looking for.
     */
    [Fact]
    public void AJarWithNoMetadataIsStillListed()
    {
        MakeBareJar("mystery.jar");

        var page = Load();

        Assert.Equal("mystery", Assert.Single(page.Mods).Name);
    }

    [Fact]
    public void ADisabledModIsListedAndMarkedAsSuch()
    {
        MakeFabricMod("sodium.jar.disabled", "sodium", "Sodium", "0.5.3");

        var page = Load();

        Assert.False(Assert.Single(page.Mods).IsEnabled);
    }

    /// <summary>Enabled first, then by name — the order that answers "what is this instance running?".</summary>
    [Fact]
    public void EnabledModsComeFirst()
    {
        MakeFabricMod("a.jar.disabled", "alpha", "Alpha", "1");
        MakeFabricMod("z.jar", "zeta", "Zeta", "1");

        var page = Load();

        Assert.Equal(["Zeta", "Alpha"], page.Mods.Select(m => m.Name));
    }

    [Fact]
    public void AnEmptyFolderListsNothingAndDoesNotThrow()
    {
        var page = Load();

        Assert.Empty(page.Mods);
        Assert.False(page.HasSelection);
    }

    // ================================================================== turning mods on and off

    [Fact]
    public void DisablingRenamesTheFileOnDisk()
    {
        MakeFabricMod("sodium.jar", "sodium", "Sodium", "0.5.3");

        var page = Load();

        page.Select(page.Mods[0].Path);

        Assert.Equal("Disable", page.ToggleLabel);

        page.ToggleSelected();

        Assert.False(File.Exists(Path.Combine(_mods, "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(_mods, "sodium.jar.disabled")));

        Assert.False(Assert.Single(page.Mods).IsEnabled);
        Assert.Contains("Disabled", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void EnablingPutsItBack()
    {
        MakeFabricMod("sodium.jar.disabled", "sodium", "Sodium", "0.5.3");

        var page = Load();

        page.Select(page.Mods[0].Path);

        Assert.Equal("Enable", page.ToggleLabel);

        page.ToggleSelected();

        Assert.True(File.Exists(Path.Combine(_mods, "sodium.jar")));
        Assert.True(Assert.Single(page.Mods).IsEnabled);
    }

    /*
     * THE SELECTION FOLLOWS THE FILE. Toggling renames it, so a selection held by path would be lost
     * every time -- and the next press would act on nothing, or worse, on whatever row moved into
     * place.
     */
    [Fact]
    public void TheSelectionSurvivesAToggle()
    {
        MakeFabricMod("sodium.jar", "sodium", "Sodium", "0.5.3");
        MakeFabricMod("lithium.jar", "lithium", "Lithium", "0.11");

        var page = Load();

        page.Select(page.Mods.Single(m => m.Name == "Sodium").Path);
        page.ToggleSelected();

        Assert.Equal("Sodium", page.Selected?.Name);

        // ...and pressing again puts the same mod back rather than acting on the other one.
        page.ToggleSelected();

        Assert.True(File.Exists(Path.Combine(_mods, "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(_mods, "lithium.jar")));
    }

    [Fact]
    public void TogglingWithNothingSelectedDoesNothing()
    {
        MakeFabricMod("sodium.jar", "sodium", "Sodium", "0.5.3");

        var page = Load();

        page.ToggleSelected();

        Assert.True(File.Exists(Path.Combine(_mods, "sodium.jar")));
        Assert.Equal(string.Empty, page.Status);
    }

    // ================================================================== removing

    [Fact]
    public void DeletingRemovesTheJarAndTheRow()
    {
        MakeFabricMod("sodium.jar", "sodium", "Sodium", "0.5.3");
        MakeFabricMod("lithium.jar", "lithium", "Lithium", "0.11");

        var page = Load();

        page.Select(page.Mods.Single(m => m.Name == "Sodium").Path);
        page.DeleteSelected();

        Assert.False(File.Exists(Path.Combine(_mods, "sodium.jar")));
        Assert.Equal(["Lithium"], page.Mods.Select(m => m.Name));

        // The other one is untouched.
        Assert.True(File.Exists(Path.Combine(_mods, "lithium.jar")));
    }

    [Fact]
    public void DeletingWithNothingSelectedDoesNothing()
    {
        MakeFabricMod("sodium.jar", "sodium", "Sodium", "0.5.3");

        var page = Load();

        page.DeleteSelected();

        Assert.True(File.Exists(Path.Combine(_mods, "sodium.jar")));
    }

    // ================================================================== refreshing

    /*
     * Mods appear and disappear behind the launcher's back -- someone drops a jar into the folder, or
     * another launcher writes to the same instance. Re-reading is the only thing that is always right.
     */
    [Fact]
    public void RefreshingPicksUpAModAddedFromOutside()
    {
        var page = Load();

        Assert.Empty(page.Mods);

        MakeFabricMod("sodium.jar", "sodium", "Sodium", "0.5.3");

        page.Refresh();

        Assert.Equal(["Sodium"], page.Mods.Select(m => m.Name));
    }

    // ================================================================== the page contract

    /*
     * NOTHING IS DEFERRED, so there is nothing to save. The instance window prompts about unsaved
     * changes on close, and a page reporting dirtiness it could not resolve would prompt forever.
     */
    [Fact]
    public void ThePageNeverHasAnythingToSave()
    {
        MakeFabricMod("sodium.jar", "sodium", "Sodium", "0.5.3");

        var page = Load();

        page.Select(page.Mods[0].Path);
        page.ToggleSelected();

        Assert.False(page.HasUnsavedChanges);
        Assert.True(page.Save());
    }

    // ================================================================== view folder

    private sealed class StubFolderOpener : ExtremeLauncher.ViewModels.IFolderOpener
    {
        public string? Opened { get; private set; }

        public Task OpenAsync(string path)
        {
            Opened = path;

            return Task.CompletedTask;
        }

        public Task OpenKnownAsync(string kind) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(ResourceFolderKind.Mods, "mods")]
    [InlineData(ResourceFolderKind.ResourcePacks, "resourcepacks")]
    [InlineData(ResourceFolderKind.ShaderPacks, "shaderpacks")]
    [InlineData(ResourceFolderKind.TexturePacks, "texturepacks")]
    public void EachKindOpensItsOwnFolder(ResourceFolderKind kind, string folder)
    {
        var page = new ModsPageViewModel(kind, folders: new StubFolderOpener());
        page.Load(_temp);

        // The folder path ends with that kind's directory, whatever the separators.
        Assert.EndsWith(folder, page.FolderPath.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoOpenerViewFolderIsDisabled()
    {
        var page = new ModsPageViewModel();
        page.Load(_temp);

        Assert.False(page.CanOpenFolder);
    }

    [Fact]
    public async Task OpeningTheFolderForwardsThePathAndCreatesItIfAbsent()
    {
        var opener = new StubFolderOpener();

        // The resourcepacks folder does not exist yet -- a fresh instance has only "mods".
        var page = new ModsPageViewModel(ResourceFolderKind.ResourcePacks, folders: opener);
        page.Load(_temp);

        Assert.True(page.CanOpenFolder);

        await page.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(page.FolderPath, opener.Opened);
        Assert.True(Directory.Exists(page.FolderPath));
    }
}
