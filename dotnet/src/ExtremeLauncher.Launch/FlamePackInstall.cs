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
 * The core of flame/FlameInstanceCreationTask.cpp's createInstance: turning an already-extracted
 * CurseForge pack into a staged instance. A CurseForge pack zip holds a manifest.json (the loader, the
 * Minecraft version, and a list of file ids to fetch) and an "overrides" folder of loose files that go
 * straight into the game directory. This builds the instance from those two things -- the pack profile
 * and instance.cfg, with the overrides moved into place -- which is everything the import does that
 * does NOT need the network. Resolving the file ids to download URLs (FlameFileResolver) and fetching
 * them, with the blocked-mod handling that entails, is the other half and a later wave.
 *
 * The components come from the already-ported PackComponents.FromFlame (Minecraft version normalised,
 * loader mapped, the NeoForge 1.20.1 quirk handled); this adds the instance I/O around it.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.Launch;

/// <summary>Builds an instance from an extracted CurseForge pack.</summary>
public static class FlamePackBuilder
{
    /// <summary>
    /// Turns an extracted CurseForge pack — its manifest already parsed, its <c>overrides</c> folder
    /// sitting under the instance root — into a staged instance: the pack profile (Minecraft plus the
    /// loader the manifest names) and instance.cfg, with the overrides moved in to become the game
    /// folder. The manifest.json, when present on disk, is filed under <c>flame/</c> for later update
    /// checks, as upstream does.
    /// </summary>
    public static void BuildFromExtracted(
        InstancePaths paths,
        FlamePackManifest manifest,
        RuntimeContext runtimeContext,
        string iconKey = "default",
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);

        // The overrides folder becomes the game directory. Missing is a warning upstream, not an error:
        // a pack that was already imported once has had its overrides moved away.
        var overrides = FileSystem.PathCombine(paths.InstanceRoot, manifest.Overrides);

        if (Directory.Exists(overrides) && !FileSystem.Move(overrides, paths.GameRoot))
        {
            throw new LauncherException($"Could not rename the overrides folder: {manifest.Overrides}");
        }

        // The manifest is kept under flame/ so an update can tell what the pack shipped.
        var manifestPath = FileSystem.PathCombine(paths.InstanceRoot, "manifest.json");

        if (File.Exists(manifestPath))
        {
            var filed = FileSystem.PathCombine(paths.InstanceRoot, "flame", "manifest.json");
            FileSystem.EnsureFilePathExists(filed);
            FileSystem.Move(manifestPath, filed);
        }

        var profile = new PackProfile(runtimeContext);

        foreach (var component in PackComponents.FromFlame(manifest))
        {
            profile.SetComponentVersion(component.Uid, component.Version, component.Important);
        }

        profile.Save(paths.PackProfilePath);

        var name = displayName is { Length: > 0 } ? displayName : manifest.Name;

        var settings = new IniSettingsObject(paths.ConfigPath);
        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);
        settings.Set("InstanceType", "OneSix");
        settings.Set("name", name);
        settings.Set("iconKey", ResolveIconKey(iconKey, manifest.Name));
    }

    /// <summary>
    /// The instance icon: a caller-chosen one wins; otherwise a couple of well-known pack names get a
    /// fitting default (Direwolf20 packs an old Steve icon, FTB packs the FTB logo), matching upstream.
    /// A pack matching neither keeps the plain default.
    /// </summary>
    public static string ResolveIconKey(string iconKey, string packName)
    {
        if (iconKey != "default")
        {
            return iconKey;
        }

        if (packName.Contains("Direwolf20", StringComparison.Ordinal))
        {
            return "steve";
        }

        if (packName.Contains("FTB", StringComparison.Ordinal)
            || packName.Contains("Feed The Beast", StringComparison.Ordinal))
        {
            return "ftb_logo";
        }

        return "default";
    }
}
