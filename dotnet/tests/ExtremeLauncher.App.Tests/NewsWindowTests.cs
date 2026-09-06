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
 * The news window through its real visual tree.
 *
 * THE ARTICLE BODY IS THE WHOLE REASON THIS FILE EXISTS. It is the only part of any window in this
 * port built in code rather than by a binding, so it is the only part where the view model can be
 * completely right and the screen completely empty. Everything here is about what actually landed in
 * the panel.
 */

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using ExtremeLauncher.Core;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class NewsWindowTests : IDisposable
{
    private readonly List<Window> _windows = [];

    public void Dispose()
    {
        foreach (var window in _windows)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static NewsEntry Entry(string title, string content, string link = "https://example.invalid/post")
        => new() { Title = title, Content = content, Link = link };

    private (NewsWindow Window, NewsWindowViewModel Model) Show(
        IReadOnlyList<NewsEntry>? entries = null,
        bool hidden = false,
        ILinkOpener? links = null)
    {
        var model = new NewsWindowViewModel(
            entries ?? [Entry("First", "<p>Hello <strong>world</strong></p>"), Entry("Second", "<p>Ho</p>")],
            hidden,
            links);

        var window = new NewsWindow(model);

        _windows.Add(window);

        window.Show();

        Settle(window);

        return (window, model);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static StackPanel Body(NewsWindow window)
        => window.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Name == "ArticleBody");

    [AvaloniaFact]
    public void TheArticleActuallyReachesTheScreen()
    {
        // The view model can hold perfect blocks and the panel still be empty: nothing binds it.
        var (window, _) = Show();

        Assert.NotEmpty(Body(window).Children);
    }

    [AvaloniaFact]
    public void BoldWordsAreBoldInTheControlAndNotJustInTheModel()
    {
        /*
         * The full path: HTML -> NewsHtml -> blocks -> runs -> Inlines. A code-behind that forgot the
         * weight would render the article perfectly, in one flat style, and every other test here
         * would still pass.
         */
        var (window, _) = Show([Entry("Only", "<p>Plain <strong>bold</strong> plain</p>")]);

        var runs = Body(window).Children
            .OfType<SelectableTextBlock>()
            .SelectMany(t => t.Inlines!)
            .OfType<Run>()
            .ToList();

        Assert.Equal(3, runs.Count);
        Assert.Equal(FontWeight.SemiBold, runs[1].FontWeight);
        Assert.Equal("bold", runs[1].Text);
    }

    [AvaloniaFact]
    public void RunsShareOneTextBlockSoTextStillWraps()
    {
        /*
         * WHY THIS IS BUILT IN CODE AT ALL. An ItemsControl over the runs would be shorter and would
         * put each run in its own box, so a paragraph with a bold word in the middle would stop
         * wrapping between the boxes and break in the wrong places.
         */
        var (window, _) = Show([Entry("Only", "<p>Plain <strong>bold</strong> plain</p>")]);

        var block = Assert.Single(Body(window).Children.OfType<SelectableTextBlock>());

        Assert.Equal(3, block.Inlines!.Count);
        Assert.Equal(TextWrapping.Wrap, block.TextWrapping);
    }

    [AvaloniaFact]
    public void ListItemsGetTheirMarkerInItsOwnColumn()
    {
        // So a wrapped bullet lines up under its text rather than under the bullet.
        var (window, _) = Show([Entry("Only", "<ul><li>one</li></ul>")]);

        var row = Assert.Single(Body(window).Children.OfType<Grid>());

        var marker = row.Children.OfType<TextBlock>().First(t => t is not SelectableTextBlock);

        Assert.Equal("•", marker.Text);
        Assert.Equal(0, Grid.GetColumn(marker));
        Assert.Equal(1, Grid.GetColumn(row.Children.OfType<SelectableTextBlock>().Single()));
    }

    [AvaloniaFact]
    public void ChoosingAnotherArticleRedrawsTheBody()
    {
        /*
         * The body is rebuilt from a collection-changed handler rather than by a binding, so this is
         * the one that catches "the window shows the first article forever".
         */
        var (window, model) = Show();

        Assert.Contains("Hello", Text(window), StringComparison.Ordinal);

        model.Selected = model.Entries[1];

        Settle(window);

        Assert.Contains("Ho", Text(window), StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", Text(window), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheFirstArticleIsShownWithoutBeingClicked()
    {
        // Upstream selects row zero in its constructor; a window that opens blank looks broken.
        var (window, model) = Show();

        Assert.Equal("First", model.Selected?.Title);
        Assert.NotEmpty(Body(window).Children);
    }

    [AvaloniaFact]
    public void ClickingFromTheHeadlineOpensWithTheListCollapsed()
    {
        // Upstream's newsButtonClicked toggles the list before showing: clicking a story means "show
        // me that story", not "show me the index".
        var (window, _) = Show(hidden: true);

        var list = window.GetLogicalDescendants().OfType<ListBox>().Single();

        Assert.False(list.IsVisible);
    }

    [AvaloniaFact]
    public void TheToggleButtonSaysWhatItWillDo()
    {
        var (window, model) = Show();

        var button = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ToggleListButton");

        Assert.Equal("Hide article list", button.Content);

        button.Command!.Execute(null);

        Settle(window);

        Assert.Equal("Show article list", button.Content);
        Assert.False(model.IsListVisible);
    }

    [AvaloniaFact]
    public void WithOneArticleThereIsNoListToToggle()
    {
        // A column containing one row, taking a third of the window to repeat the heading.
        var (window, _) = Show([Entry("Only", "<p>x</p>")]);

        Assert.False(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ToggleListButton").IsVisible);
    }

    [AvaloniaFact]
    public void TheLinkButtonIsDeadWithoutAnOpener()
    {
        var (window, _) = Show();

        Assert.False(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "OpenLinkButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task ReadingTheFullPostOpensTheEntrysOwnLink()
    {
        var opened = new List<string>();

        var (window, model) = Show(links: new StubLinks(opened));

        model.Selected = model.Entries[1];

        Settle(window);

        await model.OpenLinkAsync();

        Assert.Equal(["https://example.invalid/post"], opened);
    }

    [AvaloniaFact]
    public void AnEntryWithNothingInItDoesNotCrashTheWindow()
    {
        // The parser's defaults reach the screen: "No content." rather than an empty panel.
        var (window, _) = Show([new NewsEntry()]);

        Assert.Contains("No content.", Text(window), StringComparison.Ordinal);
    }

    /// <remarks>
    /// READ OFF THE INLINES, NOT OFF Text. A TextBlock filled through Inlines leaves its Text
    /// property null, so a helper that reads Text returns nothing at all and every assertion using it
    /// fails for a reason that has nothing to do with the window -- which is how the first version of
    /// this file failed.
    /// </remarks>
    private static string Text(NewsWindow window)
        => string.Concat(Body(window)
            .GetLogicalDescendants()
            .OfType<SelectableTextBlock>()
            .SelectMany(t => t.Inlines ?? [])
            .OfType<Run>()
            .Select(r => r.Text));

    private sealed class StubLinks(List<string> opened) : ILinkOpener
    {
        public Task OpenAsync(string url)
        {
            opened.Add(url);

            return Task.CompletedTask;
        }
    }
}
