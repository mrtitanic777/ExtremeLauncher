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
 * The system clipboard, which in Avalonia belongs to a top-level window rather than to the
 * application -- so it is reached through whichever window is asking.
 */

using Avalonia.Controls;
using Avalonia.Threading;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppClipboard : IClipboard
{
    private readonly Func<Window?> _owner;

    public AppClipboard(Func<Window?> owner) => _owner = owner;

    public async Task<bool> SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => SetTextAsync(text)).ConfigureAwait(true);
        }

        // No window means no clipboard: reported as false rather than thrown, because failing to copy
        // is a disappointment and not an error.
        if (_owner()?.Clipboard is not { } clipboard)
        {
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);

            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            // Some platforms refuse a clipboard write from a background context, and a headless build
            // has none at all.
            return false;
        }
    }
}
