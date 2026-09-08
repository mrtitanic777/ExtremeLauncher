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
 * The Technic browser window. It loads the trending list on open, and the Search button re-queries.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class TechnicBrowserWindow : Window
{
    public TechnicBrowserWindow() => InitializeComponent();

    public TechnicBrowserWindow(TechnicBrowserViewModel model) : this() => DataContext = model;

    /// <summary>The pack to install, or null when the window was cancelled.</summary>
    public TechnicModpack? Chosen { get; private set; }

    /// <summary>What to call the instance.</summary>
    public string InstanceName { get; private set; } = string.Empty;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();

        this.FindControl<Button>("InstallButton")!.Click += (_, _) =>
        {
            if (DataContext is TechnicBrowserViewModel model)
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

        if (DataContext is TechnicBrowserViewModel model)
        {
            _ = model.LoadAsync();
        }
    }
}
