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
 * Ported from launcher/modplatform/import_ftb/PackHelpers.{h,cpp}.
 *
 * IMPORTING FROM THE FTB APP, which is a different problem from importing a pack file. There is no
 * archive and no manifest to download -- the FTB app is already installed on the machine, with its
 * instances sitting in a folder, and this reads them where they are.
 *
 * SO A DIRECTORY THAT DOES NOT PARSE IS NOT AN ERROR. The caller points at the FTB app's instances
 * folder and every subdirectory is tried; anything that is not an FTB instance is simply skipped.
 * That is why nothing here throws -- a failed parse returns null, and one unreadable directory does
 * not lose the other twenty.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>One instance of the FTB app, as its own files describe it.</summary>
public sealed class FtbAppModpack
{
    /// <summary>Where the instance lives.</summary>
    public string Path { get; set; } = string.Empty;

    public string Uuid { get; set; } = string.Empty;

    public int Id { get; set; }

    public int VersionId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The PACK's version, as instance.json states it.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="LoaderVersion"/>, which upstream does not: its target loop
    /// assigns the loader's version over this field, and the install task then reads it back as the
    /// loader version. Internally consistent, and it leaves the struct's own <c>loaderVersion</c>
    /// member dead while the pack version is destroyed. Nothing upstream displays the pack version, so
    /// the conflation never surfaces -- but two meanings in one field is one meaning too many.
    /// </remarks>
    public string Version { get; set; } = string.Empty;

    public string McVersion { get; set; } = string.Empty;

    public int TotalPlayTime { get; set; }

    /// <summary>Read but not used for creating the instance, as upstream notes.</summary>
    public string JvmArgs { get; set; } = string.Empty;

    /// <summary>The loader the pack targets, if it is one this launcher knows.</summary>
    public ModLoaderTypes LoaderType { get; set; } = ModLoaderTypes.None;

    public string LoaderVersion { get; set; } = string.Empty;

    /// <summary>The instance's icon file, if the FTB app wrote one.</summary>
    public string IconPath { get; set; } = string.Empty;
}

public static class FtbAppImport
{
    public const string InstanceFileName = "instance.json";
    public const string VersionFileName = "version.json";

    /// <summary>The name the FTB app gives an instance's icon.</summary>
    public const string IconFileName = "folder.jpg";

    /// <summary>
    /// Reads one FTB app instance directory.
    /// </summary>
    /// <returns>
    /// Null when the directory is not a readable FTB instance — a missing file, a malformed document,
    /// or a field that is not what it claims. Never throws: see the note at the top of this file.
    /// </returns>
    public static FtbAppModpack? ParseDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var modpack = new FtbAppModpack { Path = path };

        // BOTH files are required. An instance.json alone is an FTB instance that never finished
        // installing, and importing one would produce an instance with no loader and no explanation.
        if (!TryReadJson(FileSystem.PathCombine(path, InstanceFileName), out var instance)
            || !TryReadJson(FileSystem.PathCombine(path, VersionFileName), out var version))
        {
            return null;
        }

        try
        {
            modpack.Uuid = Json.RequireString(instance, "uuid");
            modpack.Id = Json.RequireInteger(instance, "id");
            modpack.VersionId = Json.RequireInteger(instance, "versionId");
            modpack.Name = Json.RequireString(instance, "name");
            modpack.Version = Json.RequireString(instance, "version");
            modpack.McVersion = Json.RequireString(instance, "mcVersion");
            modpack.TotalPlayTime = Json.RequireInteger(instance, "totalPlayTime");

            // Whatever shape it is: upstream reads it as a QVariant and never interprets it.
            modpack.JvmArgs = instance["jvmArgs"]?.ToString() ?? string.Empty;

            ReadLoader(version, modpack);
        }
        catch (LauncherException)
        {
            // Not an FTB instance after all, or one this version of the app writes differently.
            return null;
        }

        var icon = FileSystem.PathCombine(path, IconFileName);

        if (File.Exists(icon))
        {
            modpack.IconPath = icon;
        }

        return modpack;
    }

    /// <summary>
    /// Finds the loader among the pack's build targets.
    /// </summary>
    /// <remarks>
    /// The targets array also lists the Minecraft version and sometimes a Java runtime, so the loop
    /// looks for a name it recognises and stops at the FIRST one. A pack targeting two loaders is not
    /// a thing the FTB app produces.
    /// </remarks>
    private static void ReadLoader(JsonObject version, FtbAppModpack modpack)
    {
        foreach (var element in Json.RequireArray(version, "targets"))
        {
            var target = Json.RequireObject(element);
            var name = Json.RequireString(target, "name");

            var loader = name switch
            {
                "neoforge" => ModLoaderTypes.NeoForge,
                "forge" => ModLoaderTypes.Forge,
                "fabric" => ModLoaderTypes.Fabric,
                "quilt" => ModLoaderTypes.Quilt,
                _ => ModLoaderTypes.None,
            };

            // "minecraft" and "java" targets fall through here rather than being special-cased.
            if (loader == ModLoaderTypes.None)
            {
                continue;
            }

            modpack.LoaderType = loader;
            modpack.LoaderVersion = Json.RequireString(target, "version");

            return;
        }
    }

    /// <summary>The components an imported FTB instance starts from.</summary>
    /// <remarks>
    /// Minecraft is marked important, and so is the loader — unlike the pack importers, where only
    /// Minecraft is. That is upstream's choice here (<c>setComponentVersion(..., true)</c> on the
    /// loader) and it is defensible: an FTB app instance is being adopted rather than installed from a
    /// recipe, so the loader is as much the user's as the game version.
    /// </remarks>
    public static List<PackComponent> GetComponents(FtbAppModpack modpack)
    {
        ArgumentNullException.ThrowIfNull(modpack);

        var components = new List<PackComponent>
        {
            new(PackComponents.MinecraftUid, PackComponents.NormaliseMinecraftVersion(modpack.McVersion), Important: true),
        };

        var uid = modpack.LoaderType switch
        {
            ModLoaderTypes.NeoForge => PackComponents.NeoForgeUid,
            ModLoaderTypes.Forge => PackComponents.ForgeUid,
            ModLoaderTypes.Fabric => PackComponents.FabricUid,
            ModLoaderTypes.Quilt => PackComponents.QuiltUid,
            _ => null,
        };

        if (uid is not null)
        {
            components.Add(new PackComponent(uid, modpack.LoaderVersion, Important: true));
        }

        return components;
    }

    /// <summary>
    /// Reads every FTB instance in a folder, skipping whatever is not one.
    /// </summary>
    /// <remarks>
    /// Ordered by name so a listing is stable, which the directory enumeration order is not.
    /// </remarks>
    public static List<FtbAppModpack> ScanFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateDirectories(folder)
                .Order(StringComparer.Ordinal)
                .Select(ParseDirectory)
                .Where(pack => pack is not null)
                .Select(pack => pack!),
        ];
    }

    private static bool TryReadJson(string path, out JsonObject obj)
    {
        obj = [];

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            obj = Json.RequireObject(Json.RequireDocument(FileSystem.Read(path), path));

            return true;
        }
        catch (Exception e) when (e is LauncherException or IOException)
        {
            return false;
        }
    }
}
