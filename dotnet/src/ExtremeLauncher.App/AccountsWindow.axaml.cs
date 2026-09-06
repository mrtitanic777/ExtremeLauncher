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
 * All the behaviour is in AccountsViewModel; this is the window around it.
 *
 * IT SAVES ON CLOSE rather than relying on autosave alone, because a sign-in that landed seconds
 * before the window shut is exactly the change nobody wants to repeat.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class AccountsWindow : Window
{
    private AccountList? _accounts;

    public AccountsWindow() => InitializeComponent();

    public AccountsWindow(AccountsViewModel model, AccountList accounts) : this()
    {
        DataContext = model;
        _accounts = accounts;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnClosed(EventArgs e)
    {
        _accounts?.Save();

        base.OnClosed(e);
    }
}
