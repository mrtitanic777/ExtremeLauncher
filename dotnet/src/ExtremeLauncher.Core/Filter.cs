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
 * Ported from launcher/Filter.{h,cpp}.
 *
 * NARROWING A VERSION LIST. The version pickers apply one of these to decide which entries a user is
 * shown -- "only releases", "only versions this loader supports", "only 1.20.x".
 *
 * FIVE OF THEM, AND THEY DIFFER IN WHAT "NOTHING" MEANS -- which is the only interesting thing about
 * the whole family. ExactFilter is strict. ExactIfPresentFilter accepts an empty VALUE. ExactListFilter
 * accepts anything when its LIST is empty. ContainsFilter accepts everything for an empty pattern, but
 * only because that is what a substring test does, not by design. Picking the wrong one shows the user
 * an empty list with no explanation, or a filter that never filters.
 */

using System.Text.RegularExpressions;

namespace ExtremeLauncher.Core;

/// <summary>Decides whether one value survives a narrowing.</summary>
public interface IFilter
{
    bool Accepts(string value);
}

/// <summary>Accepts one exact value, and nothing else.</summary>
public sealed class ExactFilter : IFilter
{
    private readonly string _pattern;

    public ExactFilter(string pattern) => _pattern = pattern;

    public bool Accepts(string value) => value == _pattern;
}

/// <summary>Accepts one exact value, and also accepts a value that is empty.</summary>
/// <remarks>
/// The distinction from <see cref="ExactFilter"/> is the entire reason both exist: here a missing
/// field is not evidence of a mismatch, so an entry with nothing to compare survives. Used where the
/// value being filtered on is optional in the data.
/// </remarks>
public sealed class ExactIfPresentFilter : IFilter
{
    private readonly string _pattern;

    public ExactIfPresentFilter(string pattern) => _pattern = pattern;

    public bool Accepts(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Length == 0 || value == _pattern;
    }
}

/// <summary>Accepts anything the pattern matches, or anything it does not when inverted.</summary>
/// <remarks>
/// UNANCHORED, like QRegularExpression::match: the pattern may match anywhere in the value. A filter
/// meant to accept only "1.20.1" has to say so with anchors, and upstream's callers do.
/// </remarks>
public sealed partial class RegexpFilter : IFilter
{
    private readonly Regex _pattern;
    private readonly bool _invert;

    public RegexpFilter(string pattern, bool invert = false)
    {
        // A timeout, because these patterns come from a settings file the user can edit.
        _pattern = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        _invert = invert;
    }

    public bool Accepts(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var matched = _pattern.IsMatch(value);

        return _invert ? !matched : matched;
    }
}

/// <summary>Accepts any of a set of exact values, or anything when the set is empty.</summary>
public sealed class ExactListFilter : IFilter
{
    private readonly IReadOnlyCollection<string> _pattern;

    public ExactListFilter(IReadOnlyCollection<string>? pattern = null) => _pattern = pattern ?? [];

    public bool Accepts(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // An empty LIST means no constraint; an empty value is not special, unlike ExactIfPresentFilter.
        return _pattern.Count == 0 || _pattern.Contains(value);
    }
}

/// <summary>Accepts any value containing the pattern.</summary>
/// <remarks>
/// An empty pattern accepts everything, but only as a consequence of what a substring test does --
/// upstream writes a bare <c>value.contains(pattern)</c> with no guard, and this is the same. Worth
/// stating so it is not mistaken for a deliberate "unset means no constraint" like the other filters.
/// </remarks>
public sealed class ContainsFilter : IFilter
{
    private readonly string _pattern;

    public ContainsFilter(string pattern) => _pattern = pattern;

    public bool Accepts(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Contains(_pattern, StringComparison.Ordinal);
    }
}
