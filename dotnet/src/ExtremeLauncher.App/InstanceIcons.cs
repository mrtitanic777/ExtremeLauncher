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
 * Turning an icon key into something Avalonia can draw.
 *
 * THE ONLY PART OF THE ICON SYSTEM THAT NEEDS A UI TOOLKIT, which is exactly why IconList does not do
 * it: decoding a PNG is Avalonia's job, and keeping it here leaves the file-shuffling testable without
 * a rendering stack.
 *
 * BITMAPS ARE CACHED BY KEY. The instance list draws every tile on every layout pass, and a
 * twenty-instance list would otherwise decode twenty PNGs each time the window is resized. They are
 * small and there are at most a few dozen, so nothing is ever evicted.
 */

using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.App;

public static class InstanceIcons
{
    private const string ResourceRoot = "avares://ExtremeLauncher/Assets/icons/";

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    private static string[]? _builtInKeys;

    /// <summary>
    /// The keys shipped with the application, read from its own resources.
    /// </summary>
    /// <remarks>
    /// Discovered rather than listed, so adding a PNG to Assets/icons is the whole job of adding a
    /// built-in icon -- a hand-written list is a second place to forget.
    /// </remarks>
    public static IReadOnlyList<string> BuiltInKeys => _builtInKeys ??= DiscoverBuiltIns();

    /// <summary>Loads the bitmap for an entry, or null when it cannot be drawn.</summary>
    public static Bitmap? Load(IconEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var cacheKey = entry.Source == IconSource.User ? "user:" + entry.FilePath : "builtin:" + entry.Key;

        return Cache.GetOrAdd(cacheKey, _ => LoadUncached(entry));
    }

    /// <summary>Loads the bitmap for a key, resolving it through the list first.</summary>
    public static Bitmap? Load(IconList icons, string key)
    {
        ArgumentNullException.ThrowIfNull(icons);

        return Load(icons.Resolve(key));
    }

    /// <summary>Forgets a cached bitmap, so a replaced file is picked up.</summary>
    public static void Forget(string filePath) => Cache.TryRemove("user:" + filePath, out _);

    private static Bitmap? LoadUncached(IconEntry entry)
    {
        try
        {
            if (entry.Source == IconSource.BuiltIn)
            {
                var uri = new Uri(ResourceRoot + entry.Key + ".png");

                return AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
            }

            /*
             * An SVG lands here and is refused. IconList already marks it unrenderable; this is the
             * second guard, because a file whose extension lies would otherwise reach the decoder and
             * throw somewhere far less obvious.
             */
            if (!IconUtils.IsRenderableSuffix(Path.GetExtension(entry.FilePath))
                || !File.Exists(entry.FilePath))
            {
                return null;
            }

            return new Bitmap(entry.FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // A corrupt or truncated image must not take the window down. The tile falls back to
            // nothing drawn, which is what a missing icon looks like anyway.
            return null;
        }
    }

    private static string[] DiscoverBuiltIns()
    {
        try
        {
            return AssetLoader.GetAssets(new Uri(ResourceRoot), null)
                .Select(u => Path.GetFileNameWithoutExtension(u.AbsolutePath))
                .Where(k => k.Length != 0)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }
}
