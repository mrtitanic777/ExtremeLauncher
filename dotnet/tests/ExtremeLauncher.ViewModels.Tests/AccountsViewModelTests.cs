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
 * WHAT THE SCREEN SAYS WHILE SOMEBODY SIGNS IN, which is the part of a device-code flow that is easy
 * to get wrong and impossible to notice in a unit test of the step itself.
 *
 * The whole sign-in is driven here against a stubbed Microsoft, so the code-on-screen, the cancel, and
 * the "what happens when it fails" paths are all exercised without a browser or a dispatcher.
 */

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class AccountsViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "el-accvm-" + Guid.NewGuid().ToString("N"));

    public AccountsViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AccountList NewList() => new(Path.Combine(_folder, "accounts.json"));

    [Fact]
    public void AnEmptyListShowsNothingAndOffersNoActions()
    {
        var vm = new AccountsViewModel(NewList());

        Assert.Empty(vm.Accounts);
        Assert.False(vm.CanRemove);
        Assert.False(vm.CanSetDefault);

        // No client id in this build, so the Microsoft button must not pretend it works.
        Assert.False(vm.CanSignIn);
    }

    [Fact]
    public void AddingAnOfflineAccountShowsARow()
    {
        var vm = new AccountsViewModel(NewList()) { OfflineName = "Steve" };

        Assert.True(vm.CanAddOffline);

        vm.AddOffline();

        Assert.Single(vm.Accounts);
        Assert.Equal("Steve", vm.Accounts[0].ProfileName);
        Assert.Equal("Offline", vm.Accounts[0].TypeLabel);

        // Cleared, so the next one does not arrive pre-filled with the last name typed.
        Assert.Equal(string.Empty, vm.OfflineName);
    }

    [Fact]
    public void ABlankOfflineNameIsRefused()
    {
        var vm = new AccountsViewModel(NewList()) { OfflineName = "   " };

        Assert.False(vm.CanAddOffline);

        vm.AddOffline();

        Assert.Empty(vm.Accounts);
    }

    [Fact]
    public void SelectingARowEnablesRemoveAndSetDefault()
    {
        var vm = new AccountsViewModel(NewList()) { OfflineName = "Steve" };

        vm.AddOffline();

        Assert.False(vm.CanRemove);

        vm.Selected = vm.Accounts[0];

        Assert.True(vm.CanRemove);
        Assert.True(vm.CanSetDefault);
    }

    [Fact]
    public void MakingAnAccountTheDefaultMarksItAndDisablesTheButton()
    {
        var vm = new AccountsViewModel(NewList()) { OfflineName = "Steve" };

        vm.AddOffline();
        vm.Selected = vm.Accounts[0];
        vm.SetDefault();

        Assert.True(vm.Accounts[0].IsDefault);
        Assert.NotNull(vm.DefaultAccount);

        // Already the default: pressing it again would be a no-op, so it stops being offered.
        Assert.False(vm.CanSetDefault);
    }

    [Fact]
    public void MakingAnAccountTheDefaultAnnouncesThatTheButtonShouldTurnOff()
    {
        /*
         * NOT the same as asserting CanSetDefault is false -- reading the property recomputes it, so
         * that assertion passes whether or not anything was ever announced. The XAML binds IsEnabled
         * to this property, so without the notification the button stays live after being pressed.
         * The headless window test is what found this; this is the cheaper guard against it returning.
         */
        var vm = new AccountsViewModel(NewList()) { OfflineName = "Steve" };

        vm.AddOffline();
        vm.Selected = vm.Accounts[0];

        var announced = false;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanSetDefault))
            {
                announced = true;
            }
        };

        vm.SetDefault();

        Assert.True(announced);
    }

    [Fact]
    public void RemovingTheSelectedAccountLeavesNothingSelected()
    {
        var vm = new AccountsViewModel(NewList()) { OfflineName = "Steve" };

        vm.AddOffline();
        vm.Selected = vm.Accounts[0];
        vm.Remove();

        Assert.Empty(vm.Accounts);
        Assert.Null(vm.Selected);
        Assert.False(vm.CanRemove);
    }

    [Fact]
    public void RemovingOneAccountKeepsAnotherSelected()
    {
        /*
         * The list is rebuilt wholesale when it changes, so the selection has to be re-found by
         * identity. Without that, deleting one account silently deselects the one you were looking at
         * -- which the mods page got wrong earlier in this port for exactly the same reason.
         */
        var list = NewList();
        var vm = new AccountsViewModel(list) { OfflineName = "Steve" };

        vm.AddOffline();
        vm.OfflineName = "Alex";
        vm.AddOffline();

        vm.Selected = vm.Accounts[1];

        var kept = vm.Accounts[1].Account;

        list.Remove(vm.Accounts[0].Account);

        Assert.Single(vm.Accounts);
        Assert.Same(kept, vm.Selected?.Account);
    }

    [Fact]
    public async Task ASuccessfulSignInShowsTheCodeThenTheName()
    {
        var seen = new List<string>();

        using var client = new HttpClient(new MicrosoftStub());

        var vm = new AccountsViewModel(NewList(), client, "client-id");

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.PendingCode) && vm.PendingCode is { } code)
            {
                seen.Add(code.UserCode);
            }
        };

        Assert.True(vm.CanSignIn);

        await vm.SignInAsync();

        // The user was shown a code to type. Without this the dialog is a spinner that never explains
        // itself, which is the single most common way a device-code flow is got wrong.
        Assert.Contains("ABCD-EFGH", seen);

        // The status is carried into the failure message on purpose: when this breaks it is almost
        // always the chain refusing a response, and the reason is the whole diagnosis.
        Assert.True(vm.Accounts.Count == 1, "no account was added; status was: " + vm.Status);
        Assert.Equal("Alex", vm.Accounts[0].ProfileName);
        Assert.Equal("Microsoft", vm.Accounts[0].TypeLabel);
        Assert.Contains("Signed in as Alex", vm.Status, StringComparison.Ordinal);

        // Cleared once finished, or the code stays on screen after it stops working.
        Assert.Null(vm.PendingCode);
        Assert.False(vm.IsSigningIn);
    }

    [Fact]
    public async Task TheFirstAccountSignedInBecomesTheDefault()
    {
        // Otherwise somebody signs in and nothing appears to have happened.
        using var client = new HttpClient(new MicrosoftStub());

        var vm = new AccountsViewModel(NewList(), client, "client-id");

        await vm.SignInAsync();

        Assert.NotNull(vm.DefaultAccount);
        Assert.True(vm.Accounts[0].IsDefault);
    }

    [Fact]
    public async Task TheBrowserIsOpenedAtTheVerificationUrl()
    {
        var opened = new List<string>();

        using var client = new HttpClient(new MicrosoftStub());

        var vm = new AccountsViewModel(
            NewList(),
            client,
            "client-id",
            openBrowser: url =>
            {
                opened.Add(url);

                return Task.CompletedTask;
            });

        await vm.SignInAsync();

        Assert.Contains("https://microsoft.com/link", opened);
    }

    [Fact]
    public async Task AFailedSignInSaysWhyAndAddsNothing()
    {
        using var client = new HttpClient(new FailingStub());

        var vm = new AccountsViewModel(NewList(), client, "client-id");

        await vm.SignInAsync();

        Assert.Empty(vm.Accounts);
        Assert.False(vm.IsSigningIn);

        // Something to read, not an empty box.
        Assert.NotEqual(string.Empty, vm.Status);
    }

    [Fact]
    public async Task SigningInIsNotOfferedTwiceAtOnce()
    {
        using var client = new HttpClient(new MicrosoftStub());

        var vm = new AccountsViewModel(NewList(), client, "client-id");

        var running = vm.SignInAsync();

        // Whether or not it has finished by now, the flag must never leave the button live mid-flight.
        if (vm.IsSigningIn)
        {
            Assert.False(vm.CanSignIn);
        }

        await running;

        Assert.True(vm.CanSignIn);
    }

    /// <summary>Microsoft, as far as a device-code sign-in can tell.</summary>
    private sealed class MicrosoftStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            var body = url switch
            {
                var u when u.Contains("devicecode", StringComparison.Ordinal) => new JsonObject
                {
                    ["device_code"] = "device-code",
                    ["user_code"] = "ABCD-EFGH",
                    ["verification_uri"] = "https://microsoft.com/link",
                    ["expires_in"] = 900,
                    ["interval"] = 1,
                },

                var u when u.Contains("/token", StringComparison.Ordinal) => new JsonObject
                {
                    ["access_token"] = "msa-access-token",
                    ["refresh_token"] = "msa-refresh-token",
                    ["expires_in"] = 3600,
                },

                /*
                 * IssueInstant and NotAfter are NOT optional -- the parser rejects the whole response
                 * without them, and the failure reads "response could not be understood", which sounds
                 * like a launcher bug rather than a stub missing two fields. It cost me one debugging
                 * round here; it would cost rather more against the real service.
                 */
                var u when u.Contains("user.auth.xboxlive", StringComparison.Ordinal)
                    || u.Contains("xsts.auth.xboxlive", StringComparison.Ordinal) => new JsonObject
                {
                    ["IssueInstant"] = "2026-08-20T12:00:00.0000000Z",
                    ["NotAfter"] = "2036-08-20T12:00:00.0000000Z",
                    ["Token"] = "xbox-token",
                    ["DisplayClaims"] = new JsonObject
                    {
                        ["xui"] = new JsonArray(new JsonObject { ["uhs"] = "user-hash", ["gtg"] = "Alex" }),
                    },
                },

                // "username" here is an internal id, not the player name -- the parser only checks it
                // is present, and the name the player sees comes from the profile call further down.
                var u when u.Contains("launcher/login", StringComparison.Ordinal) => new JsonObject
                {
                    ["username"] = "0123456789abcdef0123456789abcdef",
                    ["access_token"] = "minecraft-token",
                    ["expires_in"] = 86400,
                },

                var u when u.Contains("profile.xboxlive.com", StringComparison.Ordinal) => new JsonObject
                {
                    ["profileUsers"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "xbox-user-id",
                            ["settings"] = new JsonArray(
                                new JsonObject { ["id"] = "Gamertag", ["value"] = "Alex" }),
                        }),
                },

                var u when u.Contains("entitlements", StringComparison.Ordinal) => new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject { ["name"] = "product_minecraft" },
                        new JsonObject { ["name"] = "game_minecraft" }),
                },

                var u when u.Contains("minecraft/profile", StringComparison.Ordinal) => new JsonObject
                {
                    ["id"] = "0123456789abcdef0123456789abcdef",
                    ["name"] = "Alex",
                    ["skins"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "skin-id",
                            ["state"] = "ACTIVE",
                            ["url"] = "https://textures.invalid/skin.png",
                            ["variant"] = "CLASSIC",
                        }),
                    ["capes"] = new JsonArray(),
                },

                _ => new JsonObject(),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FailingStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":"invalid_client","error_description":"That client id is not known."}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
