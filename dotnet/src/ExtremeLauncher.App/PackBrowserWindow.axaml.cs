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
 * All the behaviour is in PackBrowserViewModel; this is the window around it.
 */

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class PackBrowserWindow : Window
{
    public PackBrowserWindow() => InitializeComponent();

    public PackBrowserWindow(PackBrowserViewModel model) : this() => DataContext = model;

    /// <summary>What to install, or null when the window was cancelled.</summary>
    public (IndexedPack Pack, IndexedVersion Version)? Chosen { get; private set; }

    /// <summary>What to call the instance.</summary>
    public string InstanceName { get; private set; } = string.Empty;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        var search = this.FindControl<Button>("SearchButton")!;
        var box = this.FindControl<TextBox>("SearchBox")!;

        search.Click += (_, _) => Run();

        // "CurseForge" is item 1; the enum calls it Flame. Same mapping as the mod browser.
        this.FindControl<ComboBox>("ProviderBox")!.SelectionChanged += (_, _) =>
        {
            if (DataContext is PackBrowserViewModel model)
            {
                model.Provider = this.FindControl<ComboBox>("ProviderBox")!.SelectedIndex == 1
                    ? ExtremeLauncher.ModPlatform.ResourceProvider.Flame
                    : ExtremeLauncher.ModPlatform.ResourceProvider.Modrinth;
            }
        };

        // Return searches, as in the mod browser: the field is a search box, and Cancel being the
        // default button still governs Escape and the rest of the window.
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return)
            {
                e.Handled = true;

                Run();
            }
        };

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();

        this.FindControl<Button>("InstallButton")!.Click += (_, _) =>
        {
            if (DataContext is PackBrowserViewModel model)
            {
                model.Accept();

                Chosen = model.Chosen;

                // Read BEFORE the window closes: the view model outlives it, but reading through a
                // closed window's DataContext is the kind of thing that quietly returns empty.
                InstanceName = model.EffectiveInstanceName;
            }

            Close();
        };
    }

    private void Run()
    {
        if (DataContext is PackBrowserViewModel model && model.CanSearch)
        {
            _ = model.SearchAsync();
        }
    }
}
