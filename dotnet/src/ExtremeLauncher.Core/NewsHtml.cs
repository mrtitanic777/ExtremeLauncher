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
 * No upstream counterpart -- this is what the port needs INSTEAD of one.
 *
 * A news entry's <content> is HTML. Upstream hands it to a QTextBrowser, which renders a useful
 * subset of HTML for free. AVALONIA HAS NO SUCH CONTROL, so the choice is between showing the markup
 * raw (unreadable), stripping it to flat text (losing every heading and list in a feed that is mostly
 * headings and lists), or turning it into something the window can lay out. This does the third.
 *
 * BUILT AGAINST THE FEED THAT EXISTS, not against HTML in general. Every one of the fork's 23 current
 * entries uses only:
 *
 *     p (128)   li (29)   strong (28)   ol (4)   ul (3)   em (2)
 *
 * and the entities &rsquo; &ndash; &nbsp; &amp;. Anchors, images, tables and <br> do not appear once.
 * So those six are handled properly, a few obvious neighbours (a, br, h1-h3, i, b) are handled because
 * the next post might use them, and ANYTHING ELSE IS DROPPED RATHER THAN SHOWN -- an unknown tag
 * appearing as literal text is worse than an unknown tag disappearing.
 *
 * This is deliberately not an HTML parser. It does not need to be: it needs to fail safely on markup
 * it does not know, and the failure mode is losing formatting, never showing angle brackets to a user.
 */

using System.Net;
using System.Text;

namespace ExtremeLauncher.Core;

public enum NewsBlockKind
{
    Paragraph,
    Heading,
    Bullet,
    Numbered,
}

/// <summary>A stretch of text with one style, inside a block.</summary>
public sealed record NewsRun(string Text, bool Bold = false, bool Italic = false, string Url = "")
{
    public bool IsLink => Url.Length != 0;
}

/// <summary>A paragraph, heading or list item.</summary>
public sealed record NewsBlock(NewsBlockKind Kind, IReadOnlyList<NewsRun> Runs, string Marker = "")
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

public static class NewsHtml
{
    /// <summary>Turns an entry's HTML into blocks a window can lay out.</summary>
    public static IReadOnlyList<NewsBlock> ToBlocks(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var state = new Parser();

        state.Run(html);

        return state.Blocks;
    }

    /// <summary>The same content as flat text, for a tooltip or a log line.</summary>
    public static string ToPlainText(string? html)
        => string.Join(
            Environment.NewLine,
            ToBlocks(html).Select(b => b.Marker.Length != 0 ? $"{b.Marker} {b.Text}" : b.Text));

    private sealed class Parser
    {
        private readonly List<NewsBlock> _blocks = [];
        private readonly List<NewsRun> _runs = [];
        private readonly StringBuilder _text = new();

        // A stack rather than a flag: <strong><em>x</em></strong> has to come back out bold.
        private readonly Stack<string> _lists = [];
        private readonly Stack<int> _counters = [];

        private int _bold;
        private int _italic;
        private string _url = string.Empty;

        private NewsBlockKind _kind = NewsBlockKind.Paragraph;
        private string _marker = string.Empty;

        public IReadOnlyList<NewsBlock> Blocks => _blocks;

        public void Run(string html)
        {
            var index = 0;

            while (index < html.Length)
            {
                var open = html.IndexOf('<', index);

                if (open < 0)
                {
                    Append(html[index..]);

                    break;
                }

                Append(html[index..open]);

                /*
                 * A "<" ONLY STARTS A TAG WHEN A NAME FOLLOWS IT, which is the rule browsers use and
                 * the reason "5 < 6" survives. Without it the scan runs to the next ">" -- the
                 * closing </p> -- and swallows the entire rest of the paragraph, which is a whole
                 * article body lost to one unescaped character in a hand-written post.
                 */
                var next = open + 1 < html.Length ? html[open + 1] : '\0';

                if (!char.IsAsciiLetter(next) && next is not ('/' or '!'))
                {
                    Append("<");

                    index = open + 1;

                    continue;
                }

                if (html.AsSpan(open).StartsWith("<!--"))
                {
                    // A comment ends at "-->", not at the first ">", which one may well contain.
                    var endComment = html.IndexOf("-->", open, StringComparison.Ordinal);

                    index = endComment < 0 ? html.Length : endComment + 3;

                    continue;
                }

                var close = html.IndexOf('>', open);

                if (close < 0)
                {
                    // A tag that never closes. Its text is worth more than its markup.
                    Append(html[open..]);

                    break;
                }

                Tag(html[(open + 1)..close]);

                index = close + 1;
            }

            Flush();
        }

        private void Tag(string raw)
        {
            var closing = raw.StartsWith('/');
            var body = closing ? raw[1..] : raw;

            var space = body.IndexOfAny([' ', '\t', '\r', '\n', '/']);
            var name = (space < 0 ? body : body[..space]).ToLowerInvariant();

            switch (name)
            {
                case "p":
                case "div":
                    // A block boundary either way: the open ends whatever came before it.
                    Flush();
                    _kind = NewsBlockKind.Paragraph;
                    _marker = string.Empty;

                    break;

                case "h1":
                case "h2":
                case "h3":
                case "h4":
                    Flush();
                    _kind = closing ? NewsBlockKind.Paragraph : NewsBlockKind.Heading;
                    _marker = string.Empty;

                    break;

                case "br":
                    // A line break inside a paragraph becomes a paragraph of its own, which is the
                    // closest thing this layout has and reads the same.
                    Flush();

                    break;

                case "ul":
                case "ol":
                    Flush();

                    if (closing)
                    {
                        if (_lists.Count != 0)
                        {
                            _lists.Pop();
                            _counters.Pop();
                        }
                    }
                    else
                    {
                        _lists.Push(name);
                        _counters.Push(0);
                    }

                    _kind = NewsBlockKind.Paragraph;
                    _marker = string.Empty;

                    break;

                case "li":
                    Flush();

                    if (closing)
                    {
                        _kind = NewsBlockKind.Paragraph;
                        _marker = string.Empty;

                        break;
                    }

                    if (_lists.Count != 0 && _lists.Peek() == "ol")
                    {
                        var n = _counters.Pop() + 1;

                        _counters.Push(n);

                        _kind = NewsBlockKind.Numbered;
                        _marker = $"{n}.";
                    }
                    else
                    {
                        // A stray <li> with no list around it still reads as a bullet, which is what
                        // whoever wrote it meant.
                        _kind = NewsBlockKind.Bullet;
                        _marker = "•";
                    }

                    break;

                case "strong":
                case "b":
                    Style(ref _bold, closing);

                    break;

                case "em":
                case "i":
                    Style(ref _italic, closing);

                    break;

                case "a":
                    EndRun();

                    _url = closing ? string.Empty : Attribute(body, "href");

                    break;

                default:
                    // Unknown: dropped, tag and all. Its text still comes through.
                    break;
            }
        }

        private void Style(ref int depth, bool closing)
        {
            EndRun();

            depth = closing ? Math.Max(0, depth - 1) : depth + 1;
        }

        private static string Attribute(string body, string name)
        {
            var at = body.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);

            if (at < 0)
            {
                return string.Empty;
            }

            var rest = body[(at + name.Length + 1)..];

            if (rest.Length == 0)
            {
                return string.Empty;
            }

            var quote = rest[0];

            if (quote is '"' or '\'')
            {
                var end = rest.IndexOf(quote, 1);

                return end < 0 ? string.Empty : WebUtility.HtmlDecode(rest[1..end]);
            }

            var stop = rest.IndexOfAny([' ', '\t']);

            return WebUtility.HtmlDecode(stop < 0 ? rest : rest[..stop]);
        }

        private void Append(string raw)
        {
            if (raw.Length == 0)
            {
                return;
            }

            /*
             * DECODED HERE, once, and only for text -- so a post that writes "&lt;html&gt;" to talk
             * ABOUT a tag shows the tag rather than having it swallowed as markup on a second pass.
             *
             * Non-breaking spaces become ordinary ones. The feed uses "<p>&nbsp;</p>" as a spacer,
             * and left alone those paragraphs are not empty by any whitespace test, so every one of
             * them would render as a mysterious gap with no text in it.
             */
            var text = WebUtility.HtmlDecode(raw).Replace(' ', ' ');

            /*
             * Whitespace collapsed the way a browser would, because the source is indented HTML and
             * the runs of newlines in it are not meant to be seen. A space at either edge is KEPT
             * here -- it is what joins "foo " to a following "<strong>bar</strong>" -- and trimmed
             * off later at the block edges, where it is not wanted.
             */
            var pending = false;

            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    pending = true;

                    continue;
                }

                if (pending)
                {
                    _text.Append(' ');
                    pending = false;
                }

                _text.Append(c);
            }

            if (pending)
            {
                _text.Append(' ');
            }
        }

        private void EndRun()
        {
            if (_text.Length == 0)
            {
                return;
            }

            _runs.Add(new NewsRun(_text.ToString(), _bold > 0, _italic > 0, _url));
            _text.Clear();
        }

        private void Flush()
        {
            EndRun();

            if (_runs.Count == 0)
            {
                return;
            }

            // A spacer paragraph -- "<p>&nbsp;</p>" -- carries no text worth a row.
            if (_runs.All(r => r.Text.Trim().Length == 0))
            {
                _runs.Clear();

                return;
            }

            var trimmed = Trim(_runs);

            _blocks.Add(new NewsBlock(_kind, trimmed, _marker));
            _runs.Clear();
        }

        /// <summary>Drops the leading and trailing space a block picks up from indented markup.</summary>
        private static List<NewsRun> Trim(List<NewsRun> runs)
        {
            var copy = runs.ToList();

            copy[0] = copy[0] with { Text = copy[0].Text.TrimStart() };

            var last = copy.Count - 1;

            copy[last] = copy[last] with { Text = copy[last].Text.TrimEnd() };

            return [.. copy.Where(r => r.Text.Length != 0)];
        }
    }
}
