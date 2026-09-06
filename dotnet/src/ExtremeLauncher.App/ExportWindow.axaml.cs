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
 * All the behaviour is in ExportViewModel; this is the window around it.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class ExportWindow : Window
{
    public ExportWindow() => InitializeComponent();

    public ExportWindow(ExportViewModel model) : this() => DataContext = model;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        /*
         * Radio buttons rather than a bound enum, because Avalonia has no built-in enum-to-radio
         * converter and writing one for a three-valued choice is more code than this.
         */
        this.FindControl<RadioButton>("PackRadio")!.IsCheckedChanged += (_, _) => SyncKind();
        this.FindControl<RadioButton>("CurseForgeRadio")!.IsCheckedChanged += (_, _) => SyncKind();
        this.FindControl<RadioButton>("ZipRadio")!.IsCheckedChanged += (_, _) => SyncKind();

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    private void SyncKind()
    {
        if (DataContext is ExportViewModel model)
        {
            model.Kind =
                this.FindControl<RadioButton>("ZipRadio")!.IsChecked == true ? ExportKind.InstanceZip
                : this.FindControl<RadioButton>("CurseForgeRadio")!.IsChecked == true ? ExportKind.CurseForgePack
                : ExportKind.ModrinthPack;
        }
    }
}
