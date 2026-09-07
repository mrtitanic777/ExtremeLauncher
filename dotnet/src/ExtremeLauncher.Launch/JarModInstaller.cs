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
 * Ported from PackProfile::installJarMods_internal.
 *
 * A jar mod is a jar whose classes are laid over the Minecraft jar rather than loaded as a mod -- the
 * pre-Forge way of modding, still used by a few old packs (and by the legacy FTB installer). Adding one
 * copies the jar into the instance's jarmods/ folder under a fresh id, writes a one-off component patch
 * that references it as a "local" library, and appends that component to the profile.
 *
 * In the port this is a Launch-level operation rather than a method on PackProfile: PackProfile is
 * decoupled from the instance's files, and copying jars and writing patches is instance I/O.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.Launch;

/// <summary>Installs jar mods into an instance, ported from PackProfile::installJarMods_internal.</summary>
public static class JarModInstaller
{
    /// <summary>
    /// Copies each jar into the instance, writes its component patch, appends it to <paramref name="profile"/>
    /// and saves the profile. Returns false if any file could not be copied or written; a run that fails
    /// part way may leave the earlier jars installed, as upstream's does.
    /// </summary>
    public static bool Install(InstancePaths paths, PackProfile profile, IEnumerable<string> jarFilePaths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(jarFilePaths);

        try
        {
            FileSystem.EnsureFolderPathExists(paths.PatchesDir);
            FileSystem.EnsureFolderPathExists(paths.JarModsDir);

            foreach (var source in jarFilePaths)
            {
                var id = Guid.NewGuid().ToString("D");
                var targetFilename = id + ".jar";
                var targetUid = "custom.jarmod." + id;
                var baseName = Path.GetFileNameWithoutExtension(source);
                var targetName = baseName + " (jar mod)";

                File.Copy(source, FileSystem.PathCombine(paths.JarModsDir, targetFilename));

                var file = new VersionFile { Uid = targetUid, Name = targetName };
                file.JarMods.Add(new Library("custom.jarmods:" + id + ":1")
                {
                    Filename = targetFilename,
                    DisplayNameOverride = baseName,
                    Hint = "local",
                });

                var patchPath = FileSystem.PathCombine(paths.PatchesDir, targetUid + ".json");
                File.WriteAllText(patchPath, OneSixVersionFormat.VersionFileToJson(file).ToJsonString());

                var component = new Component(targetUid, file);
                component.SetCachedData(targetName, string.Empty, [], [], isVolatile: false);

                profile.AppendComponent(component);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return profile.Save(paths.PackProfilePath);
    }
}
