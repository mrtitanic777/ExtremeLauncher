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
 * For LocalSkin (minecraft/skins/SkinModel): a skin-library entry. The tests pin the pieces that carry
 * behaviour — the name derived from the path, the JSON round-trip, the rename that moves the file, and
 * the size rule that decides a valid skin — the last read straight from a hand-built PNG header so no
 * image decoder is involved.
 */

using System.Buffers.Binary;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.Minecraft.Skins;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class LocalSkinTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-skins-" + Guid.NewGuid().ToString("N"));

    public LocalSkinTests() => Directory.CreateDirectory(_temp);

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

    /// <summary>Writes a minimal PNG (signature + an IHDR carrying the given size) — enough for the size rule.</summary>
    private string WritePng(string name, int width, int height)
    {
        var path = Path.Combine(_temp, name);

        var bytes = new byte[24];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);

        File.WriteAllBytes(path, bytes);

        return path;
    }

    [Fact]
    public void TheNameIsTheFilenameWithoutExtension()
    {
        var skin = new LocalSkin(Path.Combine(_temp, "Steve.png"));

        Assert.Equal("Steve", skin.Name);
        Assert.Equal(SkinModel.Classic, skin.Model);
        Assert.Equal("CLASSIC", skin.ModelString);
    }

    [Fact]
    public void ASlimSkinReportsItsModelString()
    {
        var skin = new LocalSkin(Path.Combine(_temp, "Alex.png")) { Model = SkinModel.Slim };

        Assert.Equal("SLIM", skin.ModelString);
    }

    [Fact]
    public void JsonRoundTripsThroughTheSkinsDirectory()
    {
        var json = new LocalSkin(Path.Combine(_temp, "Custom.png"))
        {
            Model = SkinModel.Slim,
            CapeId = "cape-1",
            Url = "https://textures.invalid/custom.png",
        }.ToJson();

        var restored = LocalSkin.FromJson(_temp, json);

        Assert.Equal("Custom", restored.Name);
        Assert.Equal(Path.Combine(_temp, "Custom.png"), restored.Path);
        Assert.Equal(SkinModel.Slim, restored.Model);
        Assert.Equal("cape-1", restored.CapeId);
        Assert.Equal("https://textures.invalid/custom.png", restored.Url);
    }

    [Fact]
    public void FromJsonDefaultsToClassicWhenTheModelIsAbsent()
    {
        var restored = LocalSkin.FromJson(_temp, new System.Text.Json.Nodes.JsonObject { ["name"] = "Plain" });

        Assert.Equal(SkinModel.Classic, restored.Model);
        Assert.Equal(string.Empty, restored.CapeId);
    }

    [Fact]
    public void RenamingMovesTheFileAndUpdatesThePath()
    {
        var original = WritePng("Old.png", 64, 64);
        var skin = new LocalSkin(original);

        Assert.True(skin.Rename("New"));
        Assert.Equal(Path.Combine(_temp, "New.png"), skin.Path);
        Assert.Equal("New", skin.Name);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(skin.Path));
    }

    [Theory]
    [InlineData(64, 64, true)]  // modern layout
    [InlineData(64, 32, true)]  // legacy layout
    [InlineData(32, 32, false)] // too narrow
    [InlineData(128, 128, false)]
    public void ValiditySizeRule(int width, int height, bool expected)
    {
        var skin = new LocalSkin(WritePng($"skin-{width}x{height}.png", width, height));

        Assert.Equal(expected, skin.IsValid());
    }

    [Fact]
    public void ANonPngFileIsNotValid()
    {
        var path = Path.Combine(_temp, "notreally.png");
        File.WriteAllText(path, "this is not a PNG at all, just text padding it out past 24 bytes");

        Assert.False(new LocalSkin(path).IsValid());
    }

    [Fact]
    public void AMissingFileIsNotValid()
        => Assert.False(new LocalSkin(Path.Combine(_temp, "gone.png")).IsValid());

    [Fact]
    public void ATruncatedFileIsNotValid()
    {
        var path = Path.Combine(_temp, "tiny.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);

        Assert.False(new LocalSkin(path).IsValid());
    }
}
