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
 * The window around an OtherLogsPageViewModel pointed at the launcher's own logs folder.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class LauncherLogWindow : Window
{
    private OtherLogsPageViewModel? _model;

    public LauncherLogWindow() => InitializeComponent();

    public LauncherLogWindow(OtherLogsPageViewModel model) : this()
    {
        _model = model;
        DataContext = model;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        // The watcher holds a handle on the logs folder; leaving it running would keep rescanning
        // for a window nobody is looking at.
        Closed += (_, _) => _model?.Dispose();
    }
}
