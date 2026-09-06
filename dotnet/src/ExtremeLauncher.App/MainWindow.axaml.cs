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
 * Deliberately empty of logic. Everything decidable about this window lives in MainWindowViewModel,
 * where it can be tested without a display; if code starts accumulating here, that is the signal it
 * belongs in the view model instead.
 */

using Avalonia.Controls;

namespace ExtremeLauncher.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
