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
 * The contract from the SILENT half of launcher/minecraft/auth/steps/MSAStep.cpp.
 *
 * This is what makes a saved account worth saving. An access token lasts about a day; the refresh
 * token is what turns "you signed in once" into "you are still signed in next week". Without this step
 * accounts.json stores a refresh token that nothing ever spends, and every session starts with the
 * device-code dance again.
 */

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class MSARefreshStepTests
{
    private const string ClientId = "test-client-id";

    private static AccountData SignedInAccount(string refreshToken = "old-refresh-token")
    {
        var data = new AccountData { Type = AccountType.Msa, MsaClientId = ClientId };

        data.MsaToken.Value = "old-access-token";
        data.MsaToken.RefreshToken = refreshToken;
        data.MsaToken.Validity = Validity.Assumed;

        return data;
    }

    private static HttpClient Responding(HttpStatusCode status, string body, Action<string>? captureForm = null)
        => new(new StubHandler(status, body, captureForm));

    [Fact]
    public async Task ARefreshExchangesTheTokenAndKeepsTheNewOne()
    {
        var body = new JsonObject
        {
            ["access_token"] = "new-access-token",
            ["refresh_token"] = "new-refresh-token",
            ["expires_in"] = 3600,
        }.ToJsonString();

        var data = SignedInAccount();
        var step = new MSARefreshStep(data, Responding(HttpStatusCode.OK, body), ClientId);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("new-access-token", data.MsaToken.Value);

        /*
         * THE ROTATED REFRESH TOKEN MUST BE KEPT. Microsoft may hand back a new one, and the old one
         * can stop working the moment it does. Storing the response but keeping the old refresh token
         * would work for exactly one more session and then log the user out for no visible reason.
         */
        Assert.Equal("new-refresh-token", data.MsaToken.RefreshToken);
        Assert.Equal(Validity.Certain, data.MsaToken.Validity);
    }

    [Fact]
    public async Task ARefreshSendsTheRefreshTokenGrant()
    {
        var form = string.Empty;

        var body = new JsonObject
        {
            ["access_token"] = "a",
            ["refresh_token"] = "b",
            ["expires_in"] = 3600,
        }.ToJsonString();

        var data = SignedInAccount("the-stored-token");
        var step = new MSARefreshStep(data, Responding(HttpStatusCode.OK, body, f => form = f), ClientId);

        await step.PerformAsync(CancellationToken.None);

        Assert.Contains("grant_type=refresh_token", form, StringComparison.Ordinal);
        Assert.Contains("refresh_token=the-stored-token", form, StringComparison.Ordinal);
        Assert.Contains("client_id=" + ClientId, form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyRefreshTokenIsDisabledNotFailed()
    {
        var data = SignedInAccount(refreshToken: string.Empty);
        var step = new MSARefreshStep(data, Responding(HttpStatusCode.OK, "{}"), ClientId);

        var result = await step.PerformAsync(CancellationToken.None);

        /*
         * DISABLED, not a failure, and the difference is not cosmetic. A failure means "something went
         * wrong, try again"; Disabled means "this account needs you to sign in again". Reporting a
         * network-style failure here would have the user retrying forever.
         */
        Assert.Equal(AccountTaskState.Disabled, result.State);
    }

    [Fact]
    public async Task AChangedClientIdIsDisabled()
    {
        /*
         * The token was issued to a different application, so it cannot be refreshed by this one. It
         * happens for real: a fork that changes its client id invalidates every stored account.
         */
        var data = SignedInAccount();

        data.MsaClientId = "some-other-client-id";

        var step = new MSARefreshStep(data, Responding(HttpStatusCode.OK, "{}"), ClientId);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Disabled, result.State);
    }

    [Fact]
    public async Task ARejectedRefreshTokenIsAHardFailure()
    {
        /*
         * What an expired or revoked refresh token looks like. HARD rather than Disabled: upstream
         * reserves Disabled for what it can tell before asking, and routes every OAuth error response
         * to FAILED_HARD. Both mean the user has to sign in again; only Hard carries the server's
         * reason, which is the part worth showing them.
         */
        var body = new JsonObject
        {
            ["error"] = "invalid_grant",
            ["error_description"] = "The refresh token has expired.",
        }.ToJsonString();

        var data = SignedInAccount();
        var step = new MSARefreshStep(data, Responding(HttpStatusCode.BadRequest, body), ClientId);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedHard, result.State);
        Assert.Contains("expired", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AServerErrorIsASoftFailureSoItCanBeRetried()
    {
        /*
         * The other side of the line above. A 500 with no OAuth error in it is Microsoft having a bad
         * day, not the account being dead -- calling it Hard would sign the user out over a blip.
         */
        var data = SignedInAccount();
        var step = new MSARefreshStep(data, Responding(HttpStatusCode.InternalServerError, "nope"), ClientId);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);
    }

    [Fact]
    public async Task AFailedRefreshLeavesTheOldTokenAlone()
    {
        // So a retry still has something to try with.
        var data = SignedInAccount("keep-me");
        var step = new MSARefreshStep(data, Responding(HttpStatusCode.InternalServerError, "nope"), ClientId);

        await step.PerformAsync(CancellationToken.None);

        Assert.Equal("keep-me", data.MsaToken.RefreshToken);
    }

    [Fact]
    public async Task AResponseWithNoAccessTokenIsAFailure()
    {
        var body = new JsonObject { ["refresh_token"] = "b", ["expires_in"] = 3600 }.ToJsonString();

        var data = SignedInAccount();
        var step = new MSARefreshStep(data, Responding(HttpStatusCode.OK, body), ClientId);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);

        // Not half-applied: a token with no value is worse than the old one.
        Assert.Equal("old-access-token", data.MsaToken.Value);
    }

    [Fact]
    public async Task BeingUnableToReachMicrosoftIsOfflineNotAFailure()
    {
        /*
         * A laptop with no network must not cost anybody their account. Offline is its own state
         * precisely so the launcher can say "you are offline" instead of "your sign-in is broken".
         */
        var data = SignedInAccount();
        var step = new MSARefreshStep(data, new HttpClient(new UnreachableHandler()), ClientId);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Offline, result.State);
        Assert.Equal("old-refresh-token", data.MsaToken.RefreshToken);
    }

    [Fact]
    public async Task ARefreshWithNoNewRefreshTokenKeepsTheOldOne()
    {
        /*
         * Microsoft does not always rotate the refresh token. Assigning whatever came back would wipe a
         * perfectly good one whenever the response omits it -- and the account would then last exactly
         * as long as the new access token, which looks like a random sign-out a day later.
         */
        var body = new JsonObject
        {
            ["access_token"] = "new-access-token",
            ["expires_in"] = 3600,
        }.ToJsonString();

        var data = SignedInAccount("still-good");
        var step = new MSARefreshStep(data, Responding(HttpStatusCode.OK, body), ClientId);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("still-good", data.MsaToken.RefreshToken);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("No such host is known.");
    }

    private sealed class StubHandler(HttpStatusCode status, string body, Action<string>? captureForm)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (captureForm is not null && request.Content is not null)
            {
                captureForm(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
