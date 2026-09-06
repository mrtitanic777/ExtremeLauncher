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
 * Ported in behaviour from launcher/ui/pages/global/AccountListPage.cpp and the dialogs it drives.
 *
 * THE SIGN-IN THE LAUNCHER ACTUALLY SHOWS. Device code rather than an embedded browser, which is
 * upstream's own fallback and the better choice here: it needs no web view, works the same on all
 * three platforms, and never asks the user to type a Microsoft password into a window this program
 * drew. The user gets a short code and a URL, and confirms in a browser they already trust.
 *
 * THE UI THREAD is reached through an injected post delegate, as everywhere else in this port, so all
 * of this is testable without a dispatcher -- including the part that matters most, which is what the
 * screen says while a sign-in is half-finished.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Auth;

namespace ExtremeLauncher.ViewModels;

/// <summary>One row in the account list.</summary>
public sealed partial class AccountViewModel(MinecraftAccount account) : ObservableObject
{
    [ObservableProperty]
    private bool _isDefault;

    public MinecraftAccount Account { get; } = account;

    public string ProfileName => Account.ProfileName.Length != 0 ? Account.ProfileName : "(no profile)";

    public string TypeLabel => Account.AccountType switch
    {
        AccountType.Msa => "Microsoft",
        AccountType.Offline => "Offline",
        _ => "Unknown",
    };

    /// <summary>What the status column says. The interesting cases are all failures.</summary>
    public string StatusLabel => Account.AccountType == AccountType.Offline
        ? "Offline account"
        : Account.AccountState switch
        {
            AccountState.Online => "Ready",
            AccountState.Working => "Signing in…",
            AccountState.Expired => "Sign-in expired",
            AccountState.Disabled => "Needs signing in again",
            AccountState.Errored => Account.LastError.Length != 0 ? Account.LastError : "Error",
            AccountState.Gone => "Account no longer exists",
            _ => "Unknown",
        };

    public void Refreshed()
    {
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(StatusLabel));
    }
}

public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly AccountList _accounts;
    private readonly HttpClient? _client;
    private readonly string _clientId;
    private readonly Action<Action> _post;
    private readonly Func<string, Task>? _openBrowser;

    private CancellationTokenSource? _signIn;

    public AccountsViewModel(
        AccountList accounts,
        HttpClient? client = null,
        string clientId = "",
        Action<Action>? post = null,
        Func<string, Task>? openBrowser = null)
    {
        _accounts = accounts;
        _client = client;
        _clientId = clientId;
        _post = post ?? (a => a());
        _openBrowser = openBrowser;

        _accounts.ListChanged += (_, _) => _post(Rebuild);
        _accounts.DefaultAccountChanged += (_, _) => _post(MarkDefault);

        Rebuild();
    }

    [ObservableProperty]
    private AccountViewModel? _selected;

    /// <summary>The code and URL to show while a device-code sign-in is waiting.</summary>
    [ObservableProperty]
    private DeviceCodeInfo? _pendingCode;

    [ObservableProperty]
    private bool _isSigningIn;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The name to give a new offline account.</summary>
    [ObservableProperty]
    private string _offlineName = string.Empty;

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    /// <summary>
    /// Whether Microsoft sign-in can be offered at all.
    /// </summary>
    /// <remarks>
    /// False in a build with no client id configured — which is this repository's default, since the
    /// keys are supplied at runtime rather than committed. The button is disabled with a reason
    /// showing rather than failing when pressed.
    /// </remarks>
    public bool CanSignIn => _client is not null && _clientId.Length != 0 && !IsSigningIn;

    public bool CanAddOffline => OfflineName.Trim().Length != 0;

    public bool CanRemove => Selected is not null;

    public bool CanSetDefault => Selected is not null && !Selected.IsDefault;

    /// <summary>The account a launch should use, or null to play offline.</summary>
    public MinecraftAccount? DefaultAccount => _accounts.DefaultAccount;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    public async Task SignInAsync()
    {
        if (_client is null || _clientId.Length == 0)
        {
            return;
        }

        var account = MinecraftAccount.CreateBlankMsa();

        var step = new MSADeviceCodeStep(account.Data, _client, _clientId);

        // The code arrives partway through the step, not at the start: it takes one request to get it.
        step.AuthorizeWithBrowser += (_, info) => _post(() =>
        {
            PendingCode = info;
            Status = $"Enter the code {info.UserCode} at {info.VerificationUri}.";

            // Best effort: an opened browser is a convenience, and the code is on screen regardless.
            _ = _openBrowser?.Invoke(info.VerificationUri);
        });

        var flow = AuthFlow.CreateMsaFlow(account.Data, _client, step, "Signing in");

        flow.StatusChanged += (_, text) => _post(() =>
        {
            // Only once the code has been dealt with, or this overwrites the instructions the user
            // is in the middle of following.
            if (PendingCode is null)
            {
                Status = text;
            }
        });

        _signIn = new CancellationTokenSource();
        IsSigningIn = true;
        Status = "Asking Microsoft for a sign-in code…";

        try
        {
            await flow.RunAsync(_signIn.Token).ConfigureAwait(false);

            if (flow.TaskState == AccountTaskState.Succeeded)
            {
                _post(() =>
                {
                    _accounts.Add(account);

                    // The first account signed in becomes the default, because otherwise somebody
                    // signs in and nothing appears to happen.
                    _accounts.DefaultAccount ??= account;

                    Status = $"Signed in as {account.ProfileName}.";
                });
            }
            else
            {
                _post(() => Status = flow.FailReason.Length != 0 ? flow.FailReason : "Sign-in failed.");
            }
        }
        catch (OperationCanceledException)
        {
            _post(() => Status = "Sign-in cancelled.");
        }
        catch (LauncherException e)
        {
            _post(() => Status = e.Message);
        }
        finally
        {
            _post(() =>
            {
                IsSigningIn = false;
                PendingCode = null;
            });

            _signIn?.Dispose();
            _signIn = null;
        }
    }

    [RelayCommand]
    public void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand(CanExecute = nameof(CanAddOffline))]
    public void AddOffline()
    {
        var name = OfflineName.Trim();

        if (name.Length == 0)
        {
            return;
        }

        _accounts.Add(MinecraftAccount.CreateOffline(name));

        OfflineName = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    public void Remove()
    {
        if (Selected is { } row)
        {
            _accounts.Remove(row.Account);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSetDefault))]
    public void SetDefault()
    {
        if (Selected is { } row)
        {
            _accounts.DefaultAccount = row.Account;
        }
    }

    private void Rebuild()
    {
        var selected = Selected?.Account;

        Accounts.Clear();

        foreach (var account in _accounts.Accounts)
        {
            Accounts.Add(new AccountViewModel(account));
        }

        MarkDefault();

        // Kept across a rebuild where it still exists, so removing one row does not deselect another.
        Selected = Accounts.FirstOrDefault(a => ReferenceEquals(a.Account, selected));
    }

    private void MarkDefault()
    {
        foreach (var row in Accounts)
        {
            row.IsDefault = ReferenceEquals(row.Account, _accounts.DefaultAccount);
        }

        OnPropertyChanged(nameof(DefaultAccount));

        /*
         * CanSetDefault IS BOUND TO IsEnabled IN THE XAML, so notifying only the command leaves the
         * button live after the account it would set is already the default. The view model tests all
         * passed -- they read the property, which recomputes -- and the headless window test caught
         * it. That is the difference the UI tests exist for.
         */
        OnPropertyChanged(nameof(CanSetDefault));
        SetDefaultCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChanged(AccountViewModel? value)
    {
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanSetDefault));
        RemoveCommand.NotifyCanExecuteChanged();
        SetDefaultCommand.NotifyCanExecuteChanged();
    }

    partial void OnOfflineNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanAddOffline));
        AddOfflineCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSigningInChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSignIn));
        SignInCommand.NotifyCanExecuteChanged();
    }
}
