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
 * Working out what a dropped file is.
 *
 * THE ORDERING TEST IS THE ONE THAT MATTERS. A mod jar routinely carries the very things a
 * resource-pack test looks for, and upstream's comment on the first branch says exactly that:
 * "mods can contain resource and data packs so they must be tested first".
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Minecraft.Mods;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class LocalResourceParseTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-lrp-" + Guid.NewGuid().ToString("N"));

    public LocalResourceParseTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Builds a zip with the given entries and returns its path.</summary>
    private string Zip(string name, params (string Path, string Contents)[] entries)
    {
        var path = Path.Combine(_temp, name);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entryPath, contents) in entries)
        {
            var entry = archive.CreateEntry(entryPath);

            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);

            writer.Write(contents);
        }

        return path;
    }

    [Fact]
    public void AFabricModIsIdentifiedAsAMod()
    {
        var path = Zip(
            "sodium.jar",
            ("fabric.mod.json", """{"schemaVersion":1,"id":"sodium","version":"0.5.13","name":"Sodium"}"""));

        Assert.Equal(PackedResourceType.Mod, LocalResourceParse.Identify(path));
    }

    [Fact]
    public void AForgeModIsIdentifiedAsAMod()
    {
        var path = Zip(
            "jei.jar",
            ("META-INF/mods.toml", """
                modLoader="javafml"
                loaderVersion="[47,)"
                [[mods]]
                modId="jei"
                version="15.2.0"
                """));

        Assert.Equal(PackedResourceType.Mod, LocalResourceParse.Identify(path));
    }

    [Fact]
    public void AResourcePackIsIdentifiedAsOne()
    {
        var path = Zip(
            "faithful.zip",
            ("pack.mcmeta", """{"pack":{"pack_format":15,"description":"Faithful"}}"""),
            ("assets/minecraft/textures/block/stone.png", "not really a png"));

        Assert.Equal(PackedResourceType.ResourcePack, LocalResourceParse.Identify(path));
    }

    [Fact]
    public void AModThatAlsoContainsAResourcePackIsStillAMod()
    {
        /*
         * THE ORDERING, which upstream comments on the first branch: a mod jar routinely carries a
         * pack.mcmeta and an assets tree, so a resource-pack test would claim it. Reordering these
         * checks files mods as resource packs, and that looks like the user's fault rather than the
         * launcher's.
         */
        var path = Zip(
            "createmod.jar",
            ("fabric.mod.json", """{"schemaVersion":1,"id":"create","version":"0.5.1","name":"Create"}"""),
            ("pack.mcmeta", """{"pack":{"pack_format":15,"description":"Create resources"}}"""),
            ("assets/create/textures/block/cog.png", "not really a png"));

        Assert.Equal(PackedResourceType.Mod, LocalResourceParse.Identify(path));
    }

    [Fact]
    public void AWorldSaveIsIdentifiedAsOne()
    {
        var path = Zip("MyWorld.zip", ("MyWorld/level.dat", "pretend nbt"));

        Assert.Equal(PackedResourceType.WorldSave, LocalResourceParse.Identify(path));
    }

    [Fact]
    public void SomethingUnrecognisedIsUnknownRatherThanAGuess()
    {
        // A launcher that files an arbitrary zip as a mod produces an instance that will not start,
        // and nothing on screen explaining why.
        var path = Zip("holiday-photos.zip", ("beach.jpg", "not a minecraft thing at all"));

        Assert.Equal(PackedResourceType.Unknown, LocalResourceParse.Identify(path));
    }

    [Fact]
    public void ACorruptArchiveIsUnknownRatherThanAThrow()
    {
        /*
         * A dropped file is arbitrary bytes somebody chose. A text file renamed to .jar makes a zip
         * reader throw, and the answer to "is this a shader pack" is then "no" -- not a crash.
         */
        var path = Path.Combine(_temp, "broken.jar");

        File.WriteAllText(path, "this is definitely not a zip archive");

        Assert.Equal(PackedResourceType.Unknown, LocalResourceParse.Identify(path));
    }

    [Fact]
    public void AFileThatIsNotThereIsUnknown()
    {
        Assert.Equal(PackedResourceType.Unknown, LocalResourceParse.Identify(Path.Combine(_temp, "gone.jar")));
    }

    [Fact]
    public void AnEmptyPathIsUnknown()
    {
        Assert.Equal(PackedResourceType.Unknown, LocalResourceParse.Identify(string.Empty));
    }

    [Fact]
    public void ATexturePackGoesToItsOwnFolderRatherThanTheResourcePackOne()
    {
        /*
         * Pre-1.6 format, read from a different folder. Putting one in with the resource packs makes
         * it invisible to the game, which reads as the import having failed.
         */
        Assert.Equal("texturepacks", LocalResourceParse.FolderFor(PackedResourceType.TexturePack));
        Assert.Equal("resourcepacks", LocalResourceParse.FolderFor(PackedResourceType.ResourcePack));
    }

    [Theory]
    [InlineData(PackedResourceType.Mod, "mods")]
    [InlineData(PackedResourceType.ShaderPack, "shaderpacks")]
    [InlineData(PackedResourceType.DataPack, "datapacks")]
    [InlineData(PackedResourceType.WorldSave, "saves")]
    public void EveryKindKnowsWhereItGoes(PackedResourceType type, string folder)
        => Assert.Equal(folder, LocalResourceParse.FolderFor(type));

    [Fact]
    public void UnknownHasNowhereToGo()
    {
        // An empty destination is what stops the importer writing something arbitrary somewhere.
        Assert.Equal(string.Empty, LocalResourceParse.FolderFor(PackedResourceType.Unknown));
    }

    [Fact]
    public void EveryValidKindHasAReadableNameAndAFolder()
    {
        Assert.All(LocalResourceParse.ValidTypes, type =>
        {
            Assert.NotEqual("unknown", LocalResourceParse.TypeName(type));
            Assert.NotEqual(string.Empty, LocalResourceParse.FolderFor(type));
        });
    }
}
