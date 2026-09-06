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
 * The window-shaped half of the news.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppNews(Func<Window?> owner, ILinkOpener? links = null) : INewsUi
{
    public async Task ShowAsync(IReadOnlyList<NewsEntry> entries, bool startWithListHidden)
    {
        var window = new NewsWindow(new NewsWindowViewModel(entries, startWithListHidden, links));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }
}
