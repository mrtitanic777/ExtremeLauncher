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
 * Reading the news feed.
 *
 * ASSERTED AGAINST A REAL CAPTURE of the fork's live feed rather than against hand-written XML,
 * because the interesting parts -- the Atom namespace, the fork's own <serverlistentry> extension,
 * HTML escaped twice over -- are all things I would have got wrong if I had invented the fixture.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class NewsFeedTests
{
    private static string RealFeed => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "news-feed.xml"));

    [Fact]
    public void TheRealFeedParses()
    {
        var feed = NewsFeed.Parse(RealFeed);

        Assert.NotEmpty(feed.Entries);
        Assert.All(feed.Entries, e => Assert.NotEqual(string.Empty, e.Title));
    }

    [Fact]
    public void TheNamespaceIsIgnoredTheWayQtIgnoresIt()
    {
        /*
         * THE BUG THIS PORT WOULD OTHERWISE HAVE. The feed's root is
         * <feed xmlns="http://www.w3.org/2005/Atom">, so every element is in that namespace and
         * XDocument's Descendants("entry") finds NOTHING. Qt's elementsByTagName matches local names
         * and does not care, which is why upstream never had to think about it.
         *
         * Zero entries looks exactly like "no news available", so this would have been a silent
         * empty window rather than a crash.
         */
        var feed = NewsFeed.Parse(RealFeed);

        Assert.Equal(23, feed.Entries.Count);
    }

    [Fact]
    public void EntriesComeOutInFeedOrder()
    {
        // The window shows entries[0] on the toolbar, so the order is load-bearing.
        var feed = NewsFeed.Parse(RealFeed);

        Assert.StartsWith("ExtremeLauncher 5.1.0.7", feed.Entries[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinkComesFromTheIdBecauseThatIsWhatUpstreamReads()
    {
        // Upstream's NewsEntry::fromXmlElement reads <id>, not <link>. In this feed they agree.
        var first = NewsFeed.Parse(RealFeed).Entries[0];

        Assert.StartsWith("https://extremelauncher.net/news/", first.Link, StringComparison.Ordinal);
        Assert.Equal(first.Link, first.AlternateLink);
    }

    [Fact]
    public void TheForksOwnServerEntriesAreRead()
    {
        /*
         * NOT AN ATOM ELEMENT AT ALL -- <serverlistentry> is this fork's extension, and upstream's
         * NewsChecker collects it into a global that CreateGameFolders writes into every instance's
         * servers.dat. So the news feed can put servers in the player's multiplayer list. Parsed for
         * parity; nothing consumes it yet.
         */
        var feed = NewsFeed.Parse(RealFeed);

        Assert.Equal(
            ["extremelauncher.extremecraft.net", "extremelauncher.strongcraft.org"],
            feed.ServerList);
    }

    [Fact]
    public void TheTimestampIsRead()
    {
        var first = NewsFeed.Parse(RealFeed).Entries[0];

        Assert.NotNull(first.Updated);
        Assert.Equal(2026, first.Updated!.Value.Year);
    }

    [Fact]
    public void AnEntryMissingEverythingGetsUpstreamsPlaceholders()
    {
        // Upstream's defaults, kept because they are what the window shows: an entry with no title
        // still gets a row rather than an invisible one.
        var feed = NewsFeed.Parse("<feed><entry></entry></feed>");

        var entry = Assert.Single(feed.Entries);

        Assert.Equal("Untitled", entry.Title);
        Assert.Equal("No content.", entry.Content);
        Assert.Equal(string.Empty, entry.Link);
        Assert.Null(entry.Updated);
    }

    [Fact]
    public void AFeedWithNoEntriesIsNotAnError()
    {
        // "No news available" is a state the window has; a throw here would show an error instead.
        var feed = NewsFeed.Parse("<feed><title>Nothing yet</title></feed>");

        Assert.Empty(feed.Entries);
        Assert.Empty(feed.ServerList);
    }

    [Fact]
    public void BrokenXmlThrowsWithAPlaceToLook()
    {
        /*
         * Upstream reports the parse error WITH ITS LINE AND COLUMN and shows that instead of the
         * news. A bare "failed to load news" would leave nobody able to tell a server outage from a
         * malformed feed.
         */
        var error = Assert.Throws<System.Xml.XmlException>(() => NewsFeed.Parse("<feed><entry></feed>"));

        Assert.True(error.LineNumber > 0);
    }

    [Fact]
    public void AnUnnamespacedFeedParsesToo()
    {
        // The other half of being namespace-blind: a feed served without the declaration still works.
        var feed = NewsFeed.Parse("<feed><entry><title>Hi</title><id>https://example.invalid/1</id></entry></feed>");

        Assert.Equal("Hi", Assert.Single(feed.Entries).Title);
    }
}
