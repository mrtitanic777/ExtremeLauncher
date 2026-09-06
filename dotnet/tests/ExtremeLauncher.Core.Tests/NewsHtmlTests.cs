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
 * Turning a news entry's HTML into something a window can lay out.
 *
 * THE SAFETY PROPERTY MATTERS MORE THAN THE FIDELITY ONE. Losing a tag's formatting is a bad day;
 * showing a user raw angle brackets, or dropping the body of an article because of one stray
 * character, is a broken launcher. Several tests below are about the second kind.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class NewsHtmlTests
{
    private static string RealFeed => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "news-feed.xml"));

    [Fact]
    public void ParagraphsBecomeBlocks()
    {
        var blocks = NewsHtml.ToBlocks("<p>First.</p><p>Second.</p>");

        Assert.Equal(["First.", "Second."], blocks.Select(b => b.Text));
        Assert.All(blocks, b => Assert.Equal(NewsBlockKind.Paragraph, b.Kind));
    }

    [Fact]
    public void BoldSurvivesAsAStyleRatherThanAsMarkup()
    {
        // The whole reason this exists: the feed's headings are <strong> inside a <p>, so flattening
        // to text would lose every heading in it.
        var block = Assert.Single(NewsHtml.ToBlocks("<p>Plain <strong>bold</strong> plain</p>"));

        Assert.Equal(3, block.Runs.Count);
        Assert.False(block.Runs[0].Bold);
        Assert.True(block.Runs[1].Bold);
        Assert.Equal("bold", block.Runs[1].Text);
        Assert.False(block.Runs[2].Bold);
    }

    [Fact]
    public void TheSpacesAroundAStyledRunAreKept()
    {
        // Off-by-one here reads as "Plainboldplain", which is the kind of thing that only shows up
        // when somebody looks at the window.
        var block = Assert.Single(NewsHtml.ToBlocks("<p>Plain <strong>bold</strong> plain</p>"));

        Assert.Equal("Plain bold plain", block.Text);
    }

    [Fact]
    public void NestedStylesComeBackOutOneLayerAtATime()
    {
        // Why the parser counts depth rather than holding a flag.
        var block = Assert.Single(NewsHtml.ToBlocks("<p><strong>a<em>b</em>c</strong></p>"));

        Assert.All(block.Runs, r => Assert.True(r.Bold));
        Assert.Equal([false, true, false], block.Runs.Select(r => r.Italic));
    }

    [Fact]
    public void BulletsAndNumbersAreToldApart()
    {
        var blocks = NewsHtml.ToBlocks("<ul><li>one</li><li>two</li></ul><ol><li>first</li><li>second</li></ol>");

        Assert.Equal(
            [NewsBlockKind.Bullet, NewsBlockKind.Bullet, NewsBlockKind.Numbered, NewsBlockKind.Numbered],
            blocks.Select(b => b.Kind));

        Assert.Equal(["•", "•", "1.", "2."], blocks.Select(b => b.Marker));
    }

    [Fact]
    public void EachOrderedListStartsFromOneAgain()
    {
        // Two "how to use it" lists in one post is exactly what the feed has.
        var blocks = NewsHtml.ToBlocks("<ol><li>a</li></ol><p>then</p><ol><li>b</li><li>c</li></ol>");

        Assert.Equal(["1.", "", "1.", "2."], blocks.Select(b => b.Marker));
    }

    [Fact]
    public void EntitiesAreDecodedOnce()
    {
        /*
         * DECODED ONCE, NOT TWICE. The content arrives escaped twice over -- "&amp;nbsp;" in the XML
         * becomes "&nbsp;" after the XML parse -- and a second pass over the decoded text would turn
         * a post that WRITES ABOUT "&lt;p&gt;" into an actual tag and swallow it.
         */
        var block = Assert.Single(NewsHtml.ToBlocks("<p>a &amp; b &rsquo;c&rsquo; &ndash; d</p>"));

        Assert.Equal("a & b ’c’ – d", block.Text);
    }

    [Fact]
    public void MarkupWrittenAboutRatherThanUsedIsShownNotEaten()
    {
        // The other half of decoding once. A post explaining how to write a tag must show the tag.
        var block = Assert.Single(NewsHtml.ToBlocks("<p>write &lt;strong&gt; for bold</p>"));

        Assert.Equal("write <strong> for bold", block.Text);
    }

    [Fact]
    public void SpacerParagraphsDoNotBecomeEmptyRows()
    {
        /*
         * The feed uses "<p>&nbsp;</p>" as a visual gap. A non-breaking space is not whitespace to
         * string.IsNullOrWhiteSpace once decoded to U+00A0, so without converting it these render as
         * blank rows with no text and no explanation.
         */
        var blocks = NewsHtml.ToBlocks("<p>a</p><p>&nbsp;</p><p>b</p>");

        Assert.Equal(["a", "b"], blocks.Select(b => b.Text));
    }

    [Fact]
    public void AnUnknownTagLosesItsFormattingAndKeepsItsText()
    {
        // Never show angle brackets to a user; never drop the words either.
        var block = Assert.Single(NewsHtml.ToBlocks("<p>before <mark class=\"x\">middle</mark> after</p>"));

        Assert.Equal("before middle after", block.Text);
    }

    [Fact]
    public void AStrayLessThanDoesNotEatTheRestOfThePost()
    {
        /*
         * The failure mode worth guarding: an unescaped "<" in a hand-written post. Treating the
         * remainder as an unterminated tag would silently drop the whole article body.
         */
        var blocks = NewsHtml.ToBlocks("<p>5 < 6 and that is the end</p>");

        Assert.Contains("that is the end", string.Concat(blocks.Select(b => b.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void AStrayLessThanStillLetsRealTagsWork()
    {
        // The lenient rule must not become "nothing after a stray < is markup any more".
        var blocks = NewsHtml.ToBlocks("<p>5 < 6</p><p><strong>still bold</strong></p>");

        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[1].Runs[0].Bold);
    }

    [Fact]
    public void ACommentEndsWhereCommentsEnd()
    {
        // A comment can contain ">" -- stopping at the first one would spill its innards onto screen.
        var block = Assert.Single(NewsHtml.ToBlocks("<p>a<!-- 1 > 0, hidden -->b</p>"));

        Assert.Equal("ab", block.Text);
    }

    [Fact]
    public void LinksKeepTheirDestination()
    {
        // Not in the feed today; the next post might have one, and a link with no href is a dead end.
        var block = Assert.Single(NewsHtml.ToBlocks("<p>see <a href=\"https://example.invalid/x\">this</a></p>"));

        var link = Assert.Single(block.Runs, r => r.IsLink);

        Assert.Equal("this", link.Text);
        Assert.Equal("https://example.invalid/x", link.Url);
    }

    [Fact]
    public void HeadingsAreMarkedAsHeadings()
    {
        var blocks = NewsHtml.ToBlocks("<h2>Title</h2><p>body</p>");

        Assert.Equal(NewsBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(NewsBlockKind.Paragraph, blocks[1].Kind);
    }

    [Fact]
    public void NothingInMeansNothingOut()
    {
        Assert.Empty(NewsHtml.ToBlocks(null));
        Assert.Empty(NewsHtml.ToBlocks(string.Empty));
        Assert.Empty(NewsHtml.ToBlocks("   "));
        Assert.Empty(NewsHtml.ToBlocks("<p></p><p>&nbsp;</p>"));
    }

    [Fact]
    public void EveryRealEntryProducesSomethingToRead()
    {
        /*
         * THE TEST THAT COUNTS. Twenty-three real articles through the real parser: every one has to
         * produce blocks, none may come out empty, and NO BLOCK MAY CONTAIN A "<" -- which is the
         * single symptom that would tell a user this was written by somebody who did not check.
         */
        var entries = NewsFeed.Parse(RealFeed).Entries;

        Assert.Equal(23, entries.Count);

        foreach (var entry in entries)
        {
            var blocks = NewsHtml.ToBlocks(entry.Content);

            Assert.True(blocks.Count > 0, $"no content came out of \"{entry.Title}\"");
            Assert.All(blocks, b => Assert.NotEqual(string.Empty, b.Text.Trim()));
            Assert.All(blocks, b => Assert.DoesNotContain('<', b.Text));

            // And no entity left undecoded. A bare "&" is fine -- "Fabric & Quilt" is a real
            // sentence -- but "&nbsp;" reaching the screen means a decode pass was missed.
            Assert.All(
                blocks,
                b => Assert.DoesNotMatch(@"&[a-zA-Z]{2,10};|&#\d+;", b.Text));
        }
    }

    [Fact]
    public void TheRealFeedActuallyExercisesEveryBlockKind()
    {
        /*
         * A guard on the guard above: if the feed only ever had flat paragraphs, that test would pass
         * while proving almost nothing. This asserts the fixture is worth having.
         */
        var kinds = NewsFeed.Parse(RealFeed).Entries
            .SelectMany(e => NewsHtml.ToBlocks(e.Content))
            .Select(b => b.Kind)
            .ToHashSet();

        Assert.Contains(NewsBlockKind.Paragraph, kinds);
        Assert.Contains(NewsBlockKind.Bullet, kinds);
        Assert.Contains(NewsBlockKind.Numbered, kinds);

        var runs = NewsFeed.Parse(RealFeed).Entries
            .SelectMany(e => NewsHtml.ToBlocks(e.Content))
            .SelectMany(b => b.Runs)
            .ToList();

        Assert.Contains(runs, r => r.Bold);
        Assert.Contains(runs, r => r.Italic);
    }

    [Fact]
    public void PlainTextKeepsTheListMarkers()
    {
        // For a tooltip or a log line, where there is nothing to style with.
        var text = NewsHtml.ToPlainText("<p>Head</p><ul><li>one</li></ul>");

        Assert.Contains("Head", text, StringComparison.Ordinal);
        Assert.Contains("• one", text, StringComparison.Ordinal);
    }
}
