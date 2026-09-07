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
 * Ported from launcher/minecraft/skins/SkinModel.{h,cpp}. One entry in the user's local skin library:
 * a PNG on disk, the arm model it uses (classic or slim), an optional cape, and the URL it came from.
 * It is named LocalSkin because SkinModel is already the arm-model enum (Auth/SkinApi); upstream
 * overloaded the one name for both.
 *
 * The list manager around it (SkinList) is a Qt list-model with a file watcher and drag-and-drop, and
 * belongs with the UI. What is here is the entry itself: its name and model string, its JSON form, a
 * rename, and the validity check — a Minecraft skin is a 64-wide PNG that is 32 or 64 tall, which is
 * decided from the PNG header rather than by decoding the image.
 */

using System.Buffers.Binary;
using System.Text.Json.Nodes;

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Auth;

namespace ExtremeLauncher.Minecraft.Skins;

/// <summary>One skin in the user's local skin library.</summary>
public sealed class LocalSkin
{
    /// <summary>A skin newly taken from a PNG file, defaulting to the classic arm model.</summary>
    public LocalSkin(string path)
    {
        Path = path;
        Model = SkinModel.Classic;
    }

    private LocalSkin(string path, string capeId, string url, SkinModel model)
    {
        Path = path;
        CapeId = capeId;
        Url = url;
        Model = model;
    }

    /// <summary>The PNG's path on disk.</summary>
    public string Path { get; private set; }

    public string CapeId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public SkinModel Model { get; set; }

    /// <summary>The skin's name: its filename without the extension.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The arm model as the string the skins.json and Mojang both use.</summary>
    public string ModelString => Model == SkinModel.Slim ? "SLIM" : "CLASSIC";

    /// <summary>
    /// Reads a skin from a skins.json entry. The name is a bare filename; the actual path is that name,
    /// with a ".png" extension, inside the skins directory.
    /// </summary>
    public static LocalSkin FromJson(string skinDirectory, JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var name = Json.EnsureString(obj, "name");
        var model = Json.EnsureString(obj, "model") == "SLIM" ? SkinModel.Slim : SkinModel.Classic;

        return new LocalSkin(
            System.IO.Path.Combine(skinDirectory, name + ".png"),
            Json.EnsureString(obj, "capeId"),
            Json.EnsureString(obj, "url"),
            model);
    }

    /// <summary>The skin as a skins.json entry.</summary>
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["capeId"] = CapeId,
        ["url"] = Url,
        ["model"] = ModelString,
    };

    /// <summary>
    /// Renames the skin's file to <paramref name="newName"/> (keeping the ".png" extension) in the same
    /// directory, updating <see cref="Path"/>. Returns whether the move succeeded.
    /// </summary>
    public bool Rename(string newName)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? string.Empty;
        var source = Path;

        Path = System.IO.Path.Combine(directory, newName + ".png");

        return FileSystem.Move(source, Path);
    }

    /// <summary>
    /// Whether the file is a usable Minecraft skin: a PNG 64 pixels wide and either 32 or 64 tall (the
    /// old and new skin layouts). Decided from the PNG header, so no image decoder is needed.
    /// </summary>
    public bool IsValid() => IsValidSkinTexture(Path);

    /// <summary>Reads a PNG's dimensions and applies the Minecraft skin size rule.</summary>
    /// <remarks>
    /// Upstream loads the file into a QPixmap and reads its size, which also accepts non-PNG images;
    /// Minecraft skins are always PNG, so reading the PNG header covers every real skin and a non-PNG
    /// file is simply not valid.
    /// </remarks>
    public static bool IsValidSkinTexture(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[24];

        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return false;
            }
        }

        // The 8-byte PNG signature, then the IHDR chunk whose width and height are big-endian at 16 and 20.
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        if (!header[..8].SequenceEqual(signature))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));

        return width == 64 && (height == 32 || height == 64);
    }
}
