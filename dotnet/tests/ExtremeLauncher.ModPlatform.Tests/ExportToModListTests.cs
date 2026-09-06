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
 * Writing an installed mod list out in each of the five formats.
 *
 * The escaping tests are the ones that earn their place: every format escapes differently, and a
 * mod name with a bracket in it is the difference between a working markdown link and a mess.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class ExportToModListTests
{
    private static readonly ModListEntry Sodium = new(
        "Sodium",
        "https://modrinth.com/mod/sodium",
        "0.5.13",
        ["jellysquid3"],
        "sodium-fabric-0.5.13.jar");

    private static readonly ModListEntry Bare = new("Handmade Mod");

    [Fact]
    public void PlainTextCarriesEverythingAskedFor()
    {
        var text = ExportToModList.Render([Sodium], ModListFormat.PlainText);

        Assert.Equal("Sodium (https://modrinth.com/mod/sodium) [0.5.13] by jellysquid3", text);
    }

    [Fact]
    public void AFieldTheModHasNotGotIsSkippedRatherThanLeftEmpty()
    {
        /*
         * THE DIFFERENCE BETWEEN UPSTREAM'S APPROACH AND A TEMPLATE. My first version of this file
         * substituted into a per-format template, which for a mod with no url produced
         * "Handmade Mod () []" -- plausible-looking output that upstream never emits.
         */
        var text = ExportToModList.Render([Bare], ModListFormat.PlainText);

        Assert.Equal("Handmade Mod", text);
        Assert.DoesNotContain("()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlMakesTheNameTheLink()
    {
        // Which is the whole reason to pick HTML over plain text.
        var html = ExportToModList.Render([Sodium], ModListFormat.Html);

        Assert.Contains("<a href=\"https://modrinth.com/mod/sodium\">Sodium</a>", html, StringComparison.Ordinal);

        // A whole document: a bare run of <li> is not a list.
        Assert.StartsWith("<html><body><ul>", html, StringComparison.Ordinal);
        Assert.EndsWith("</ul></body></html>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlEscapesTheDangerousCharacters()
    {
        // A mod called "<script>" is not a reason to produce a document that runs it.
        var mod = new ModListEntry("A <b>bold</b> & \"quoted\" mod");

        var html = ExportToModList.Render([mod], ModListFormat.Html);

        Assert.Contains("&lt;b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownIsAListWithLinks()
    {
        var markdown = ExportToModList.Render([Sodium], ModListFormat.Markdown);

        Assert.StartsWith("- [Sodium](https://modrinth.com/mod/sodium)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownEscapesTheNameButNotTheUrl()
    {
        /*
         * The url goes inside the link target, where a backslash becomes part of the address. The
         * name goes in the visible half, where an unescaped bracket ends the link early.
         */
        var mod = new ModListEntry("Mod [Beta] (v2)", "https://example.invalid/a_b(c)");

        var markdown = ExportToModList.Render([mod], ModListFormat.Markdown);

        Assert.Contains(@"Mod \[Beta\] \(v2\)", markdown, StringComparison.Ordinal);
        Assert.Contains("(https://example.invalid/a_b(c))", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonIsRealJsonRatherThanPrinted()
    {
        // A mod with a quote in its name must not produce a document nothing can parse.
        var mod = new ModListEntry("A \"quoted\" mod", "https://example.invalid/x", "1.0", ["someone"]);

        var json = ExportToModList.Render([mod], ModListFormat.Json);

        var parsed = JsonNode.Parse(json)!.AsArray();

        Assert.Single(parsed);
        Assert.Equal("A \"quoted\" mod", (string?)parsed[0]!["name"]);
    }

    [Fact]
    public void JsonAuthorsAreAnArray()
    {
        // Upstream's choice, and the right one for a machine-readable format.
        var mod = new ModListEntry("Sodium", Authors: ["one", "two"]);

        var parsed = JsonNode.Parse(ExportToModList.Render([mod], ModListFormat.Json))!.AsArray();

        var authors = parsed[0]!["authors"]!.AsArray();

        Assert.Equal(2, authors.Count);
        Assert.Equal("one", (string?)authors[0]);
    }

    [Fact]
    public void JsonOmitsFieldsAModHasNotGot()
    {
        var parsed = JsonNode.Parse(ExportToModList.Render([Bare], ModListFormat.Json))!.AsArray();

        Assert.Null(parsed[0]!["url"]);
        Assert.Null(parsed[0]!["version"]);
    }

    [Fact]
    public void CsvKeepsEveryRowTheSameWidth()
    {
        /*
         * THE ONE FORMAT WHERE A MISSING FIELD STILL COSTS A CELL. A CSV whose rows have different
         * column counts is not a CSV, so "skip the field" cannot mean "skip the comma".
         */
        var csv = ExportToModList.Render([Sodium, Bare], ModListFormat.Csv);

        var rows = csv.Split('\n');

        Assert.Equal(2, rows.Length);
        Assert.Equal(rows[0].Count(c => c == ','), rows[1].Count(c => c == ','));
    }

    [Fact]
    public void CsvQuotesACellThatNeedsIt()
    {
        // Two authors joined with ", " would otherwise become two columns.
        var mod = new ModListEntry("Sodium", Authors: ["one", "two"]);

        var csv = ExportToModList.Render([mod], ModListFormat.Csv, ModListFields.Authors);

        Assert.Contains("\"one, two\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void CsvDoublesAQuoteInsideACell()
    {
        var mod = new ModListEntry("A \"quoted\" mod");

        Assert.Contains("\"A \"\"quoted\"\" mod\"", ExportToModList.Render([mod], ModListFormat.Csv), StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingFewerFieldsLeavesThemOut()
    {
        var text = ExportToModList.Render([Sodium], ModListFormat.PlainText, ModListFields.Version);

        Assert.Equal("Sodium [0.5.13]", text);
    }

    [Fact]
    public void TheFileNameCanBeIncluded()
    {
        // Off by default: it is the one field that is about your disk rather than about the mod.
        var text = ExportToModList.Render([Sodium], ModListFormat.PlainText, ModListFields.FileName);

        Assert.Equal("Sodium (sodium-fabric-0.5.13.jar)", text);
    }

    [Fact]
    public void ACustomTemplateIsSubstitutedAsWritten()
    {
        /*
         * The one place substitution IS the mechanism: the user wrote the template, so an empty field
         * is theirs to deal with.
         */
        var text = ExportToModList.Render([Sodium], "{name} => {version}");

        Assert.Equal("Sodium => 0.5.13", text);
    }

    [Fact]
    public void EveryModGetsALine()
    {
        var text = ExportToModList.Render([Sodium, Bare], ModListFormat.PlainText);

        Assert.Equal(2, text.Split('\n').Length);
    }

    [Fact]
    public void AnEmptyListRendersToNothingRatherThanThrowing()
    {
        Assert.Equal(string.Empty, ExportToModList.Render([], ModListFormat.PlainText));
        Assert.Equal("[]", JsonNode.Parse(ExportToModList.Render([], ModListFormat.Json))!.ToJsonString());
    }

    [Theory]
    [InlineData(ModListFormat.Html)]
    [InlineData(ModListFormat.Markdown)]
    [InlineData(ModListFormat.PlainText)]
    [InlineData(ModListFormat.Json)]
    [InlineData(ModListFormat.Csv)]
    public void EveryFormatHasAnExampleLineForTheDialogToShow(ModListFormat format)
    {
        var example = ExportToModList.ExampleLine(format);

        Assert.Contains("{name}", example, StringComparison.Ordinal);
    }
}
