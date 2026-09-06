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
 * Ported in behaviour from launcher/icons/IconList.cpp.
 *
 * WHAT ICONS EXIST AND WHERE THEY COME FROM. Every instance has carried an `iconKey` in its
 * instance.cfg since the very first wave -- the vanilla creator writes "default", the Modrinth
 * importer writes "default" -- and nothing has ever been able to read one back, let alone change it.
 * Every instance in this launcher looks identical.
 *
 * TWO SOURCES, and the difference matters at every turn:
 *
 *   built-in   shipped with the launcher, keyed by name. Cannot be deleted or overwritten.
 *   user       files in <data>/icons. Added by dropping a file in, removable, and they WIN on a key
 *              clash -- which is upstream's rule and the only way to replace a built-in you dislike.
 *
 * IT DOES NOT LOAD IMAGES. Decoding a PNG is a UI toolkit's job and belongs where Avalonia is; this
 * resolves a key to a source and says whether this build can draw it. That keeps the whole thing
 * testable without a rendering stack, which is what the file-shuffling parts actually need.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Launch;

/// <summary>Where an icon comes from.</summary>
public enum IconSource
{
    /// <summary>Shipped with the launcher.</summary>
    BuiltIn,

    /// <summary>A file in the user's icons folder.</summary>
    User,
}

/// <summary>One icon that can be given to an instance.</summary>
public sealed record IconEntry(string Key, IconSource Source, string FilePath = "")
{
    /// <summary>What to show under it. The key with separators turned back into spaces.</summary>
    public string DisplayName => Prettify(Key);

    /// <summary>
    /// Whether this build can actually draw it.
    /// </summary>
    /// <remarks>
    /// False for an SVG, which upstream accepts and this port cannot render without another
    /// dependency. Listed anyway rather than hidden -- see IconUtils -- because an icons folder shared
    /// with an upstream install contains them, and silently dropping them would make the launcher
    /// disagree with the folder it is showing.
    /// </remarks>
    public bool IsRenderable => Source == IconSource.BuiltIn
        || IconUtils.IsRenderableSuffix(Path.GetExtension(FilePath));

    private static string Prettify(string key)
    {
        var spaced = key.Replace('_', ' ').Replace('-', ' ');

        return spaced.Length == 0 ? key : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}

public sealed class IconList
{
    /// <summary>What an instance with no icon of its own gets.</summary>
    public const string DefaultKey = "default";

    private readonly string _folder;

    private readonly List<string> _builtIn;

    /// <param name="folder">The user's icons folder. Created on demand, not up front.</param>
    /// <param name="builtInKeys">
    /// The keys the application ships. Passed in rather than discovered, because they live in the
    /// UI project's resources and this layer must not depend on it.
    /// </param>
    public IconList(string folder, IEnumerable<string>? builtInKeys = null)
    {
        _folder = folder;
        _builtIn = builtInKeys?.ToList() ?? [];

        _builtIn.Sort(StringComparer.OrdinalIgnoreCase);
    }

    public string Folder => _folder;

    /// <summary>Every icon on offer, user files first where a key appears in both.</summary>
    public IReadOnlyList<IconEntry> All()
    {
        var entries = new List<IconEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /*
         * USER ICONS FIRST, so a key present in both wins for the user. That is upstream's rule and
         * the only way somebody can replace a built-in icon they dislike -- drop a file with the same
         * name into the folder.
         */
        foreach (var path in UserFiles())
        {
            var key = Path.GetFileNameWithoutExtension(path);

            if (key.Length != 0 && seen.Add(key))
            {
                entries.Add(new IconEntry(key, IconSource.User, path));
            }
        }

        foreach (var key in _builtIn)
        {
            if (seen.Add(key))
            {
                entries.Add(new IconEntry(key, IconSource.BuiltIn));
            }
        }

        return entries;
    }

    /// <summary>Resolves a key, falling back the way upstream does.</summary>
    /// <remarks>
    /// A MISSING KEY RESOLVES TO THE DEFAULT rather than to nothing. An instance whose icon file was
    /// deleted, or one copied from a machine that had it, must still draw something -- and an instance
    /// with no tile at all reads as a broken instance rather than a missing picture.
    /// </remarks>
    public IconEntry Resolve(string key)
    {
        var all = All();

        if (key.Length != 0
            && all.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal)) is { } found)
        {
            return found;
        }

        return all.FirstOrDefault(e => string.Equals(e.Key, DefaultKey, StringComparison.Ordinal))
            ?? new IconEntry(DefaultKey, IconSource.BuiltIn);
    }

    public bool Contains(string key)
        => All().Any(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Copies a file into the icons folder and returns the key it got.
    /// </summary>
    /// <returns>The key, or empty when the file was not an icon or could not be copied.</returns>
    public string Import(string sourcePath)
    {
        if (sourcePath.Length == 0 || !File.Exists(sourcePath))
        {
            return string.Empty;
        }

        if (!IconUtils.IsIconSuffix(Path.GetExtension(sourcePath)))
        {
            return string.Empty;
        }

        try
        {
            FileSystem.EnsureFolderPathExists(_folder);

            var extension = Path.GetExtension(sourcePath);
            var key = Path.GetFileNameWithoutExtension(sourcePath);

            if (key.Length == 0)
            {
                return string.Empty;
            }

            /*
             * A CLASHING NAME IS SUFFIXED, not overwritten. Importing "creeper.png" twice from two
             * different folders is a thing people do, and silently replacing the first is how somebody
             * loses an icon they were using -- every instance keyed to it would change picture at once.
             */
            var target = FileSystem.PathCombine(_folder, key + extension);

            if (File.Exists(target))
            {
                for (var n = 2; ; n++)
                {
                    var candidate = FileSystem.PathCombine(_folder, $"{key}-{n}{extension}");

                    if (!File.Exists(candidate))
                    {
                        target = candidate;
                        key = $"{key}-{n}";

                        break;
                    }
                }
            }

            File.Copy(sourcePath, target);

            return key;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Removes a user icon.
    /// </summary>
    /// <remarks>
    /// A BUILT-IN CANNOT BE REMOVED -- there is no file to remove, and the key would come straight
    /// back on the next listing, which would look like the delete had silently failed.
    /// </remarks>
    public bool Remove(string key)
    {
        var entry = All().FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));

        if (entry is not { Source: IconSource.User })
        {
            return false;
        }

        try
        {
            // To the recycle bin, as instance deletion does: an icon is somebody's own file, and
            // "remove from the launcher" should not mean "destroy".
            if (Trash.TryTrash(entry.FilePath, out _))
            {
                return true;
            }

            File.Delete(entry.FilePath);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<string> UserFiles()
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(_folder)
                .Where(p => IconUtils.IsIconSuffix(Path.GetExtension(p)))
                .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
