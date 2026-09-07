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
 * The classic FTB browser window. Unlike the Modrinth one there is no search button: the whole
 * catalogue is fetched when the window opens (LoadAsync) and the search box filters it live.
 */

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class LegacyFtbBrowserWindow : Window
{
    public LegacyFtbBrowserWindow() => InitializeComponent();

    public LegacyFtbBrowserWindow(LegacyFtbBrowserViewModel model) : this() => DataContext = model;

    /// <summary>What to install, or null when the window was cancelled.</summary>
    public (LegacyFtbModpack Pack, string Version)? Chosen { get; private set; }

    /// <summary>What to call the instance.</summary>
    public string InstanceName { get; private set; } = string.Empty;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();

        this.FindControl<Button>("InstallButton")!.Click += (_, _) =>
        {
            if (DataContext is LegacyFtbBrowserViewModel model)
            {
                model.Accept();

                Chosen = model.Chosen;
                InstanceName = model.EffectiveInstanceName;
            }

            Close();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Fetch the catalogue once the window is up; the view model guards against a second fetch.
        if (DataContext is LegacyFtbBrowserViewModel model)
        {
            _ = model.LoadAsync();
        }
    }
}
