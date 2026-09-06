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
 * Shows the new-instance dialog. The app owns this because the app owns windows; the decisions are all
 * in NewInstanceViewModel, where they are tested.
 */

using Avalonia.Controls;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppInstanceCreator : IInstanceCreator
{
    private readonly Func<Window?> _owner;

    private readonly MetaVersionListSource _versions;

    private readonly (LauncherPaths Paths, HttpClient Client, string MetaUrl)? _resolution;

    public AppInstanceCreator(
        Func<Window?> owner,
        MetaVersionListSource versions,
        (LauncherPaths Paths, HttpClient Client, string MetaUrl)? resolution = null)
    {
        _owner = owner;
        _versions = versions;
        _resolution = resolution;
    }

    public async Task<string> CreateAsync(InstanceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (_owner() is not { } owner)
        {
            return string.Empty;
        }

        var viewModel = new NewInstanceViewModel(new AppVersionListSource(_versions), resolution: _resolution);

        var window = new NewInstanceWindow
        {
            DataContext = viewModel,
            Instances = list,
        };

        /*
         * The fetch is started but NOT awaited before showing: the dialog appears immediately with its
         * name box ready to type in, and the list fills underneath. Awaiting first would leave the user
         * looking at nothing while the metadata server is contacted.
         */
        _ = viewModel.LoadVersionsAsync();

        await window.ShowDialog(owner).ConfigureAwait(true);

        return window.CreatedId;
    }
}
