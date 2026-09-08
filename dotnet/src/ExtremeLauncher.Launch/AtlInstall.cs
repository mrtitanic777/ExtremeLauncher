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
 * The decision logic of ATLPackInstallTask, ported as pure functions. The install task itself is
 * ~1,075 lines of download/extract/staging orchestration bound to Qt; two of its private helpers are
 * pure and carry the real rules, so they are worth porting and testing on their own ahead of the rest:
 *
 *   - getDirForModType: where a mod of a given type is placed inside the instance. A delivery
 *     instruction, not a category -- "mods" is the mods folder, "jar" is a jar mod, "dependency" is a
 *     Minecraft-version-specific mods subfolder, and several types are placed elsewhere (extracted,
 *     decompiled) so they resolve to no plain destination here.
 *   - detectLibrary: turning a library's server path or filename into a Gradle coordinate, so a
 *     library ATLauncher ships can line up with the same library from the metadata index.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.Launch;

/// <summary>Pure decision helpers ported from ATLauncher's PackInstallTask.</summary>
public static class AtlInstall
{
    /// <summary>
    /// The instance-relative folder a mod of the given type is dropped into, or <c>null</c> when the
    /// type is not placed as a plain file here — either handled elsewhere (extracted, decompiled, the
    /// pack root) or unsupported. Forward slashes, matching upstream's normalised paths.
    /// </summary>
    /// <param name="minecraftVersion">Used only by <see cref="AtlModType.Dependency"/>, which lands in a
    /// per-version mods subfolder.</param>
    /// <exception cref="LauncherException">
    /// The type is <see cref="AtlModType.Unknown"/>, which upstream treats as a fatal install error.
    /// </exception>
    public static string? GetDirForModType(AtlModType type, string raw, string minecraftVersion)
    {
        return type switch
        {
            // Handled at another stage (extracted, decompiled) or ignored entirely.
            AtlModType.Root
                or AtlModType.Extract
                or AtlModType.Decomp
                or AtlModType.TexturePackExtract
                or AtlModType.ResourcePackExtract
                or AtlModType.Mcpc => null,

            // Forge is detected later; if it cannot be, it installs as a jar mod — so both land here.
            AtlModType.Forge or AtlModType.Jar => "jarmods",

            AtlModType.Mods => "mods",
            AtlModType.Flan => "Flan",
            AtlModType.Dependency => "mods/" + minecraftVersion,
            AtlModType.Ic2Lib => "mods/ic2",
            AtlModType.DenLib => "mods/denlib",
            AtlModType.Coremods => "coremods",
            AtlModType.Plugins => "plugins",
            AtlModType.TexturePack => "texturepacks",
            AtlModType.ResourcePack => "resourcepacks",
            AtlModType.ShaderPack => "shaderpacks",

            // Recognised but not supported; upstream warns and skips it.
            AtlModType.Millenaire => null,

            _ => throw new LauncherException($"Unknown mod type: {raw}"),
        };
    }

    /// <summary>
    /// A Gradle-style coordinate for a pack library, so it can be matched against the metadata index.
    /// The server path is preferred (it carries the real group/artefact/version); failing that, a
    /// couple of well-known filenames are recognised; failing that, a synthetic ATLauncher coordinate
    /// keyed by the library's md5 keeps it unique.
    /// </summary>
    public static string DetectLibrary(AtlVersionLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        // A server path like ".../group/parts/artefact/version/file.jar" spells out the coordinate.
        if (!string.IsNullOrEmpty(library.Server) && library.Server.Split('/').Length >= 3)
        {
            var lastSlash = library.Server.LastIndexOf('/');
            var locationAndVersion = library.Server[..lastSlash];

            lastSlash = locationAndVersion.LastIndexOf('/');
            var location = locationAndVersion[..lastSlash];
            var version = locationAndVersion[(lastSlash + 1)..];

            lastSlash = location.LastIndexOf('/');
            var group = location[..lastSlash].Replace('/', '.');
            var artefact = location[(lastSlash + 1)..];

            return $"{group}:{artefact}:{version}";
        }

        // A "name-version.jar" filename is enough for the two libraries ATLauncher ships under names
        // the metadata index also knows.
        if (library.File.Contains('-', StringComparison.Ordinal))
        {
            var lastDash = library.File.LastIndexOf('-');
            var name = library.File[..lastDash];
            var version = library.File[(lastDash + 1)..].Replace(".jar", string.Empty, StringComparison.Ordinal);

            if (name == "guava")
            {
                return "com.google.guava:guava:" + version;
            }

            if (name == "commons-lang3")
            {
                return "org.apache.commons:commons-lang3:" + version;
            }
        }

        // Nothing recognisable: a synthetic coordinate, unique by md5, so it still installs.
        return "org.multimc.atlauncher:" + library.Md5 + ":1";
    }
}

/// <summary>
/// Stages an ATLauncher instance from a resolved pack version, ported from the tail of
/// ATLPackInstallTask's install(): the pack profile (Minecraft plus the loader, from
/// <see cref="PackComponents.FromAtl"/>, plus any jar mods) and instance.cfg, including the managed-pack
/// fields that let the instance be updated later. The library and pack VersionFile components upstream
/// also writes (createLibrariesComponent / createPackComponent) are a further wave; this is the part
/// that turns the version metadata and downloaded jar mods into an instance.
/// </summary>
public static class AtlPackBuilder
{
    private const string ManagedPackType = "atlauncher";

    public static void BuildInstance(
        InstancePaths paths,
        AtlPackVersion version,
        RuntimeContext runtimeContext,
        string packName,
        string packSafeName,
        string versionName,
        IEnumerable<string>? jarMods = null,
        string iconKey = "default",
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(version);

        var profile = new PackProfile(runtimeContext);

        foreach (var component in PackComponents.FromAtl(version))
        {
            profile.SetComponentVersion(component.Uid, component.Version, component.Important);
        }

        var jars = jarMods?.ToList() ?? [];

        if (jars.Count != 0)
        {
            JarModInstaller.Install(paths, profile, jars);
        }

        profile.Save(paths.PackProfilePath);

        var name = displayName is { Length: > 0 } ? displayName : packName;

        var settings = new IniSettingsObject(paths.ConfigPath);
        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);
        settings.Set("InstanceType", "OneSix");
        settings.Set("name", name);

        if (iconKey != "default")
        {
            settings.Set("iconKey", iconKey);
        }

        // The managed-pack fields, so the instance knows it came from ATLauncher and can be offered an
        // update — the same keys InstanceSettings.SetManagedPack writes. The id is the pack's safe name,
        // and both version fields carry the version name (ATLauncher has no separate numeric version id).
        settings.RegisterSetting("ManagedPack", false);
        settings.RegisterSetting("ManagedPackType", string.Empty);
        settings.RegisterSetting("ManagedPackID", string.Empty);
        settings.RegisterSetting("ManagedPackName", string.Empty);
        settings.RegisterSetting("ManagedPackVersionID", string.Empty);
        settings.RegisterSetting("ManagedPackVersionName", string.Empty);
        settings.Set("ManagedPack", true);
        settings.Set("ManagedPackType", ManagedPackType);
        settings.Set("ManagedPackID", packSafeName);
        settings.Set("ManagedPackName", packName);
        settings.Set("ManagedPackVersionID", versionName);
        settings.Set("ManagedPackVersionName", versionName);
    }
}
