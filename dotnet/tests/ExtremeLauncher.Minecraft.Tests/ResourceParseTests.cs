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
 * Ported from tests/{ResourcePackParse,DataPackParse,TexturePackParse,ShaderPackParse,WorldSaveParse,
 * MetaComponentParse}_test.cpp.
 *
 * SIX INHERITED CONTRACTS, run against the Qt suite's own fixtures rather than copies — the same zips
 * and folders both implementations answer to. Every expectation below is upstream's; anything added
 * here is marked as such.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Minecraft.Mods;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class ResourceParseTests
{
    private static string Data(string suite, string name)
        => Path.Combine(AppContext.BaseDirectory, "testdata", suite, name);

    // ================================================================== resource packs

    [Fact]
    public void ResourcePackZipIsParsed()
    {
        var pack = new ResourcePack(Data("ResourcePackParse", "test_resource_pack_idk.zip"));

        Assert.True(ResourcePackUtils.ProcessZip(pack));
        Assert.Equal(3, pack.PackFormat);
        Assert.Equal(
            "um dois, feijão com arroz, três quatro, feijão no prato, cinco seis, café inglês, "
            + "sete oito, comer biscoito, nove dez comer pastéis!!",
            pack.Description);
    }

    [Fact]
    public void ResourcePackFolderIsParsed()
    {
        var pack = new ResourcePack(Data("ResourcePackParse", "test_folder"));

        Assert.True(ResourcePackUtils.ProcessFolder(pack));
        Assert.Equal(1, pack.PackFormat);
        Assert.Equal("Some resource pack maybe", pack.Description);
    }

    [Fact]
    public void AResourcePackWithoutAnAssetsDirectoryIsInvalid()
    {
        var pack = new ResourcePack(Data("ResourcePackParse", "another_test_folder"));

        // The mcmeta parses and the description comes through — but with no "assets" directory the
        // game has nothing to load, so the pack is refused.
        // Its pack.mcmeta starts with a UTF-8 BOM, which Qt skips and System.Text.Json rejects -- see
        // ParseManifest. Without that tolerance the format would read back as 0 here.
        Assert.False(ResourcePackUtils.Process(pack));
        Assert.Equal(6, pack.PackFormat);
        Assert.Equal(
            "o quartel pegou fogo, policia deu sinal, acode acode acode a bandeira nacional",
            pack.Description);
    }

    // ================================================================== data packs

    [Fact]
    public void DataPackZipIsParsed()
    {
        var pack = new DataPack(Data("DataPackParse", "test_data_pack_boogaloo.zip"));

        Assert.True(DataPackUtils.ProcessZip(pack));
        Assert.Equal(4, pack.PackFormat);
        Assert.Equal("Some data pack 2 boobgaloo", pack.Description);
    }

    [Fact]
    public void DataPackFolderIsParsed()
    {
        var pack = new DataPack(Data("DataPackParse", "test_folder"));

        Assert.True(DataPackUtils.ProcessFolder(pack));
        Assert.Equal(10, pack.PackFormat);
        Assert.Equal("Some data pack, maybe", pack.Description);
    }

    [Fact]
    public void DataPackFolderIsParsedThroughTheDispatcher()
    {
        var pack = new DataPack(Data("DataPackParse", "another_test_folder"));

        // Process() picks the folder or zip path from the resource type rather than being told.
        Assert.True(DataPackUtils.Process(pack));
        Assert.Equal(6, pack.PackFormat);
        Assert.Equal("Some data pack three, leaves on the tree", pack.Description);
    }

    // ================================================================== texture packs

    [Fact]
    public void TexturePackZipIsParsed()
    {
        var pack = new TexturePack(Data("TexturePackParse", "test_texture_pack_idk.zip"));

        Assert.True(TexturePackUtils.ProcessZip(pack));
        Assert.Equal("joe biden, wake up", pack.Description);
    }

    [Fact]
    public void TexturePackFolderIsParsed()
    {
        var pack = new TexturePack(Data("TexturePackParse", "test_texturefolder"));

        Assert.True(TexturePackUtils.ProcessFolder(pack));
        Assert.Equal("Some texture pack surely", pack.Description);
    }

    [Fact]
    public void ATexturePackDescriptionKeepsItsNewlines()
    {
        var pack = new TexturePack(Data("TexturePackParse", "another_test_texturefolder"));

        // pack.txt has no structure at all: the whole file is the description, line breaks included.
        Assert.True(TexturePackUtils.Process(pack));
        Assert.Equal("quieres\nfor real", pack.Description);
    }

    // ================================================================== shader packs

    [Fact]
    public void ShaderPackZipIsParsed()
    {
        var pack = new ShaderPack(Data("ShaderPackParse", "shaderpack1.zip"));

        Assert.True(ShaderPackUtils.ProcessZip(pack));
        Assert.Equal(ShaderPackFormat.Valid, pack.PackFormat);
    }

    [Fact]
    public void ShaderPackFolderIsParsed()
    {
        var pack = new ShaderPack(Data("ShaderPackParse", "shaderpack2"));

        Assert.True(ShaderPackUtils.ProcessFolder(pack));
        Assert.Equal(ShaderPackFormat.Valid, pack.PackFormat);
    }

    [Fact]
    public void AZipWithoutAShadersDirectoryIsNotAShaderPack()
    {
        var pack = new ShaderPack(Data("ShaderPackParse", "shaderpack3.zip"));

        // There is no manifest to check, so the directory IS the specification.
        Assert.False(ShaderPackUtils.ProcessZip(pack));
        Assert.Equal(ShaderPackFormat.Invalid, pack.PackFormat);
        Assert.False(pack.Valid);
    }

    // ================================================================== world saves

    [Fact]
    public void AWorldExportedOnItsOwnIsSingleFormat()
    {
        var save = new WorldSave(Data("WorldSaveParse", "minecraft_save_1.zip"));

        Assert.True(WorldSaveUtils.ProcessZip(save));
        Assert.Equal(WorldSaveFormat.Single, save.SaveFormat);
        Assert.Equal("world_1", save.SaveDirName);
    }

    [Fact]
    public void AWorldExportedInsideItsSavesFolderIsMultiFormat()
    {
        var save = new WorldSave(Data("WorldSaveParse", "minecraft_save_2.zip"));

        // Users export both ways into the same drop target, and which it was decides the format.
        Assert.True(WorldSaveUtils.ProcessZip(save));
        Assert.Equal(WorldSaveFormat.Multi, save.SaveFormat);
        Assert.Equal("world_2", save.SaveDirName);
    }

    [Fact]
    public void AWorldFolderIsSingleFormat()
    {
        var save = new WorldSave(Data("WorldSaveParse", "minecraft_save_3"));

        Assert.True(WorldSaveUtils.ProcessFolder(save));
        Assert.Equal(WorldSaveFormat.Single, save.SaveFormat);
        Assert.Equal("world_3", save.SaveDirName);
    }

    [Fact]
    public void ASavesFolderIsMultiFormatThroughTheDispatcher()
    {
        var save = new WorldSave(Data("WorldSaveParse", "minecraft_save_4"));

        Assert.True(WorldSaveUtils.Process(save));
        Assert.Equal(WorldSaveFormat.Multi, save.SaveFormat);
        Assert.Equal("world_4", save.SaveDirName);
    }

    // ================================================================== text components

    [Theory]
    [InlineData("component_basic.json")]
    [InlineData("component_with_format.json")]
    [InlineData("component_with_extra.json")]
    [InlineData("component_with_link.json")]
    [InlineData("component_with_mixed.json")]
    public void ATextComponentRendersToTheExpectedHtml(string name)
    {
        // Each fixture carries both the input and the exact HTML upstream produces for it, so these
        // are ground truth rather than characterization.
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(Data("MetaComponentParse", name)))!;

        var expected = (string)root["expected_output"]!;
        var processed = ResourcePackUtils.ProcessComponent(root["description"]);

        Assert.Equal(expected, processed);
    }

    // ================================================================== the Resource base

    [Theory]
    [InlineData("mod.jar", ResourceType.ZipFile, "mod", true)]
    [InlineData("pack.zip", ResourceType.ZipFile, "pack", true)]
    [InlineData("thing.nilmod", ResourceType.ZipFile, "thing", true)]
    [InlineData("old.litemod", ResourceType.LiteMod, "old", true)]
    [InlineData("notes.txt", ResourceType.SingleFile, "notes.txt", true)]
    [InlineData("mod.jar.disabled", ResourceType.ZipFile, "mod", false)]
    [InlineData("old.litemod.disabled", ResourceType.LiteMod, "old", false)]
    public void TheFilenameDecidesTheTypeAndWhetherItIsEnabled(
        string fileName,
        ResourceType expectedType,
        string expectedName,
        bool expectedEnabled)
    {
        var temp = Path.Combine(Path.GetTempPath(), "el-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var path = Path.Combine(temp, fileName);
            File.WriteAllText(path, "x");

            var resource = new Resource(path);

            // Disabling is a RENAME, so the type has to be worked out from what is left after the
            // ".disabled" suffix comes off — a disabled jar is still a jar.
            Assert.Equal(expectedType, resource.Type);
            Assert.Equal(expectedName, resource.Name);
            Assert.Equal(expectedEnabled, resource.Enabled);

            // The internal id keeps the whole filename, because that is what identifies the file.
            Assert.Equal(fileName, resource.InternalId);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void AFolderResourceCountsItsEntries()
    {
        var temp = Path.Combine(Path.GetTempPath(), "el-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "pack"));

        try
        {
            File.WriteAllText(Path.Combine(temp, "pack", "a.txt"), "a");
            File.WriteAllText(Path.Combine(temp, "pack", "b.txt"), "b");

            var resource = new Resource(Path.Combine(temp, "pack"));

            Assert.Equal(ResourceType.Folder, resource.Type);
            Assert.Equal(2, resource.SizeInfo);
            Assert.Equal("2 items", resource.SizeString);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void ASingleEntryFolderIsSingular()
    {
        var temp = Path.Combine(Path.GetTempPath(), "el-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "pack"));

        try
        {
            File.WriteAllText(Path.Combine(temp, "pack", "only.txt"), "x");

            Assert.Equal("1 item", new Resource(Path.Combine(temp, "pack")).SizeString);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void SomethingThatIsNotThereHasNoType()
    {
        var resource = new Resource(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()));

        Assert.Equal(ResourceType.Unknown, resource.Type);
        Assert.False(resource.Valid);
    }

    // ================================================================== dispatch

    [Fact]
    public void TheDispatcherRefusesATypeItCannotHandle()
    {
        // ADDED, not inherited: a loose file is none of these five kinds, and every Process() has to
        // say so rather than assuming a folder.
        var temp = Path.Combine(Path.GetTempPath(), "el-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var path = Path.Combine(temp, "loose.txt");
            File.WriteAllText(path, "x");

            Assert.False(ResourcePackUtils.Process(new ResourcePack(path)));
            Assert.False(DataPackUtils.Process(new DataPack(path)));
            Assert.False(TexturePackUtils.Process(new TexturePack(path)));
            Assert.False(ShaderPackUtils.Process(new ShaderPack(path)));
            Assert.False(WorldSaveUtils.Process(new WorldSave(path)));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    // ================================================================== the texturepacks folder scanner

    [Fact]
    public void LoadTexturePacksScansTheFolderAndParsesEachPack()
    {
        var temp = Path.Combine(Path.GetTempPath(), "el-tpscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            // A folder pack (pack.txt) and a zip pack (pack.txt entry) -- the two shapes a user drops in.
            var folderPack = Path.Combine(temp, "HandMade");
            Directory.CreateDirectory(folderPack);
            File.WriteAllText(Path.Combine(folderPack, "pack.txt"), "hand made");

            var zipPack = Path.Combine(temp, "Faithful.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zipPack, System.IO.Compression.ZipArchiveMode.Create))
            {
                using var stream = archive.CreateEntry("pack.txt").Open();
                using var writer = new StreamWriter(stream);
                writer.Write("faithful");
            }

            var entries = ResourceFolder.LoadTexturePacks(temp);

            // Both listed, and both parsed as valid texture packs.
            Assert.Equal(2, entries.Count);
            Assert.All(entries.Values, e => Assert.True(e.Resource.Valid));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void LoadTexturePacksOnAMissingFolderIsEmptyNotAnError()
    {
        // Every modern instance is this case: no texturepacks folder at all.
        Assert.Empty(ResourceFolder.LoadTexturePacks(
            Path.Combine(Path.GetTempPath(), "el-nope-" + Guid.NewGuid().ToString("N"))));
    }
}
