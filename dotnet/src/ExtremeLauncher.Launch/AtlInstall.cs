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
using ExtremeLauncher.ModPlatform;

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
