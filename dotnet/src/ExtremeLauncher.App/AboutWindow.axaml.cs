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
 * All the content is in AboutViewModel; this is the window around it.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();

    public AboutWindow(AboutViewModel model) : this() => DataContext = model;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        this.FindControl<Button>("CopyButton")!.Click += async (_, _) =>
        {
            if (DataContext is not AboutViewModel model)
            {
                return;
            }

            var copied = await new AppClipboard(() => this).SetTextAsync(model.SupportSummary)
                .ConfigureAwait(true);

            /*
             * Confirmed on screen. A copy button that does nothing visible leaves somebody pressing it
             * repeatedly, and on a platform with no clipboard it would do exactly nothing forever.
             */
            this.FindControl<TextBlock>("CopyStatus")!.Text = copied
                ? "Copied to the clipboard."
                : "Could not reach the clipboard.";
        };
    }
}
