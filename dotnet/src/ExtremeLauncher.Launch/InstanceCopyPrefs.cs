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
 * Ported from launcher/InstanceCopyPrefs.{h,cpp}.
 *
 * WHAT TO LEAVE BEHIND WHEN COPYING AN INSTANCE. The user ticks what to bring -- saves, mods, screen-
 * shots -- and this turns the unticked boxes into an EXCLUSION pattern, because copying is done by
 * walking the source and skipping matches rather than by assembling a list.
 *
 * SO EVERY BOX IS INVERTED, and each unticked one names the several folders that box actually covers:
 * "resource packs" is two directories because Minecraft renamed them, "servers" is three files, and
 * "mods" also excludes config -- a mods folder without its configs produces an instance that crashes
 * differently from a clean one, which is worse than either.
 */

using System.Text.RegularExpressions;

namespace ExtremeLauncher.Launch;

/// <summary>Which parts of an instance a copy should bring along.</summary>
public sealed class InstanceCopyPrefs
{
    public bool CopySaves { get; set; } = true;

    public bool KeepPlaytime { get; set; } = true;

    public bool CopyGameOptions { get; set; } = true;

    public bool CopyResourcePacks { get; set; } = true;

    public bool CopyShaderPacks { get; set; } = true;

    public bool CopyServers { get; set; } = true;

    public bool CopyMods { get; set; } = true;

    public bool CopyScreenshots { get; set; } = true;

    /// <summary>Link instead of copying, so both instances share the files.</summary>
    public bool UseSymLinks { get; set; }

    public bool LinkRecursively { get; set; }

    public bool UseHardLinks { get; set; }

    /// <summary>Copy-on-write, where the filesystem supports it.</summary>
    public bool UseClone { get; set; }

    /// <summary>
    /// The paths to skip, as one regular expression, or an empty string to copy everything.
    /// </summary>
    /// <remarks>
    /// Every entry is anchored under the game directory, and the anchor is <c>[.]?minecraft/</c> --
    /// with the dot OPTIONAL, because the folder is ".minecraft" in some layouts and "minecraft" in
    /// others. An unanchored pattern would match a user's own "saves" folder anywhere in the tree.
    /// </remarks>
    /// <param name="additionalFilters">
    /// Extra paths to skip, appended verbatim and anchored the same way.
    /// </param>
    public string GetSelectedFiltersAsRegex(IEnumerable<string>? additionalFilters = null)
    {
        var filters = new List<string>();

        // Inverted throughout: an UNticked box adds an exclusion.
        if (!CopySaves)
        {
            filters.Add("saves");
        }

        if (!CopyGameOptions)
        {
            filters.Add("options.txt");
        }

        if (!CopyResourcePacks)
        {
            // Two names for one thing: Minecraft renamed texture packs to resource packs in 1.6.
            filters.Add("resourcepacks");
            filters.Add("texturepacks");
        }

        if (!CopyShaderPacks)
        {
            filters.Add("shaderpacks");
        }

        if (!CopyServers)
        {
            filters.Add("servers.dat");

            // The backup Minecraft writes beside it, which would otherwise restore the list.
            filters.Add("servers.dat_old");

            filters.Add("server-resource-packs");
        }

        if (!CopyMods)
        {
            filters.Add("coremods");
            filters.Add("mods");

            /*
             * CONFIG GOES WITH THE MODS, and it has to: configs for mods that are not there produce an
             * instance that fails differently from a clean one, which is harder to diagnose than
             * either. Upstream groups them for the same reason.
             */
            filters.Add("config");
        }

        if (!CopyScreenshots)
        {
            filters.Add("screenshots");
        }

        if (additionalFilters is not null)
        {
            filters.AddRange(additionalFilters);
        }

        if (filters.Count == 0)
        {
            return string.Empty;
        }

        // The anchor leads, and rejoins between every alternative: ".minecraft/saves|.minecraft/mods".
        return MinecraftRoot + string.Join("|" + MinecraftRoot, filters);
    }

    /// <summary>The optional-dot anchor every exclusion is prefixed with.</summary>
    public const string MinecraftRoot = "[.]?minecraft/";

    /// <summary>Whether a path relative to the instance root would be skipped.</summary>
    /// <remarks>
    /// Provided because the pattern is the only artifact upstream produces, and a caller that wants
    /// to know "is this file excluded" would otherwise rebuild the regex itself and get the anchoring
    /// subtly wrong.
    /// </remarks>
    public bool IsExcluded(string relativePath, IEnumerable<string>? additionalFilters = null)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var pattern = GetSelectedFiltersAsRegex(additionalFilters);

        if (pattern.Length == 0)
        {
            return false;
        }

        // Matched against forward slashes, as the pattern is written.
        return Regex.IsMatch(
            relativePath.Replace('\\', '/'),
            pattern,
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
    }
}
