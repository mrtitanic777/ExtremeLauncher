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
 * Ported from launcher/settings/INIFile.{h,cpp}.
 *
 * ==================== READ THIS BEFORE CHANGING THE ESCAPING ====================
 *
 * Upstream INIFile is a thin wrapper over QSettings with QSettings::IniFormat. That means the on-disk
 * format is not "INI" in general -- it is *QSettings' particular INI dialect*, and every existing
 * instance.cfg on every user's disk is written in it. .NET has no equivalent, so the dialect is
 * reimplemented here, matching Qt's iniEscapedString / iniUnescapedStringList:
 *
 *   - A value is quoted if it contains ';' ',' or '=', has leading/trailing whitespace, or already
 *     begins with '"'. This is why "env mesa=true" round-trips as "\"env mesa=true\"".
 *   - Control characters become \a \b \f \n \r \t \v; '"' and '\' are backslash-escaped; anything
 *     else below 0x20 becomes \xHH.
 *   - After a \0 or \xHH escape, a following hex digit is *also* escaped, so "\x1" followed by "2"
 *     cannot be misread as "\x12".
 *   - Unquoted commas separate list elements, which is how QStringList survives a round trip.
 *   - Comments are whole lines starting with ';' or '#'. There are no inline comments -- that is
 *     precisely why ';' forces quoting.
 *
 * VERIFICATION STATUS: the reader is pinned against literal file content inherited from
 * tests/INIFile_test.cpp, which is real ground truth. The writer is verified by round-trip only --
 * there was no Qt build or existing launcher install available to diff byte-for-byte against. Before
 * shipping, run a real instance.cfg from a Prism/MultiMC install through LoadFile+SaveFile and diff.
 * See PORTING.md.
 *
 * CONFIG VERSIONS
 *   (absent) -- pre-QSettings hand-rolled format; parsed by ParseLegacyFormat, upgraded to 1.2
 *   1.1      -- QSettings format, but values were double-quoted; unquoted on load, upgraded to 1.2
 *   1.2      -- current
 */

using System.Globalization;
using System.Text;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Settings;

/// <summary>A sectionless INI file, compatible with Qt's <c>QSettings::IniFormat</c>.</summary>
public sealed class IniFile
{
    public const string ConfigVersionKey = "ConfigVersion";
    public const string CurrentConfigVersion = "1.2";

    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public bool Contains(string key) => _values.ContainsKey(key);

    public object? Get(string key, object? defaultValue = null)
        => _values.TryGetValue(key, out var value) ? value : defaultValue;

    public string GetString(string key, string defaultValue = "")
        => Get(key) switch
        {
            null => defaultValue,
            string s => s,
            IEnumerable<string> list => string.Join(", ", list),
            var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? defaultValue,
        };

    public void Set(string key, object? value) => _values[key] = value;

    public bool Remove(string key) => _values.Remove(key);

    public void Clear() => _values.Clear();

    // ================================================================== loading

    public bool LoadFile(string fileName)
    {
        try
        {
            return LoadFromText(File.ReadAllText(fileName, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool LoadFromBytes(byte[] data) => LoadFromText(Encoding.UTF8.GetString(data));

    public bool LoadFromText(string text)
    {
        var raw = ParseIni(text);

        if (!raw.TryGetValue(ConfigVersionKey, out var version))
        {
            // No version marker: the pre-QSettings format, which needs the legacy parser.
            foreach (var (key, value) in ParseLegacyFormat(text))
            {
                _values[key] = value;
            }

            _values[ConfigVersionKey] = CurrentConfigVersion;
            return true;
        }

        if (string.Equals(version as string, "1.1", StringComparison.Ordinal))
        {
            // 1.1 wrote values that were quoted a second time; strip that layer.
            foreach (var (key, value) in raw)
            {
                _values[key] = value is string s ? Unquote(s) : value;
            }

            _values[ConfigVersionKey] = CurrentConfigVersion;
            return true;
        }

        foreach (var (key, value) in raw)
        {
            _values[key] = value;
        }

        return true;
    }

    // ================================================================== saving

    public bool SaveFile(string fileName)
    {
        if (!_values.ContainsKey(ConfigVersionKey))
        {
            _values[ConfigVersionKey] = CurrentConfigVersion;
        }

        try
        {
            FileSystem.Write(fileName, Encoding.UTF8.GetBytes(ToText()));
            return true;
        }
        catch (FileSystemException)
        {
            return false;
        }
    }

    public string ToText()
    {
        var builder = new StringBuilder();

        // QSettings sorts keys; matching that keeps diffs between the two implementations readable.
        foreach (var key in _values.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            builder.Append(key).Append('=').Append(EscapeValue(_values[key])).Append('\n');
        }

        return builder.ToString();
    }

    // ================================================================== QSettings INI dialect

    private static Dictionary<string, object?> ParseIni(string text)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();

            // Whole-line comments only; there are no inline comments in this dialect.
            if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[')
            {
                continue;
            }

            var separator = line.IndexOf('=');

            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (key.Length != 0)
            {
                result[key] = UnescapeValue(value);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses one value, resolving escapes and splitting unquoted commas into a list.
    /// </summary>
    /// <returns>A <see cref="string"/>, or a <see cref="List{T}"/> of strings for list values.</returns>
    internal static object UnescapeValue(string value)
    {
        var current = new StringBuilder();
        List<string>? list = null;

        var inQuotes = false;
        var i = 0;

        while (i < value.Length)
        {
            var c = value[i];

            if (c == '\\')
            {
                i++;

                if (i >= value.Length)
                {
                    break;
                }

                var next = value[i++];

                switch (next)
                {
                    case 'a': current.Append('\a'); break;
                    case 'b': current.Append('\b'); break;
                    case 'f': current.Append('\f'); break;
                    case 'n': current.Append('\n'); break;
                    case 'r': current.Append('\r'); break;
                    case 't': current.Append('\t'); break;
                    case 'v': current.Append('\v'); break;
                    case '"': current.Append('"'); break;
                    case '?': current.Append('?'); break;
                    case '\'': current.Append('\''); break;
                    case '\\': current.Append('\\'); break;

                    case 'x':
                    {
                        var start = i;

                        while (i < value.Length && i - start < 4 && Uri.IsHexDigit(value[i]))
                        {
                            i++;
                        }

                        if (i > start)
                        {
                            current.Append((char)Convert.ToInt32(value[start..i], 16));
                        }

                        break;
                    }

                    case >= '0' and <= '7':
                    {
                        // Octal, up to three digits including the one already consumed.
                        var start = i - 1;

                        while (i < value.Length && i - start < 3 && value[i] is >= '0' and <= '7')
                        {
                            i++;
                        }

                        current.Append((char)Convert.ToInt32(value[start..i], 8));
                        break;
                    }

                    default:
                        // Unknown escape: emit the character as-is, matching Qt.
                        current.Append(next);
                        break;
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                i++;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                // An unquoted comma means this value is a list.
                list ??= [];
                list.Add(current.ToString().Trim());
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (list is null)
        {
            return current.ToString();
        }

        list.Add(current.ToString().Trim());
        return list;
    }

    /// <summary>Renders a value using Qt's quoting and escaping rules.</summary>
    internal static string EscapeValue(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string s)
        {
            return EscapeString(s);
        }

        if (value is bool b)
        {
            // QVariant renders booleans lowercase.
            return b ? "true" : "false";
        }

        if (value is IEnumerable<string> strings)
        {
            return string.Join(", ", strings.Select(EscapeString));
        }

        if (value is System.Collections.IEnumerable sequence and not string)
        {
            return string.Join(
                ", ",
                sequence.Cast<object?>().Select(item => EscapeString(Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty)));
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string EscapeString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);

        var needsQuotes = false;
        var escapeNextIfDigit = false;

        foreach (var c in value)
        {
            // These would otherwise be read as a list separator, a comment, or a key/value split.
            if (c is ';' or ',' or '=')
            {
                needsQuotes = true;
            }

            if (escapeNextIfDigit && Uri.IsHexDigit(c))
            {
                // Stops "\x1" + "2" from being re-read as "\x12".
                builder.Append("\\x").Append(((int)c).ToString("x", CultureInfo.InvariantCulture));
                continue;
            }

            escapeNextIfDigit = false;

            switch (c)
            {
                case '\0':
                    builder.Append("\\0");
                    escapeNextIfDigit = true;
                    break;

                case '\a': builder.Append("\\a"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\v': builder.Append("\\v"); break;

                case '"':
                case '\\':
                    builder.Append('\\').Append(c);
                    break;

                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\x").Append(((int)c).ToString("x", CultureInfo.InvariantCulture));
                        escapeNextIfDigit = true;
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        var escaped = builder.ToString();

        // Leading or trailing whitespace would be eaten by the reader's Trim().
        if (escaped.Length != 0 && (escaped[0] == ' ' || escaped[^1] == ' '))
        {
            needsQuotes = true;
        }

        // A value that already starts with a quote must be wrapped, or the reader sees it as the
        // opening delimiter of a quoted section.
        if (needsQuotes || (escaped.Length != 0 && escaped[0] == '"'))
        {
            return '"' + escaped + '"';
        }

        return escaped;
    }

    /// <summary>Strips one layer of quoting, as the 1.1 -&gt; 1.2 migration requires.</summary>
    internal static string Unquote(string value)
        => (value.Contains(';', StringComparison.Ordinal)
            || value.Contains('=', StringComparison.Ordinal)
            || value.Contains(',', StringComparison.Ordinal))
           && value.Length >= 2
           && value.StartsWith('"')
           && value.EndsWith('"')
            ? value[1..^1]
            : value;

    /// <summary>
    /// The pre-QSettings format: <c>key=value</c> with <c>#</c> comments and <c>\n</c>/<c>\t</c>/<c>\#</c> escapes.
    /// </summary>
    internal static Dictionary<string, string> ParseLegacyFormat(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            /*
             * Strip a comment, but only at an UNESCAPED '#'. Scanned from the START of the line: a
             * line that is entirely a comment has its '#' at position 0, and one containing an '='
             * would otherwise be parsed as a setting. An empty line has to be tolerated too --
             * IndexOf(char, 1) on a zero-length string throws, which is how this was found.
             */
            for (var index = 0; index < line.Length; index++)
            {
                if (line[index] != '#')
                {
                    continue;
                }

                if (index > 0 && line[index - 1] == '\\')
                {
                    continue;
                }

                line = line[..index].Trim();
                break;
            }

            var separator = line.IndexOf('=');

            if (separator == -1)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            result[key] = Unquote(UnescapeLegacy(value));
        }

        return result;
    }

    private static string UnescapeLegacy(string value)
    {
        var builder = new StringBuilder(value.Length);
        var escaped = false;

        foreach (var c in value)
        {
            if (escaped)
            {
                builder.Append(c switch
                {
                    'n' => '\n',
                    't' => '\t',
                    '#' => '#',
                    _ => c,
                });

                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
