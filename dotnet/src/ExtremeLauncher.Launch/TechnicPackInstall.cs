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
 * Ported from technic/TechnicPackProcessor.cpp -- the step that turns an extracted Technic pack (its
 * files already sitting under staging/minecraft) into an instance: work out the components from the
 * pack's version metadata, write the pack profile and instance.cfg. It lives in Launch, not
 * ModPlatform, because it drives instance I/O (the pack profile, jar mods, instance.cfg); the pure
 * piece it leans on -- reading a version.json into a component list -- is TechnicVersionJson over in
 * ModPlatform.
 *
 * A Technic pack declares its loader in one of three shapes, and the processor picks whichever it
 * finds:
 *   - bin/modpack.jar containing a version.json (the modern shape) -> TechnicVersionJson,
 *   - bin/modpack.jar with no version.json (the pre-Forge shape) -> the jar itself is a jar mod, over
 *     the Minecraft version the search gave us, plus a Forge component read from forgeversion.properties,
 *   - bin/version.json on disk (a Solder pack) -> TechnicVersionJson,
 *   - none of the above -> the "Vanilla" pack, just Minecraft.
 * The isSolder flag upstream is unused, so it is not part of this API.
 */

using System.IO.Compression;
using System.Text;

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.Launch;

/// <summary>Builds an instance from an extracted Technic pack.</summary>
public static class TechnicPackBuilder
{
    /// <summary>
    /// Turns a Technic pack already extracted into <c>staging/minecraft</c> into an instance: the pack
    /// profile (Minecraft plus whatever loader the pack names) and instance.cfg. Throws
    /// <see cref="LauncherException"/> when a jar-mod modpack.jar carries no Minecraft version and none
    /// was supplied, matching upstream's "but Minecraft version is unknown".
    /// </summary>
    /// <param name="minecraftVersion">
    /// The Minecraft version the pack search reported, used only when the pack's own metadata does not
    /// name one (a jar-mod modpack.jar, or the Vanilla pack).
    /// </param>
    public static void BuildFromStaging(
        InstancePaths paths,
        RuntimeContext runtimeContext,
        string instanceName,
        string minecraftVersion = "",
        string iconKey = "default")
    {
        ArgumentNullException.ThrowIfNull(paths);

        var modpackJar = FileSystem.PathCombine(paths.BinRoot, "modpack.jar");
        var versionJsonPath = FileSystem.PathCombine(paths.BinRoot, "version.json");

        var profile = new PackProfile(runtimeContext);

        string? versionData;
        var fmlMinecraftVersion = string.Empty;

        if (File.Exists(modpackJar))
        {
            using var zip = ZipFile.OpenRead(modpackJar);

            var versionEntry = zip.GetEntry("version.json");

            if (versionEntry is null)
            {
                // No version.json inside: the jar itself is a jar mod, laid over a known Minecraft
                // version, and any Forge coordinates come from forgeversion.properties.
                if (minecraftVersion.Length == 0)
                {
                    throw new LauncherException(
                        "Could not find \"version.json\" inside \"bin/modpack.jar\", but Minecraft version is unknown.");
                }

                profile.SetComponentVersion(PackComponents.MinecraftUid, minecraftVersion, important: true);
                JarModInstaller.Install(paths, profile, [modpackJar]);

                if (zip.GetEntry("forgeversion.properties") is { } forgeEntry)
                {
                    profile.SetComponentVersion(PackComponents.ForgeUid, ForgeVersionFromProperties(forgeEntry));
                }

                Finish(paths, profile, instanceName, iconKey);
                return;
            }

            // fmlversion.properties, when present, names the Minecraft version an old FML version.json
            // omits from inheritsFrom.
            if (zip.GetEntry("fmlversion.properties") is { } fmlEntry)
            {
                var ini = new IniFile();
                ini.LoadFromBytes(ReadAll(fmlEntry));
                fmlMinecraftVersion = ini.GetString("fmlbuild.mcversion");
            }

            versionData = Encoding.UTF8.GetString(ReadAll(versionEntry));
        }
        else if (File.Exists(versionJsonPath))
        {
            versionData = File.ReadAllText(versionJsonPath);
        }
        else
        {
            // The "Vanilla" pack the search code excludes: no bin at all, just Minecraft.
            profile.SetComponentVersion(PackComponents.MinecraftUid, minecraftVersion, important: true);
            Finish(paths, profile, instanceName, iconKey);
            return;
        }

        // Json (and DetectComponents) throw JsonException, itself a LauncherException carrying a
        // "Could not understand version.json" message, so a bad file surfaces without extra wrapping.
        var root = Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(versionData), "version.json"));

        foreach (var component in TechnicVersionJson.DetectComponents(root, fmlMinecraftVersion))
        {
            profile.SetComponentVersion(component.Uid, component.Version, component.Important);
        }

        Finish(paths, profile, instanceName, iconKey);
    }

    /// <summary>Reads the four-part Forge version out of a forgeversion.properties entry.</summary>
    private static string ForgeVersionFromProperties(ZipArchiveEntry entry)
    {
        var ini = new IniFile();
        ini.LoadFromBytes(ReadAll(entry));

        var major = ini.GetString("forge.major.number");
        var minor = ini.GetString("forge.minor.number");
        var revision = ini.GetString("forge.revision.number");
        var build = ini.GetString("forge.build.number");

        if (major.Length == 0 || minor.Length == 0 || revision.Length == 0 || build.Length == 0)
        {
            throw new LauncherException("Invalid \"forgeversion.properties\".");
        }

        return $"{major}.{minor}.{revision}.{build}";
    }

    private static void Finish(InstancePaths paths, PackProfile profile, string instanceName, string iconKey)
    {
        profile.Save(paths.PackProfilePath);

        var settings = new IniSettingsObject(paths.ConfigPath);
        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);
        settings.Set("InstanceType", "OneSix");
        settings.Set("name", instanceName);

        if (iconKey != "default")
        {
            settings.Set("iconKey", iconKey);
        }
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }
}
