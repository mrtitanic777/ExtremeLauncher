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
 * The window-shaped half of the accounts UI, kept out of the view models so those stay testable.
 */

using System.Diagnostics;
using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppAccounts(
    Func<Window?> owner,
    AccountList accounts,
    HttpClient client,
    string clientId,
    LauncherLog? log = null) : IAccountsUi
{
    public string CurrentAccountName => accounts.DefaultAccount?.ProfileName ?? string.Empty;

    public async Task OpenAsync()
    {
        var model = new AccountsViewModel(accounts, client, clientId, openBrowser: OpenBrowserAsync);

        var window = new AccountsWindow(model, accounts);

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }

        log?.Info($"Accounts window closed; default is {(CurrentAccountName.Length != 0 ? CurrentAccountName : "none")}.");
    }

    /// <summary>
    /// Opens the verification page in the user's browser.
    /// </summary>
    /// <remarks>
    /// A CONVENIENCE, NOT A REQUIREMENT: the code and the URL are on screen either way, so a machine
    /// with no browser association loses nothing but a click. Failing loudly here would turn a
    /// cosmetic problem into a failed sign-in.
    ///
    /// UseShellExecute is what makes this work on all three platforms -- it hands the URL to the
    /// desktop's own handler rather than trying to name a browser.
    /// </remarks>
    private Task OpenBrowserAsync(string url)
    {
        try
        {
            // Only ever a URL this launcher built from Microsoft's own response, never user input.
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && parsed.Scheme is "http" or "https")
            {
                using var process = Process.Start(new ProcessStartInfo(parsed.AbsoluteUri)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Warning($"Could not open a browser for the sign-in page: {e.Message}");
        }

        return Task.CompletedTask;
    }
}
