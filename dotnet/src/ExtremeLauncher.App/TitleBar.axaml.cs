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
 * A custom title bar is our own control, so it starts the window move itself (and maximises/restores
 * on a double click), the way the OS caption would have. One control, shared by every window that
 * extends its client area, so the drag logic is written once.
 */

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace ExtremeLauncher.App;

public partial class TitleBar : UserControl
{
    public TitleBar() => AvaloniaXamlLoader.Load(this);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2 && window.CanResize)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        window.BeginMoveDrag(e);
    }
}
