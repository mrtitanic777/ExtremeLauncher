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
 * The one dialog this launcher has, replacing the QMessageBox calls scattered through upstream's UI.
 *
 * Avalonia ships no message box at all -- unlike Qt, where QMessageBox::question is one line. Rather
 * than take a dependency for a single confirmation, this builds it: a modal window, two buttons, and
 * the rule that everything unanswered is a NO.
 *
 * THE DEFAULT IS CANCEL, EVERYWHERE. Closing the window with the X, pressing Escape, or the dialog
 * failing to appear at all must all mean "do not delete". The only thing that means yes is somebody
 * pressing the button with the word on it.
 */

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

/// <summary>Asks the user things, in a window.</summary>
public sealed class DialogPrompts : IUserPrompts
{
    private readonly Func<Window?> _owner;

    /// <param name="owner">
    /// Fetched lazily, because the prompts are built before the main window exists.
    /// </param>
    public DialogPrompts(Func<Window?> owner) => _owner = owner;

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel,
        bool destructive = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(
                () => ConfirmAsync(title, message, confirmLabel, destructive)).ConfigureAwait(true);
        }

        /*
         * With no parent window there is nothing to be modal to, and ShowDialog throws. Answering "no"
         * is the only safe reading: a confirmation that could not be shown has not been given.
         */
        if (_owner() is not { } owner)
        {
            return false;
        }

        var answered = false;

        var confirm = new Button
        {
            Content = confirmLabel,
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        if (destructive)
        {
            // Coloured rather than made the default: the destructive button must never be the one a
            // stray Return press activates.
            confirm.Foreground = Brushes.White;
            confirm.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        }

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,

            // Return activates Cancel, and it is focused when the dialog opens.
            IsDefault = true,
        };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = 460,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 18,
                Children =
                {
                    new SelectableTextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm },
                    },
                },
            },
        };

        confirm.Click += (_, _) =>
        {
            answered = true;
            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        // Escape closes without answering, which is to say: no.
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };

        dialog.Opened += (_, _) => cancel.Focus();

        await dialog.ShowDialog(owner).ConfigureAwait(true);

        // Closing by any route other than the confirm button leaves this false. That is the point.
        return answered;
    }

    /// <summary>
    /// Asks for a line of text.
    /// </summary>
    /// <remarks>
    /// Cancel returns NULL, and so does a dialog that could not be shown; an empty string means the
    /// user really did clear the box, which the caller may want to complain about. Conflating the two
    /// would turn "I changed my mind" into "make one called nothing".
    /// </remarks>
    public async Task<string?> PromptForTextAsync(string title, string message, string initialValue)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(
                () => PromptForTextAsync(title, message, initialValue)).ConfigureAwait(true);
        }

        if (_owner() is not { } owner)
        {
            return null;
        }

        string? answer = null;

        var input = new TextBox { Text = initialValue };

        var ok = new Button
        {
            Content = "OK",
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,

            // Safe to make the default here: the affirmative answer creates something rather than
            // destroying anything, so Return doing it is what a person expects.
            IsDefault = true,
        };

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = 460,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, ok },
                    },
                },
            },
        };

        ok.Click += (_, _) =>
        {
            answer = input.Text ?? string.Empty;
            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };

        // Focused and selected, so typing replaces the suggestion rather than appending to it.
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        await dialog.ShowDialog(owner).ConfigureAwait(true);

        return answer;
    }
}
