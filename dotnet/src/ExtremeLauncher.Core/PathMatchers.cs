// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// SPDX-License-Identifier: GPL-3.0-only
/*
 * Ported from launcher/pathmatcher/{IPathMatcher,SimplePrefixMatcher,RegexpMatcher,MultiMatcher}.h.
 *
 * NOT PORTED: FSTreeMatcher, which is built on SeparatorPrefixTree -- that container is its own port
 * and has no consumer yet among the classes here.
 *
 * Matchers are always consulted as `matcher.Matches(relativePath) != whitelist`, so the same matcher
 * serves as either a blacklist or a whitelist depending on the caller's flag.
 */

using System.Text.RegularExpressions;

namespace ExtremeLauncher.Core;

public interface IPathMatcher
{
    bool Matches(string path);
}

/// <summary>Matches an exact path, or any path under it when the prefix ends in '/'.</summary>
public sealed class SimplePrefixMatcher : IPathMatcher
{
    private readonly string _prefix;
    private readonly bool _isPrefix;

    public SimplePrefixMatcher(string prefix)
    {
        _prefix = prefix;
        _isPrefix = prefix.EndsWith('/');
    }

    public bool Matches(string path)
        => _isPrefix
            ? path.StartsWith(_prefix, StringComparison.Ordinal)
            : string.Equals(path, _prefix, StringComparison.Ordinal);
}

/// <summary>Matches a regular expression against the path, or against just the filename.</summary>
/// <remarks>
/// A pattern containing no '/' is applied to the filename only, matching upstream.
///
/// UPSTREAM BUG #16, NO LONGER PRESERVED. Upstream's <c>caseSensitive(bool)</c> sets
/// <c>CaseInsensitiveOption</c> when passed <see langword="true"/> -- inverted relative to its name.
/// This was first ported as a deliberate quirk, on the reasoning that call sites must have been
/// written against the behaviour rather than the name. That reasoning did not survive finding the only
/// call site: <c>InstanceCopyTask</c> passes <see langword="false"/> while building a filter that
/// excludes "saves" and "mods" from a copy, and on Windows and macOS those folders are as likely to be
/// spelled "Saves". It wanted case-insensitive matching and upstream gives it the opposite. See
/// <see cref="CaseSensitive"/>.
/// </remarks>
public sealed class RegexpMatcher : IPathMatcher
{
    private readonly string _pattern;
    private readonly bool _onlyFilenamePart;

    private Regex _regex;

    public RegexpMatcher(string pattern)
    {
        _pattern = pattern;
        _onlyFilenamePart = !pattern.Contains('/');
        _regex = new Regex(pattern, RegexOptions.None);
    }

    /// <summary>Whether the pattern distinguishes case.</summary>
    /// <remarks>
    /// UPSTREAM BUG #16, fixed here: its <c>caseSensitive(true)</c> sets CaseInsensitiveOption and
    /// <c>caseSensitive(false)</c> sets NoPatternOption -- the method does the exact opposite of its
    /// name. The one caller, InstanceCopyTask, passes <c>false</c> meaning "match case-insensitively"
    /// and therefore gets case-SENSITIVE matching, so a folder named "Saves" is not excluded from a
    /// copy on Windows or macOS even though the user unticked saves. The name and the call site agree
    /// on the intent; only the implementation disagreed.
    /// </remarks>
    public RegexpMatcher CaseSensitive(bool caseSensitive = true)
    {
        _regex = new Regex(_pattern, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);

        return this;
    }

    public bool Matches(string path)
    {
        if (_onlyFilenamePart)
        {
            var slash = path.LastIndexOf('/');

            if (slash != -1)
            {
                return _regex.IsMatch(path[(slash + 1)..]);
            }
        }

        return _regex.IsMatch(path);
    }
}

/// <summary>Matches if any contained matcher matches.</summary>
public sealed class MultiMatcher : IPathMatcher
{
    private readonly List<IPathMatcher> _matchers = [];

    public MultiMatcher Add(IPathMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        _matchers.Add(matcher);
        return this;
    }

    public bool Matches(string path) => _matchers.Any(matcher => matcher.Matches(path));
}
