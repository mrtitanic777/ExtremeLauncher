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
 * Ported from launcher/icons/IconUtils.cpp.
 *
 * WHICH FILES COUNT AS AN ICON, and how a key maps to one on disk. An instance stores an `iconKey` in
 * its instance.cfg -- "creeper_legacy", or the base name of a file the user dropped in -- and this is
 * what turns that back into a path.
 */

namespace ExtremeLauncher.Core;

public static class IconUtils
{
    /// <summary>
    /// The extensions upstream accepts, in its order.
    /// </summary>
    /// <remarks>
    /// SVG IS LISTED AND NOT RENDERED by this port -- Avalonia has no built-in SVG support and adding
    /// one is a dependency decision, not an oversight. It stays in the list because the FILTER is a
    /// compatibility surface: an icons folder shared with an upstream install has .svg files in it,
    /// and hiding them would make the two launchers disagree about what is there. See IconList, which
    /// marks such an entry unrenderable rather than pretending it does not exist.
    /// </remarks>
    public static readonly string[] ValidExtensions =
        ["svg", "png", "ico", "gif", "jpg", "jpeg", "webp"];

    /// <summary>The extensions this build can actually draw.</summary>
    public static readonly string[] RenderableExtensions =
        ["png", "ico", "gif", "jpg", "jpeg", "webp"];

    public static bool IsIconSuffix(string suffix)
        => ValidExtensions.Contains(Normalise(suffix), StringComparer.OrdinalIgnoreCase);

    public static bool IsRenderableSuffix(string suffix)
        => RenderableExtensions.Contains(Normalise(suffix), StringComparer.OrdinalIgnoreCase);

    /// <summary>A file-picker filter listing the accepted extensions.</summary>
    public static IReadOnlyList<string> FilterPatterns
        => ValidExtensions.Select(e => "*." + e).ToArray();

    /// <summary>
    /// Finds the file in a folder whose name matches an icon key.
    /// </summary>
    /// <remarks>
    /// Upstream matches on the complete base name OR the whole file name, which is not redundant: an
    /// icon key can legitimately contain a dot, and "my.icon" as a key must find "my.icon.png" by the
    /// first rule and a file literally called "my.icon" by the second.
    /// </remarks>
    /// <returns>The full path, or empty when nothing matches.</returns>
    public static string FindBestIconIn(string folder, string iconKey)
    {
        if (folder.Length == 0 || iconKey.Length == 0 || !Directory.Exists(folder))
        {
            return string.Empty;
        }

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            var baseName = Path.GetFileNameWithoutExtension(path);

            if ((string.Equals(baseName, iconKey, StringComparison.Ordinal)
                 || string.Equals(name, iconKey, StringComparison.Ordinal))
                && IsIconSuffix(Path.GetExtension(path)))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static string Normalise(string suffix) => suffix.TrimStart('.');
}
