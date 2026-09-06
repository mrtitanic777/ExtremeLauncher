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
 * All the behaviour is in VersionSelectViewModel; this is the window around it.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class VersionSelectWindow : Window
{
    public VersionSelectWindow() => InitializeComponent();

    public VersionSelectWindow(VersionSelectViewModel model) : this()
    {
        DataContext = model;

        /*
         * Started when the window opens, not awaited here: OnOpened runs on the UI thread and the
         * fetch is a network round trip. The list fills in under the user, with the status line
         * saying what is happening -- which is why the view model has one.
         */
        Opened += async (_, _) => await model.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>The version chosen, or empty when cancelled.</summary>
    public string ChosenVersion { get; private set; } = string.Empty;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();

        this.FindControl<Button>("AcceptButton")!.Click += (_, _) =>
        {
            if (DataContext is VersionSelectViewModel model)
            {
                model.Accept();

                ChosenVersion = model.ChosenVersion;
            }

            Close();
        };
    }
}
