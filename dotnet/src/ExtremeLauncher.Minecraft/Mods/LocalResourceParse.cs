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
 * Ported from launcher/minecraft/mod/tasks/LocalResourceParse.cpp.
 *
 * WORKING OUT WHAT A FILE IS, so somebody can drop a zip on the launcher and have it land in the
 * right folder. Six validators for six kinds of thing were ported waves ago and nothing ever asked
 * them a question -- this is the twenty lines that do.
 *
 * THE ORDER IS LOAD-BEARING, and upstream says so in a comment on the first branch:
 *
 *     // mods can contain resource and data packs so they must be tested first
 *
 * A mod jar very often carries an assets/ tree and a pack.mcmeta, so a resource-pack test would claim
 * it. Every ordering after that is the same argument: the most specific claimant first. Reordering
 * these lines silently files mods as resource packs, which is the sort of bug that looks like the
 * user's fault.
 */

namespace ExtremeLauncher.Minecraft.Mods;

/// <summary>What a dropped file turned out to be.</summary>
public enum PackedResourceType
{
    DataPack,
    ResourcePack,
    TexturePack,
    ShaderPack,
    WorldSave,
    Mod,

    /// <summary>Nothing this launcher recognises.</summary>
    Unknown,
}

public static class LocalResourceParse
{
    /// <summary>The kinds an instance has somewhere to put.</summary>
    public static readonly PackedResourceType[] ValidTypes =
    [
        PackedResourceType.DataPack,
        PackedResourceType.ResourcePack,
        PackedResourceType.TexturePack,
        PackedResourceType.ShaderPack,
        PackedResourceType.WorldSave,
        PackedResourceType.Mod,
    ];

    /// <summary>What to call it on screen.</summary>
    public static string TypeName(PackedResourceType type) => type switch
    {
        PackedResourceType.DataPack => "data pack",
        PackedResourceType.ResourcePack => "resource pack",
        PackedResourceType.TexturePack => "texture pack",
        PackedResourceType.ShaderPack => "shader pack",
        PackedResourceType.WorldSave => "world save",
        PackedResourceType.Mod => "mod",
        _ => "unknown",
    };

    /// <summary>
    /// Where inside an instance's game folder this kind belongs.
    /// </summary>
    /// <remarks>
    /// A texture pack goes in <c>texturepacks</c>, not <c>resourcepacks</c>: it is the pre-1.6 format
    /// and the game reads it from a different folder. Putting one in with the resource packs makes it
    /// invisible, which reads as the import having failed.
    /// </remarks>
    public static string FolderFor(PackedResourceType type) => type switch
    {
        PackedResourceType.Mod => "mods",
        PackedResourceType.ResourcePack => "resourcepacks",
        PackedResourceType.TexturePack => "texturepacks",
        PackedResourceType.ShaderPack => "shaderpacks",
        PackedResourceType.DataPack => "datapacks",
        PackedResourceType.WorldSave => "saves",
        _ => string.Empty,
    };

    /// <summary>
    /// Identifies a file by asking each validator in turn.
    /// </summary>
    /// <remarks>
    /// Upstream's order exactly. See the file header: a mod jar routinely contains the very things a
    /// resource-pack or data-pack test looks for, so mods are asked about first and the rest follow
    /// from most specific to least.
    /// </remarks>
    public static PackedResourceType Identify(string path)
    {
        if (path.Length == 0 || !File.Exists(path))
        {
            return PackedResourceType.Unknown;
        }

        // Mods first: a mod can contain resource and data packs, so anything else would claim it.
        if (Valid(() => ModUtils.Process(new Mod(path), ProcessingLevel.BasicInfoOnly)))
        {
            return PackedResourceType.Mod;
        }

        if (Valid(() => ResourcePackUtils.Process(new ResourcePack(path), ProcessingLevel.BasicInfoOnly)))
        {
            return PackedResourceType.ResourcePack;
        }

        if (Valid(() => TexturePackUtils.Process(new TexturePack(path), ProcessingLevel.BasicInfoOnly)))
        {
            return PackedResourceType.TexturePack;
        }

        if (Valid(() => DataPackUtils.Process(new DataPack(path), ProcessingLevel.BasicInfoOnly)))
        {
            return PackedResourceType.DataPack;
        }

        if (Valid(() => WorldSaveUtils.Process(new WorldSave(path), ProcessingLevel.BasicInfoOnly)))
        {
            return PackedResourceType.WorldSave;
        }

        if (Valid(() => ShaderPackUtils.Process(new ShaderPack(path), ProcessingLevel.BasicInfoOnly)))
        {
            return PackedResourceType.ShaderPack;
        }

        return PackedResourceType.Unknown;
    }

    /// <summary>
    /// Runs one validator, treating a throw as "no".
    /// </summary>
    /// <remarks>
    /// A dropped file is arbitrary bytes somebody chose. A corrupt zip, a text file renamed to .jar,
    /// a directory that looks like an archive -- each of these can make a parser throw, and the answer
    /// to "is this a shader pack" is then "no", not "take the window down".
    /// </remarks>
    private static bool Valid(Func<bool> validator)
    {
        try
        {
            return validator();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
