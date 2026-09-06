// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2023 flowln <flowlnlnln@gmail.com>
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * Ported from launcher/Version.{h,cpp}. Comparison semantics are intentionally
 * bug-for-bug identical to the Qt implementation -- the FlexVer test vectors in
 * tests/testdata/Version/test_vectors.txt are the contract.
 */

using System.Globalization;
using System.Text;

namespace ExtremeLauncher.Core;

/// <summary>
/// A loosely-structured version string, compared section by section.
/// Mirrors the Qt <c>Version</c> class.
/// </summary>
/// <remarks>
/// This intentionally shadows <see cref="System.Version"/> inside this namespace, matching the
/// upstream C++ name. Fully qualify <see cref="System.Version"/> in the rare files that need both.
/// </remarks>
public sealed class Version : IComparable<Version>, IEquatable<Version>
{
    private static readonly char[] Separators = ['.', '-', '+'];

    private readonly string _string;
    private readonly List<Section> _sections = [];

    public Version(string? str)
    {
        _string = str ?? string.Empty;
        Parse();
    }

    public Version() : this(string.Empty)
    {
    }

    public static Version Empty { get; } = new(string.Empty);

    public bool IsEmpty => _string.Length == 0;

    public override string ToString() => _string;

    /// <summary>
    /// Splits the raw string on "character class" changes: digit/non-digit boundaries, and
    /// separators that differ from the one the current section already started with.
    /// </summary>
    private void Parse()
    {
        _sections.Clear();

        if (_string.Length == 0)
        {
            return;
        }

        var currentSection = new StringBuilder();
        currentSection.Append(_string[0]);

        for (var i = 1; i < _string.Length; i++)
        {
            var currentChar = _string[i];

            if (IsClassChange(_string[i - 1], currentChar, currentSection))
            {
                if (currentSection.Length > 0)
                {
                    _sections.Add(new Section(currentSection.ToString()));
                }

                currentSection.Clear();
            }

            currentSection.Append(currentChar);
        }

        if (currentSection.Length > 0)
        {
            _sections.Add(new Section(currentSection.ToString()));
        }
    }

    private static bool IsClassChange(char lastChar, char currentChar, StringBuilder currentSection)
    {
        if (lastChar == '\0')
        {
            return false;
        }

        if (char.IsDigit(lastChar) != char.IsDigit(currentChar))
        {
            return true;
        }

        // A separator starts a new section unless the current section already opened with it.
        return Array.IndexOf(Separators, currentChar) >= 0
               && currentSection.Length > 0
               && currentSection[0] != currentChar;
    }

    /// <summary>
    /// Walks both section lists in lockstep and reports the first pair that differs.
    /// Sections at or after a "+appendix" section are excluded from the comparison entirely.
    /// </summary>
    /// <returns><see langword="true"/> if a differing pair was found.</returns>
    private bool TryFindFirstDifference(Version other, out Section ours, out Section theirs)
    {
        var excludeOurSections = false;
        var excludeTheirSections = false;

        var size = Math.Max(_sections.Count, other._sections.Count);

        for (var i = 0; i < size; i++)
        {
            var sec1 = i >= _sections.Count ? default : _sections[i];
            var sec2 = i >= other._sections.Count ? default : other._sections[i];

            if (sec1.IsAppendix)
            {
                excludeOurSections = true;
            }

            if (sec2.IsAppendix)
            {
                excludeTheirSections = true;
            }

            if (excludeOurSections)
            {
                sec1 = default;

                if (sec2.IsNull)
                {
                    break;
                }
            }

            if (excludeTheirSections)
            {
                sec2 = default;

                if (sec1.IsNull)
                {
                    break;
                }
            }

            if (!sec1.Equals(sec2))
            {
                ours = sec1;
                theirs = sec2;
                return true;
            }
        }

        ours = default;
        theirs = default;
        return false;
    }

    public bool Equals(Version? other)
    {
        if (other is null)
        {
            return false;
        }

        return ReferenceEquals(this, other) || !TryFindFirstDifference(other, out _, out _);
    }

    public override bool Equals(object? obj) => obj is Version other && Equals(other);

    public override int GetHashCode()
    {
        // Must agree with Equals: sections from the first appendix onward are not compared,
        // so they must not contribute to the hash either.
        var hash = new HashCode();

        foreach (var section in _sections)
        {
            if (section.IsAppendix)
            {
                break;
            }

            hash.Add(section.NumPart);
            hash.Add(section.StringPart, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public int CompareTo(Version? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (!TryFindFirstDifference(other, out var ours, out var theirs))
        {
            return 0;
        }

        return Section.LessThan(ours, theirs) ? -1 : 1;
    }

    public static bool operator <(Version? left, Version? right)
    {
        var l = left ?? Empty;
        var r = right ?? Empty;

        return l.TryFindFirstDifference(r, out var ours, out var theirs) && Section.LessThan(ours, theirs);
    }

    public static bool operator >(Version? left, Version? right) => !(left <= right);

    public static bool operator <=(Version? left, Version? right) => left < right || left == right;

    public static bool operator >=(Version? left, Version? right) => !(left < right);

    public static bool operator ==(Version? left, Version? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return left.Equals(right);
    }

    public static bool operator !=(Version? left, Version? right) => !(left == right);

    /// <summary>
    /// One chunk of a version string, split into a leading numeric part and a trailing string part.
    /// </summary>
    /// <remarks>
    /// The flag is stored inverted (<c>_hasValue</c> rather than the C++ <c>m_isNull</c>) so that
    /// <c>default(Section)</c> is the null section, matching the C++ default constructor.
    /// </remarks>
    private readonly struct Section : IEquatable<Section>
    {
        private readonly bool _hasValue;
        private readonly string? _stringPart;
        private readonly string? _fullString;

        public Section(string fullString)
        {
            _fullString = fullString;
            _hasValue = false;
            NumPart = 0;
            _stringPart = null;

            var cutoff = fullString.Length;

            for (var i = 0; i < fullString.Length; i++)
            {
                if (!char.IsDigit(fullString[i]))
                {
                    cutoff = i;
                    break;
                }
            }

            var numPart = fullString.AsSpan(0, cutoff);

            if (!numPart.IsEmpty)
            {
                _hasValue = true;
                NumPart = ToInt(numPart);
            }

            var stringPart = fullString.AsSpan(cutoff);

            if (!stringPart.IsEmpty)
            {
                _hasValue = true;
                _stringPart = stringPart.ToString();
            }
        }

        public bool IsNull => !_hasValue;

        public int NumPart { get; }

        public string StringPart => _stringPart ?? string.Empty;

        public string FullString => _fullString ?? string.Empty;

        public bool IsAppendix => StringPart.StartsWith('+');

        public bool IsPreRelease => StringPart.StartsWith('-') && StringPart.Length > 1;

        /// <summary>QString::toInt() yields 0 on overflow or malformed input; match that.</summary>
        private static int ToInt(ReadOnlySpan<char> value)
            => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        public bool Equals(Section other)
        {
            if (IsNull != other.IsNull)
            {
                return false;
            }

            if (IsNull)
            {
                return true;
            }

            return NumPart == other.NumPart && string.Equals(StringPart, other.StringPart, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is Section other && Equals(other);

        public override int GetHashCode()
            => IsNull ? 0 : HashCode.Combine(NumPart, StringComparer.Ordinal.GetHashCode(StringPart));

        public static bool LessThan(Section left, Section right)
        {
            // Whether a present section sorts *below* an absent one, e.g. "1.0-rc1" < "1.0".
            static bool UnequalIsLess(Section nonNull)
            {
                if (nonNull.StringPart.Length == 0)
                {
                    return nonNull.NumPart == 0;
                }

                return !string.Equals(nonNull.StringPart, ".", StringComparison.Ordinal) && nonNull.IsPreRelease;
            }

            if (!left.IsNull && right.IsNull)
            {
                return UnequalIsLess(left);
            }

            if (left.IsNull && !right.IsNull)
            {
                return !UnequalIsLess(right);
            }

            if (!left.IsNull && !right.IsNull)
            {
                if (left.NumPart < right.NumPart)
                {
                    return true;
                }

                if (left.NumPart == right.NumPart
                    && string.CompareOrdinal(left.StringPart, right.StringPart) < 0)
                {
                    return true;
                }

                if (left.StringPart.Length != 0 && right.StringPart.Length == 0)
                {
                    return false;
                }

                return left.StringPart.Length == 0 && right.StringPart.Length != 0;
            }

            return string.CompareOrdinal(left.FullString, right.FullString) < 0;
        }

        public override string ToString() => FullString;
    }
}
