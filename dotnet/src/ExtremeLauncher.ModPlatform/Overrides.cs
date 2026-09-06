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
 * Ported from launcher/modplatform/helpers/OverrideUtils.{h,cpp}.
 *
 * REMEMBERING WHAT A PACK PUT THERE. Both pack formats ship an "overrides" folder that is copied
 * wholesale over the instance -- configs, scripts, resource packs, anything. The files land mixed in
 * with the user's own, so on the next update the launcher has no way to tell which of them it wrote,
 * and every override the pack later drops would linger forever.
 *
 * The answer is a plain list, written beside the instance at import time: one relative path per line,
 * for every file the overrides folder contained. Update reads it back and deletes them before copying
 * the new set.
 */

using System.Text;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

public static class Overrides
{
    /// <summary>The record's filename for an overrides folder called <paramref name="name"/>.</summary>
    public static string RecordFileName(string name) => name + ".txt";

    /// <summary>
    /// Records every file in an overrides folder, so a later update can undo it.
    /// </summary>
    /// <param name="name">The overrides folder's name, e.g. "overrides" or "client-overrides".</param>
    /// <param name="parentFolder">Where the record is written.</param>
    /// <param name="overridePath">The folder to list.</param>
    /// <remarks>
    /// UPSTREAM DERIVES THE RELATIVE PATH BY SPLITTING ON THE FOLDER'S NAME -- <c>split(name).last()</c>
    /// then dropping one leading character. That works until the name appears elsewhere in the path,
    /// which is not exotic: a staging directory under a pack called "overrides", or a user folder
    /// containing the word, and every recorded path is silently truncated at the wrong point. The
    /// record then names files that do not exist, so the next update deletes nothing and the stale
    /// overrides stay forever.
    ///
    /// Computed as a real relative path here instead. Same output for the ordinary case, and correct
    /// for the rest.
    /// </remarks>
    public static void Write(string name, string parentFolder, string overridePath)
    {
        ArgumentNullException.ThrowIfNull(name);

        var recordPath = FileSystem.PathCombine(parentFolder, RecordFileName(name));

        FileSystem.EnsureFilePathExists(recordPath);

        var builder = new StringBuilder();

        // Missing is not empty: a pack with no overrides still gets a record, so a later read can tell
        // "nothing was written" from "this was never imported".
        if (Directory.Exists(overridePath))
        {
            var root = Path.GetFullPath(overridePath);

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                // Forward slashes, so a record written on Windows reads correctly elsewhere.
                builder.Append(Path.GetRelativePath(root, file).Replace('\\', '/')).Append('\n');
            }
        }

        File.WriteAllText(recordPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Reads back a record written by <see cref="Write"/>.
    /// </summary>
    /// <returns>
    /// The relative paths, or an empty list when no record exists -- which is the normal state for an
    /// instance imported before records were kept.
    /// </returns>
    /// <remarks>
    /// Blank lines are dropped. Upstream's read loop appends before testing for the end, so the list
    /// it returns always ends with an empty string, and every caller opens with a check for one. That
    /// check is the tell; the empty entry is not meant to be there.
    /// </remarks>
    public static List<string> Read(string name, string parentFolder)
    {
        var recordPath = FileSystem.PathCombine(parentFolder, RecordFileName(name));

        if (!File.Exists(recordPath))
        {
            return [];
        }

        return
        [
            .. File.ReadAllLines(recordPath)
                .Select(line => line.Trim())
                .Where(line => line.Length != 0),
        ];
    }

    /// <summary>
    /// The absolute paths an update should delete before applying a pack's new overrides.
    /// </summary>
    /// <remarks>
    /// Every recorded path is checked for containment before being handed back. The record is written
    /// by this launcher, but it sits in the instance folder where anything could have edited it, and
    /// the result of this call is a delete list -- the one place where a bad path does the most damage.
    /// </remarks>
    public static List<string> GetStalePaths(string name, string parentFolder, string gameRoot)
    {
        var root = Path.GetFullPath(gameRoot);
        var result = new List<string>();

        foreach (var relative in Read(name, parentFolder))
        {
            string resolved;

            try
            {
                resolved = Path.GetFullPath(Path.Combine(root, relative));
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }

            // Never delete outside the instance, whatever the record says.
            if (resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                result.Add(resolved);
            }
        }

        return result;
    }
}
