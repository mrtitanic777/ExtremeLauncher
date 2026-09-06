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
 * All the behaviour is in ModUpdateViewModel; this is the window around it.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class ModUpdateWindow : Window
{
    public ModUpdateWindow() => InitializeComponent();

    public ModUpdateWindow(ModUpdateViewModel model) : this()
    {
        DataContext = model;

        // The check starts as the window opens rather than on a button: the user already pressed one
        // to get here, and a dialog that opens empty and waits for a second press is a dialog that
        // makes you ask it twice.
        Opened += async (_, _) => await model.CheckAsync().ConfigureAwait(true);
    }

    /// <summary>Whether anything was actually installed, so the caller can re-read the folder.</summary>
    public bool Applied => DataContext is ModUpdateViewModel { IsDone: true };

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }
}
