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
 * The article body, which is the one part of this window that cannot be a binding.
 *
 * Upstream gives an entry's HTML to a QTextBrowser and gets rendering for free. Avalonia has no HTML
 * control, so NewsHtml turns the markup into blocks of styled runs and this builds the controls: one
 * text block per paragraph, its Inlines carrying the bold and italic spans, and a marker column for
 * list items.
 *
 * BUILT IN CODE RATHER THAN TEMPLATED because TextBlock.Inlines is not a bindable list -- an
 * ItemsControl over the runs would put each one in its own box and text would stop wrapping between
 * them, so "a very long sentence with one bold word" would break in the wrong places.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using ExtremeLauncher.Core;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class NewsWindow : Window
{
    public NewsWindow() => InitializeComponent();

    public NewsWindow(NewsWindowViewModel model) : this()
    {
        DataContext = model;

        model.Blocks.CollectionChanged += (_, _) => RenderArticle();

        RenderArticle();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    /// <summary>Rebuilds the article body from the selected entry's blocks.</summary>
    private void RenderArticle()
    {
        var body = this.FindControl<StackPanel>("ArticleBody");

        if (body is null || DataContext is not NewsWindowViewModel model)
        {
            return;
        }

        body.Children.Clear();

        foreach (var block in model.Blocks)
        {
            body.Children.Add(Render(block));
        }
    }

    private static Control Render(NewsBlock block)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,

            // Headings get their weight from the block, not from a run, because a heading whose
            // markup carried no <strong> would otherwise be indistinguishable from a paragraph.
            FontWeight = block.Kind == NewsBlockKind.Heading ? FontWeight.SemiBold : FontWeight.Normal,
            FontSize = block.Kind == NewsBlockKind.Heading ? 15 : 13,
        };

        foreach (var run in block.Runs)
        {
            text.Inlines!.Add(new Run(run.Text)
            {
                FontWeight = run.Bold ? FontWeight.SemiBold : FontWeight.Normal,
                FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal,
            });
        }

        if (block.Marker.Length == 0)
        {
            return text;
        }

        /*
         * A list item: the marker in its own fixed column so wrapped lines line up under the text
         * rather than under the bullet. Putting the marker in the same text block would be one line
         * shorter and would look wrong on every item long enough to wrap.
         */
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*"),
            Margin = new Thickness(12, 0, 0, 0),
        };

        var marker = new TextBlock
        {
            Text = block.Marker,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top,
        };

        Grid.SetColumn(marker, 0);
        Grid.SetColumn(text, 1);

        row.Children.Add(marker);
        row.Children.Add(text);

        return row;
    }
}
