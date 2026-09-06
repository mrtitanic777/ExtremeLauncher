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
 * Ported from launcher/minecraft/gameoptions/GameOptions.{h,cpp}, less its Qt model half.
 *
 * MINECRAFT'S OWN options.txt, which the launcher reads so it can show and edit settings without
 * starting the game. The format is barely a format: one `key:value` per line, no escaping, no
 * sections, no comments.
 *
 * SO EVERYTHING HERE IS ABOUT NOT DAMAGING IT. This file belongs to Minecraft, not to the launcher --
 * it holds every keybind and video setting a player has ever changed, Minecraft rewrites it on every
 * exit, and it contains keys this launcher has never heard of and newer versions will add more. The
 * rules that follow all come from that:
 *
 *   - ORDER IS PRESERVED. A list, not a dictionary. Rewriting the file sorted would work perfectly and
 *     produce a diff against every backup the user has.
 *   - UNKNOWN KEYS SURVIVE untouched, because most of them are unknown.
 *   - DUPLICATE KEYS SURVIVE. The format permits them and Minecraft reads the last; collapsing them
 *     would silently change which one wins.
 *   - A LINE WITH NO COLON IS SKIPPED, not treated as an error. There is no such thing as a malformed
 *     options.txt worth refusing to open.
 */

using System.Globalization;
using System.Text;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

/// <summary>One line of options.txt.</summary>
public sealed class GameOptionItem
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class GameOptions
{
    /// <summary>The key holding the file's format version, stored apart from the rest.</summary>
    public const string VersionKey = "version";

    private GameOptions(string path) => Path = path;

    public string Path { get; }

    /// <summary>Whether the file was read. False for a missing or unreadable one.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    /// The format version, or 0 when the file did not say.
    /// </summary>
    /// <remarks>
    /// Held separately rather than as an ordinary entry, and written back FIRST — which is where
    /// Minecraft puts it. Zero means absent, and an absent version is not invented on save: an old
    /// file that never had one should not gain one from being opened.
    /// </remarks>
    public int Version { get; set; }

    /// <summary>Every other line, in the order the file had them.</summary>
    public List<GameOptionItem> Contents { get; } = [];

    /// <summary>Reads options.txt, or returns an unloaded instance if it cannot be read.</summary>
    public static GameOptions Load(string path)
    {
        var options = new GameOptions(path);

        options.Reload();

        return options;
    }

    /// <summary>Re-reads the file, discarding anything held.</summary>
    public bool Reload()
    {
        Contents.Clear();
        Version = 0;
        IsLoaded = false;

        if (!File.Exists(Path))
        {
            return false;
        }

        string[] lines;

        try
        {
            lines = File.ReadAllLines(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var line in lines)
        {
            /*
             * The FIRST colon separates. A value may contain more -- keybinds are written as
             * "key.keyboard.left.control" and resource pack lists as JSON arrays -- so splitting on
             * every colon would truncate them.
             */
            var separator = line.IndexOf(':', StringComparison.Ordinal);

            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator];

            /*
             * A trailing carriage return is stripped, which upstream does not do: it chops only '\n',
             * so a file saved with CRLF -- by a user editing in Notepad, say -- leaves '\r' on the end
             * of every value. Nothing then matches "true" or "fullscreen", and the stray byte is
             * written straight back on save.
             */
            var value = line[(separator + 1)..].TrimEnd('\r');

            if (key == VersionKey)
            {
                Version = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;

                continue;
            }

            Contents.Add(new GameOptionItem { Key = key, Value = value });
        }

        IsLoaded = true;

        return true;
    }

    /// <summary>The first value for a key, or null.</summary>
    public string? Get(string key) => Contents.Find(i => i.Key == key)?.Value;

    /// <summary>Sets a key, adding it at the end if it is not already there.</summary>
    /// <remarks>
    /// Updates the FIRST match rather than the last. Minecraft reads the last of a duplicated key, so
    /// this is not quite the same as changing what the game sees — but adding a third copy would be
    /// worse, and a duplicated key in a real options.txt is a corruption rather than a feature.
    /// </remarks>
    public void Set(string key, string value)
    {
        if (Contents.Find(i => i.Key == key) is { } existing)
        {
            existing.Value = value;

            return;
        }

        Contents.Add(new GameOptionItem { Key = key, Value = value });
    }

    /// <summary>Writes the file back.</summary>
    /// <remarks>
    /// Always LF, never CRLF, on every platform — which is what Minecraft itself writes, and what
    /// keeps a file from gaining line-ending churn just because the launcher opened it.
    /// </remarks>
    public bool Save()
    {
        var builder = new StringBuilder();

        // First, and only if there is one. See the note on Version.
        if (Version != 0)
        {
            builder.Append(VersionKey).Append(':')
                .Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var item in Contents)
        {
            builder.Append(item.Key).Append(':').Append(item.Value).Append('\n');
        }

        try
        {
            // Through FileSystem.Write, so a failure part-way leaves the original rather than a stub.
            FileSystem.Write(Path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString()));

            return true;
        }
        catch (FileSystemException)
        {
            return false;
        }
    }
}
