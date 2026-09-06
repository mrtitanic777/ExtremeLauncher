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
 * The new-instance dialog. Like DialogPrompts, Cancel is the DEFAULT button -- Return closes without
 * creating anything, because a stray keypress in a name box should not commit a directory.
 */

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class NewInstanceWindow : Window
{
    /// <summary>The id of the instance that was created, or empty when the dialog was cancelled.</summary>
    public string CreatedId { get; private set; } = string.Empty;

    public NewInstanceWindow() => InitializeComponent();

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("CreateButton")!.Click += async (_, _) => await CreateAsync().ConfigureAwait(true);
    }

    /// <summary>The list this dialog creates into. Set by the caller before showing it.</summary>
    public InstanceList? Instances { get; set; }

    private async Task CreateAsync()
    {
        if (DataContext is not NewInstanceViewModel viewModel || Instances is null)
        {
            return;
        }

        var id = await viewModel.CreateAsync(Instances).ConfigureAwait(true);

        // Left open on failure, with the reason on screen: closing would throw away everything the
        // user typed for a problem they may be able to fix.
        if (id.Length == 0)
        {
            return;
        }

        CreatedId = id;

        Close();
    }
}
