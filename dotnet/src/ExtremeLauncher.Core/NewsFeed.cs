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
 * Ported from launcher/news/NewsEntry.cpp and the parsing half of launcher/news/NewsChecker.cpp.
 *
 * THE LAUNCHER'S NEWS FEED. Upstream calls it an RSS feed everywhere -- the job is even named "News
 * RSS Feed" -- but it reads <entry>, <title>, <content> and <id>, which is ATOM. The fork's feed at
 * BuildConfig.NewsRssUrl is Atom too, so the name is the only thing that is wrong.
 *
 * NAMESPACE-BLIND ON PURPOSE. Qt's elementsByTagName matches on the local name and ignores the
 * namespace, so upstream finds <entry> in http://www.w3.org/2005/Atom without ever declaring it.
 * XDocument is not namespace-blind, so matching here is done on LocalName -- otherwise a feed served
 * with a different (or no) namespace declaration would parse to zero entries, which looks exactly
 * like "no news available" and nothing would say why.
 */

using System.Xml;
using System.Xml.Linq;

namespace ExtremeLauncher.Core;

/// <summary>One article out of the feed.</summary>
public sealed record NewsEntry
{
    /// <remarks>
    /// Upstream's defaults for a malformed entry, kept because they are what the window shows: an
    /// entry with no title still gets a row rather than an empty one.
    /// </remarks>
    public string Title { get; init; } = "Untitled";

    public string Content { get; init; } = "No content.";

    /// <summary>
    /// Where "read more" goes.
    /// </summary>
    /// <remarks>
    /// FROM &lt;id&gt;, NOT &lt;link&gt;, which is upstream's choice and looks like a mistake until you
    /// check the feed: Atom's id is only required to be a URI, not a URL you can open. In this fork's
    /// feed the two are the same string, so it works. <see cref="AlternateLink"/> carries the real
    /// link for a feed where they differ.
    /// </remarks>
    public string Link { get; init; } = string.Empty;

    /// <summary>The href of &lt;link rel="alternate"&gt;, which is where a feed reader would go.</summary>
    public string AlternateLink { get; init; } = string.Empty;

    /// <summary>When the entry says it was updated, or null if it did not say or said it badly.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>The link to actually open: the real one when there is one, upstream's otherwise.</summary>
    public string BestLink => AlternateLink.Length != 0 ? AlternateLink : Link;
}

/// <summary>What a parsed feed carries.</summary>
public sealed record NewsFeedContents
{
    public IReadOnlyList<NewsEntry> Entries { get; init; } = [];

    /// <summary>
    /// The &lt;serverlistentry&gt; values, which are not an Atom thing at all.
    /// </summary>
    /// <remarks>
    /// A FORK-LOCAL EXTENSION. Upstream's NewsChecker collects these into the global `serverList`,
    /// and CreateGameFolders writes them into every instance's servers.dat at launch -- so the news
    /// feed can put entries in the player's multiplayer list. Parsed here because parity is the goal;
    /// nothing in this port consumes it yet, and when something does it needs to say so out loud.
    /// </remarks>
    public IReadOnlyList<string> ServerList { get; init; } = [];
}

public static class NewsFeed
{
    /// <summary>Parses a feed document. Throws <see cref="XmlException"/> on anything unparseable.</summary>
    /// <remarks>
    /// Upstream reports the parse error with its line and column and shows it in place of the news.
    /// The exception carries both, so the caller can do the same rather than saying "failed".
    /// </remarks>
    public static NewsFeedContents Parse(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);

        var entries = document
            .Descendants()
            .Where(e => e.Name.LocalName == "entry")
            .Select(ParseEntry)
            .ToList();

        var servers = document
            .Descendants()
            .Where(e => e.Name.LocalName == "serverlistentry")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length != 0)
            .ToList();

        return new NewsFeedContents { Entries = entries, ServerList = servers };
    }

    private static NewsEntry ParseEntry(XElement element)
    {
        /*
         * Upstream's childValue takes the FIRST match of elementsByTagName, which searches the whole
         * subtree rather than only direct children. Kept, because an Atom <entry> can legitimately
         * carry nested markup and the first title is still the entry's own.
         */
        var title = Child(element, "title");
        var content = Child(element, "content");
        var id = Child(element, "id");

        return new NewsEntry
        {
            Title = title.Length != 0 ? title : "Untitled",
            Content = content.Length != 0 ? content : "No content.",
            Link = id,
            AlternateLink = AlternateLink(element),
            Updated = ParseTimestamp(Child(element, "updated")),
        };
    }

    private static string Child(XElement element, string localName)
        => element.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? string.Empty;

    private static string AlternateLink(XElement element)
    {
        var links = element.Descendants().Where(e => e.Name.LocalName == "link").ToList();

        // Atom's default rel is "alternate", so a link with no rel at all is the one you want.
        var link = links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate");

        return (string?)link?.Attribute("href") ?? string.Empty;
    }

    private static DateTimeOffset? ParseTimestamp(string value)
        => DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
}
