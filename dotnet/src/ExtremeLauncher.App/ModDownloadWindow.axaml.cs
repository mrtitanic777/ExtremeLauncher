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
 * All the behaviour is in ModDownloadViewModel; this is the window around it.
 */

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;

// Avalonia.Controls has a ResourceProvider of its own; this is the mod-platform one.
using ResourceProvider = ExtremeLauncher.ModPlatform.ResourceProvider;

namespace ExtremeLauncher.App;

public partial class ModDownloadWindow : Window
{
    public ModDownloadWindow() => InitializeComponent();

    public ModDownloadWindow(ModDownloadViewModel model) : this()
    {
        DataContext = model;

        /*
         * The version list is fetched when a row is highlighted, not for all twenty-five results at
         * once: that would be twenty-five requests for a list where the user will look at two.
         */
        model.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(model.Highlighted) && model.Highlighted is { } row)
            {
                await model.LoadVersionsAsync(row).ConfigureAwait(true);
            }
        };
    }

    /// <summary>What the user chose to install, or empty when cancelled.</summary>
    public IReadOnlyList<(IndexedPack Pack, IndexedVersion Version)> Chosen { get; private set; } = [];

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        var search = this.FindControl<Button>("SearchButton")!;
        var box = this.FindControl<TextBox>("SearchBox")!;

        search.Click += (_, _) => Run();

        // Return searches. This is the one dialog where that is right: the field is a search box, and
        // Cancel being the default button still governs Escape and the rest of the window.
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return)
            {
                e.Handled = true;

                Run();
            }
        };

        this.FindControl<ComboBox>("ProviderBox")!.SelectionChanged += (_, _) =>
        {
            if (DataContext is ModDownloadViewModel model)
            {
                model.Provider = this.FindControl<ComboBox>("ProviderBox")!.SelectedIndex == 1
                    ? ResourceProvider.Flame
                    : ResourceProvider.Modrinth;
            }
        };

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();

        this.FindControl<Button>("InstallButton")!.Click += (_, _) =>
        {
            if (DataContext is ModDownloadViewModel model)
            {
                model.Accept();

                Chosen = model.Chosen;
            }

            Close();
        };
    }

    private void Run()
    {
        if (DataContext is ModDownloadViewModel model && model.CanSearch)
        {
            _ = model.SearchAsync();
        }
    }
}
