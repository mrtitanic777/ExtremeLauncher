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
 * All the behaviour is in GlobalSettingsViewModel; this is the window around it.
 *
 * CANCEL DISCARDS, and that is why the window exists as a dialog rather than saving as you type: the
 * settings object is shared with the running launcher, so an edit that reached it immediately would
 * take effect on the next launch whether or not the user meant it. Revert is what puts the file's
 * values back if they change their mind before saving.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class GlobalSettingsWindow : Window
{
    public GlobalSettingsWindow() => InitializeComponent();

    public GlobalSettingsWindow(GlobalSettingsViewModel model) : this() => DataContext = model;

    /// <summary>True when the user saved rather than cancelled.</summary>
    public bool Saved { get; private set; }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("CancelButton")!.Click += (_, _) =>
        {
            /*
             * Reverted on the way out, not merely closed. The settings object outlives this window, so
             * leaving edited values in it would apply them to the next launch despite Cancel.
             */
            if (DataContext is GlobalSettingsViewModel model)
            {
                model.Revert();
            }

            Close();
        };

        this.FindControl<Button>("SaveButton")!.Click += (_, _) =>
        {
            if (DataContext is not GlobalSettingsViewModel model)
            {
                Close();

                return;
            }

            // Stays open on a failed write, with the reason on screen: closing here would throw away
            // everything the user typed and tell them nothing.
            if (model.Save())
            {
                Saved = true;

                Close();
            }
        };
    }
}
