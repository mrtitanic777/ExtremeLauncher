// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
 *  Copyright (C) 2023 flowln <flowlnlnln@gmail.com>
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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from launcher/StringUtils.{h,cpp}.
 *
 * NOTE: there is no upstream test for this file. The ports below are deliberately literal,
 * including the quirks called out in comments, so that sort order and update-feed parsing do not
 * change behaviour for existing installs. Tests here are characterization tests, not a contract
 * inherited from the Qt suite.
 *
 * NOT YET PORTED: truncateUrlHumanFriendly() -- its only caller is launcher/net/NetRequest.cpp, and
 * it depends on QUrl::toDisplayString semantics. It belongs with wave 3 (Net).
 */

using System.Globalization;
using System.Text.RegularExpressions;

namespace ExtremeLauncher.Core;

/// <summary>Mirrors Qt's <c>Qt::CaseSensitivity</c>.</summary>
public enum CaseSensitivity
{
    CaseInsensitive = 0,
    CaseSensitive = 1,
}

public static partial class StringUtils
{
    private static readonly string[] UnitsSi = ["KB", "MB", "GB", "TB"];
    private static readonly string[] UnitsKibi = ["KiB", "MiB", "GiB", "TiB"];

    /// <summary>
    /// Compares two strings treating embedded digit runs as numbers, so "pack2" sorts before "pack10".
    /// </summary>
    /// <remarks>
    /// Taken from Qt (which does not expose it publicly) by way of the C++ launcher. The bounds are
    /// intentionally <c>&lt;=</c> rather than <c>&lt;</c>: the loop reads one position past the end of
    /// each string, where <see cref="GetNextChar"/> yields NUL.
    /// </remarks>
    public static int NaturalCompare(string s1, string s2, CaseSensitivity cs)
    {
        int l1 = 0, l2 = 0;

        while (l1 <= s1.Length && l2 <= s2.Length)
        {
            // Skip spaces and tabs.
            var c1 = GetNextChar(s1, l1);
            while (char.IsWhiteSpace(c1))
            {
                c1 = GetNextChar(s1, ++l1);
            }

            var c2 = GetNextChar(s2, l2);
            while (char.IsWhiteSpace(c2))
            {
                c2 = GetNextChar(s2, ++l2);
            }

            if (char.IsDigit(c1) && char.IsDigit(c2))
            {
                // Skip leading zeros, so "02" and "2" compare equal here.
                while (DigitValue(c1) == 0)
                {
                    c1 = GetNextChar(s1, ++l1);
                }

                while (DigitValue(c2) == 0)
                {
                    c2 = GetNextChar(s2, ++l2);
                }

                var lookAheadLocation1 = l1;
                var lookAheadLocation2 = l2;
                var currentReturnValue = 0;

                // Walk to the end of the digit run, remembering the first difference seen.
                for (char lookAhead1 = c1, lookAhead2 = c2;
                     lookAheadLocation1 <= s1.Length && lookAheadLocation2 <= s2.Length;
                     lookAhead1 = GetNextChar(s1, ++lookAheadLocation1),
                     lookAhead2 = GetNextChar(s2, ++lookAheadLocation2))
                {
                    var is1ADigit = lookAhead1 != '\0' && char.IsDigit(lookAhead1);
                    var is2ADigit = lookAhead2 != '\0' && char.IsDigit(lookAhead2);

                    if (!is1ADigit && !is2ADigit)
                    {
                        break;
                    }

                    // A shorter digit run means a smaller number.
                    if (!is1ADigit)
                    {
                        return -1;
                    }

                    if (!is2ADigit)
                    {
                        return 1;
                    }

                    if (currentReturnValue == 0)
                    {
                        if (lookAhead1 < lookAhead2)
                        {
                            currentReturnValue = -1;
                        }
                        else if (lookAhead1 > lookAhead2)
                        {
                            currentReturnValue = 1;
                        }
                    }
                }

                if (currentReturnValue != 0)
                {
                    return currentReturnValue;
                }
            }

            if (cs == CaseSensitivity.CaseInsensitive)
            {
                // Invariant, not current-culture: QChar::toLower() is locale-independent, so using
                // char.ToLower() here would diverge under e.g. the Turkish dotted-I rules.
                if (!char.IsLower(c1))
                {
                    c1 = char.ToLowerInvariant(c1);
                }

                if (!char.IsLower(c2))
                {
                    c2 = char.ToLowerInvariant(c2);
                }
            }

            var r = CompareCharsLocaleAware(c1, c2);

            if (r < 0)
            {
                return -1;
            }

            if (r > 0)
            {
                return 1;
            }

            l1 += 1;
            l2 += 1;
        }

        // The two strings are the same (02 == 2), so fall back to the normal sort.
        return string.Compare(
            s1,
            s2,
            cs == CaseSensitivity.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// The <paramref name="decimalPoints"/> argument affects only the rollover threshold, not the
    /// rendered precision, which is always two places. That matches the C++ implementation.
    /// Note also that the loop is do/while, so byte counts below one unit still get scaled once --
    /// <c>0</c> renders as <c>"0.00 KiB"</c>.
    /// </remarks>
    public static string HumanReadableFileSize(double bytes, bool useSi = false, int decimalPoints = 1)
    {
        var units = useSi ? UnitsSi : UnitsKibi;
        var scale = useSi ? 1000 : 1024;

        var u = -1;
        var r = Math.Pow(10, decimalPoints);

        do
        {
            bytes /= scale;
            u++;
        }
        while (Math.Round(Math.Abs(bytes) * r) / r >= scale && u < units.Length - 1);

        return bytes.ToString("F2", CultureInfo.InvariantCulture) + " " + units[u];
    }

    /// <summary>Equivalent of <c>QUuid::createUuid().toString(QUuid::Id128)</c>: 32 hex digits.</summary>
    public static string GetRandomAlphaNumeric() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Shortens a URL for display by replacing middle path segments with "...", keeping the host and
    /// the final segment readable.
    /// </summary>
    /// <param name="hardLimit">
    /// If dropping segments still leaves it too long, truncate the raw string instead.
    /// </param>
    /// <remarks>
    /// QUIRK preserved: upstream appends <c>QUrl::query()</c>, which does *not* include the leading
    /// '?', so a truncated URL with a query renders it glued onto the path. Cosmetic -- the result
    /// only ever reaches a status label -- but reproduced so the strings match.
    /// </remarks>
    public static string TruncateUrlHumanFriendly(Uri url, int maxLength, bool hardLimit = false)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Equivalent of RemoveUserInfo | RemoveFragment | NormalizePathSegments.
        var display = $"{url.Scheme}://{url.Authority}{url.AbsolutePath}{url.Query}";

        if (display.Length <= maxLength)
        {
            return display;
        }

        var query = url.Query.StartsWith('?') ? url.Query[1..] : url.Query;
        var segments = url.AbsolutePath.Split('/').ToList();

        var lastSegment = segments[^1];
        segments.RemoveAt(segments.Count - 1);

        if (segments.Count >= 1 && segments[0].Length == 0)
        {
            segments.RemoveAt(0); // Drop the empty leading segment from the leading '/'.
        }

        if (segments.Count >= 1)
        {
            segments.RemoveAt(segments.Count - 1); // Drop the next-to-last segment.
        }

        var compact = Compose(url, segments, lastSegment, query);

        // Keep dropping segments while it is still too long.
        while (compact.Length > maxLength && segments.Count >= 1)
        {
            segments.RemoveAt(segments.Count - 1);
            compact = Compose(url, segments, lastSegment, query);
        }

        if (compact.Length >= maxLength && hardLimit)
        {
            var toRemove = display.Length - maxLength + 3;
            compact = display.Remove(display.Length - toRemove - 1, toRemove) + "...";
        }

        return compact;

        static string Compose(Uri url, List<string> segments, string lastSegment, string query)
        {
            var middle = segments.Count == 0
                ? string.Join('/', "...", lastSegment)
                : string.Join('/', string.Join('/', segments), "...", lastSegment);

            return $"{url.Scheme}://{url.Host}/{middle}{query}";
        }
    }

    /// <summary>Splits on the first occurrence of <paramref name="separator"/>.</summary>
    /// <remarks>
    /// QUIRK, preserved from upstream: when the separator is absent, <c>indexOf</c> returns -1 and the
    /// arithmetic below leaves the left half as the whole string and the right half as the string
    /// minus its first <c>separator.Length - 1</c> characters. Callers in the updater rely on the
    /// current behaviour, so this is faithful rather than fixed.
    /// </remarks>
    public static (string Left, string Right) SplitFirst(
        string s,
        string separator,
        CaseSensitivity cs = CaseSensitivity.CaseSensitive)
    {
        var index = s.IndexOf(
            separator,
            cs == CaseSensitivity.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        var left = Mid(s, 0, index);
        var right = Mid(s, index + separator.Length);

        return (left, right);
    }

    /// <summary>Splits on the first occurrence of <paramref name="separator"/>.</summary>
    /// <remarks>
    /// Unlike the string overload, this one derives the right half from the *left half's* length, so
    /// a missing separator yields the whole string and an empty remainder.
    /// </remarks>
    public static (string Left, string Right) SplitFirst(
        string s,
        char separator,
        CaseSensitivity cs = CaseSensitivity.CaseSensitive)
    {
        var index = IndexOf(s, separator, cs);
        var left = Mid(s, 0, index);
        var right = Mid(s, left.Length + 1);

        return (left, right);
    }

    /// <summary>Splits on the first match of <paramref name="regex"/>.</summary>
    public static (string Left, string Right) SplitFirst(string s, Regex regex)
    {
        var match = regex.Match(s);
        var index = match.Success ? match.Index : -1;

        var left = Mid(s, 0, index);
        var end = match.Success ? left.Length + match.Length : left.Length + 1;
        var right = Mid(s, end);

        return (left, right);
    }

    /// <summary>
    /// Inserts a <c>&lt;br&gt;</c> after a <c>&lt;/ul&gt;</c> that is immediately followed by an image,
    /// working around Qt's rich text engine collapsing the gap.
    /// </summary>
    public static string HtmlListPatch(string htmlStr)
    {
        var pos = UnorderedListEndPattern().Match(htmlStr) is { Success: true } first ? first.Index : -1;

        while (pos != -1)
        {
            pos = htmlStr.IndexOf('>', pos) + 1; // Step past the closing tag.

            var imgPos = htmlStr.IndexOf("<img ", pos, StringComparison.Ordinal);

            if (imgPos == -1)
            {
                break; // No image after the tag.
            }

            var textBetween = htmlStr[pos..imgPos].Trim();

            if (textBetween.Length == 0)
            {
                htmlStr = htmlStr.Insert(pos, "<br>");
            }

            var next = UnorderedListEndPattern().Match(htmlStr, pos);
            pos = next.Success ? next.Index : -1;
        }

        return htmlStr;
    }

    [GeneratedRegex(@"<\s*/\s*ul\s*>")]
    private static partial Regex UnorderedListEndPattern();

    /// <summary>Reads one character, yielding NUL past the end -- the C++ relies on this.</summary>
    private static char GetNextChar(string s, int location) => location < s.Length ? s[location] : '\0';

    /// <summary>Equivalent of <c>QChar::digitValue()</c>: the numeric value, or -1 if not a digit.</summary>
    private static int DigitValue(char c) => char.IsDigit(c) ? (int)char.GetNumericValue(c) : -1;

    /// <summary>Equivalent of <c>QString::localeAwareCompare()</c> for a single character.</summary>
    private static int CompareCharsLocaleAware(char left, char right)
    {
        Span<char> a = stackalloc char[1];
        Span<char> b = stackalloc char[1];
        a[0] = left;
        b[0] = right;

        return CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.None);
    }

    private static int IndexOf(string s, char value, CaseSensitivity cs)
    {
        if (cs == CaseSensitivity.CaseSensitive)
        {
            return s.IndexOf(value);
        }

        for (var i = 0; i < s.Length; i++)
        {
            if (char.ToLowerInvariant(s[i]) == char.ToLowerInvariant(value))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Replicates <c>QString::mid(position, n)</c>, including its clamping of out-of-range arguments.
    /// </summary>
    /// <remarks>
    /// The port depends on this: a negative length means "to the end", and a negative position with a
    /// negative length returns the whole string. Both cases are reachable from
    /// <see cref="SplitFirst(string, string, CaseSensitivity)"/> when the separator is missing.
    /// </remarks>
    private static string Mid(string s, int position, int length = -1)
    {
        var originalLength = s.Length;

        if (position > originalLength)
        {
            return string.Empty;
        }

        if (position < 0)
        {
            if (length < 0 || (long)length + position >= originalLength)
            {
                return s;
            }

            if ((long)length + position <= 0)
            {
                return string.Empty;
            }

            length += position;
            position = 0;
        }
        else if ((uint)length > (uint)(originalLength - position))
        {
            // Mirrors the C++ size_t cast, which makes a negative length compare as "very large".
            length = originalLength - position;
        }

        return length > 0 ? s.Substring(position, length) : string.Empty;
    }
}
