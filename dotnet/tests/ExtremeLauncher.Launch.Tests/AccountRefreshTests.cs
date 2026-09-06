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
 * The rules about when a stored sign-in is good enough to launch with.
 *
 * Most of these are about NOT doing something: not refreshing a token that is fine, not prompting
 * somebody who is merely offline, not trusting a token with no known expiry.
 */

using System.Net;
using System.Text;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AccountRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static AccountData Fresh(TimeSpan? expiresIn = null)
    {
        var data = new AccountData { Type = AccountType.Msa, MsaClientId = "client" };

        data.MsaToken.RefreshToken = "refresh";
        data.MsaToken.NotAfter = Now + (expiresIn ?? TimeSpan.FromHours(10));
        data.YggdrasilToken.Value = "game-token";
        data.YggdrasilToken.Validity = Validity.Certain;

        return data;
    }

    [Fact]
    public void ATokenWithPlentyOfLifeIsNotRefreshed()
    {
        Assert.False(AccountRefresh.NeedsRefresh(Fresh(), Now));
    }

    [Fact]
    public void AnExpiredTokenIsRefreshed()
    {
        Assert.True(AccountRefresh.NeedsRefresh(Fresh(TimeSpan.FromHours(-1)), Now));
    }

    [Fact]
    public void ATokenAboutToExpireIsRefreshedEarly()
    {
        /*
         * Valid, and useless: the game may still be loading when it dies. This margin is the
         * difference between a clean launch and a failure that looks like the server's fault.
         */
        Assert.True(AccountRefresh.NeedsRefresh(Fresh(TimeSpan.FromSeconds(30)), Now));
    }

    [Fact]
    public void ATokenWithNoKnownExpiryIsRefreshed()
    {
        // Treating "unknown" as "fine" is how a launcher hands the game a token that died months ago.
        var data = Fresh();

        data.MsaToken.NotAfter = null;

        Assert.True(AccountRefresh.NeedsRefresh(data, Now));
    }

    [Fact]
    public void AnAccountWithNoGameTokenIsRefreshed()
    {
        var data = Fresh();

        data.YggdrasilToken.Value = string.Empty;

        Assert.True(AccountRefresh.NeedsRefresh(data, Now));
    }

    [Fact]
    public async Task AnOfflineAccountIsAlreadyReady()
    {
        using var client = new HttpClient(new UnreachableHandler());

        var result = await AccountRefresh.PrepareAsync(
            MinecraftAccount.CreateOffline("Player"),
            client,
            "client",
            Now,
            CancellationToken.None);

        // No network touched: there is nothing about an offline account that could expire.
        Assert.Equal(AccountReadiness.Ready, result.Readiness);
    }

    [Fact]
    public async Task AGoodTokenIsUsedWithoutTouchingTheNetwork()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        CopyInto(account, Fresh());

        // An unreachable client proves the point: if this passes, nothing was sent.
        using var client = new HttpClient(new UnreachableHandler());

        var result = await AccountRefresh.PrepareAsync(account, client, "client", Now, CancellationToken.None);

        Assert.Equal(AccountReadiness.Ready, result.Readiness);
    }

    [Fact]
    public async Task BeingOfflineIsReportedAsOfflineNotAsNeedingSignIn()
    {
        /*
         * The distinction that matters on a train. A stored token may well still be good; what failed
         * was the checking. Prompting for a sign-in that cannot possibly succeed is the wrong answer.
         */
        var account = MinecraftAccount.CreateBlankMsa();

        CopyInto(account, Fresh(TimeSpan.FromHours(-1)));

        using var client = new HttpClient(new UnreachableHandler());

        var result = await AccountRefresh.PrepareAsync(account, client, "client", Now, CancellationToken.None);

        Assert.Equal(AccountReadiness.Offline, result.Readiness);
        Assert.False(result.CanPlayOnline);
    }

    [Fact]
    public async Task ARevokedTokenAsksForASignIn()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        CopyInto(account, Fresh(TimeSpan.FromHours(-1)));

        using var client = new HttpClient(
            new StubHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token revoked."}"""));

        var result = await AccountRefresh.PrepareAsync(account, client, "client", Now, CancellationToken.None);

        Assert.Equal(AccountReadiness.NeedsSignIn, result.Readiness);

        // The server's own reason, not a generic one: "Token revoked" tells the user what happened.
        Assert.Contains("revoked", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithNoClientIdConfiguredItAsksForASignInRatherThanPretending()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        CopyInto(account, Fresh(TimeSpan.FromHours(-1)));

        using var client = new HttpClient(new UnreachableHandler());

        var result = await AccountRefresh.PrepareAsync(account, client, string.Empty, Now, CancellationToken.None);

        Assert.Equal(AccountReadiness.NeedsSignIn, result.Readiness);
        Assert.Contains("not configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyInto(MinecraftAccount account, AccountData source)
    {
        account.Data.Type = source.Type;
        account.Data.MsaClientId = source.MsaClientId;
        account.Data.MsaToken.RefreshToken = source.MsaToken.RefreshToken;
        account.Data.MsaToken.NotAfter = source.MsaToken.NotAfter;
        account.Data.YggdrasilToken.Value = source.YggdrasilToken.Value;
        account.Data.YggdrasilToken.Validity = source.YggdrasilToken.Validity;
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("No such host is known.");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
