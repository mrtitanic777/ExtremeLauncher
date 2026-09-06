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
 * Ported from the SILENT branch of launcher/minecraft/auth/steps/MSAStep.cpp.
 *
 * WHAT MAKES A SAVED ACCOUNT WORTH SAVING. An access token lasts about a day. The refresh token is
 * what turns "you signed in once" into "you are still signed in next week", and without this step
 * accounts.json stores one that nothing ever spends -- every session would start with the device-code
 * dance again, which is indistinguishable from not persisting accounts at all.
 *
 * Upstream gets the exchange from QOAuth2AuthorizationCodeFlow::refreshAccessToken(). There is no such
 * thing here, so the request is written out: it is one form post, and the awkward part was never the
 * HTTP but knowing which failures are worth retrying.
 *
 * WHICH FAILURE IS WHICH is the distinction this step exists to get right, and upstream's silent
 * branch draws the lines in a specific place that is worth copying exactly:
 *
 *   Disabled    checked before any request: no refresh token, or one issued to a different client id.
 *   FailedHard  the server answered with an OAuth error -- invalid_grant, which is what an expired or
 *               revoked refresh token looks like. The tokens are invalid; retrying cannot fix it.
 *   FailedSoft  the request failed some other way -- a 500, a body that made no sense. Might work next
 *               time, so the account is not written off.
 *   Offline     could not reach Microsoft at all. Says nothing about the account.
 *
 * Upstream's rule, from MSAStep.cpp: on the `error` signal (an OAuth error response) it is always
 * FAILED_HARD; on `requestFailed` while silent it is OFFLINE for a network error and FAILED_SOFT
 * otherwise. Getting these the wrong way round is a bug in both directions -- one signs the user out
 * over a transient blip, the other retries forever against a token that will never work again.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft.Auth;

public sealed class MSARefreshStep(AccountData data, HttpClient client, string clientId) : AuthStep(data)
{
    public override string Describe => "Refreshing your Microsoft sign-in.";

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        /*
         * The client id check comes FIRST, exactly as upstream orders it. A token issued to another
         * application cannot be refreshed by this one, and finding that out from the server costs a
         * round trip and returns a less clear error than the one we can give here.
         */
        if (!string.Equals(Data.MsaClientId, clientId, StringComparison.Ordinal))
        {
            return AuthStepResult.Disabled(
                "Microsoft user authentication failed - client identification has changed.");
        }

        if (Data.MsaToken.RefreshToken.Length == 0)
        {
            return AuthStepResult.Disabled("Microsoft user authentication failed - refresh token is empty.");
        }

        HttpResponseMessage response;
        byte[] payload;

        try
        {
            using var content = new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["client_id"] = clientId,
                    ["refresh_token"] = Data.MsaToken.RefreshToken,
                    ["grant_type"] = "refresh_token",
                    ["scope"] = MsaOAuth.Scope,
                });

            response = await client.PostAsync(new Uri(MsaOAuth.TokenUrl), content, cancellationToken)
                .ConfigureAwait(false);

            payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Offline, not failed: being unable to reach Microsoft says nothing about the account.
            return AuthStepResult.Offline($"Could not reach Microsoft to refresh your sign-in: {e.Message}");
        }

        using (response)
        {
            var body = MsaOAuth.ParseObject(payload);
            var error = MsaOAuth.ErrorOf(body);

            /*
             * AN OAUTH ERROR BODY IS ALWAYS HARD, whatever the status code carrying it -- upstream
             * routes its `error` signal to FAILED_HARD unconditionally. invalid_grant arrives this way
             * and means the refresh token is spent, which no amount of retrying will change.
             */
            if (error is not null)
            {
                return AuthStepResult.Hard(error);
            }

            if (!response.IsSuccessStatusCode)
            {
                // A failure with no OAuth error in it: the server having trouble, not the token.
                return AuthStepResult.Soft(
                    $"Microsoft could not refresh your sign-in ({(int)response.StatusCode}).");
            }

            if (Json.EnsureString(body, "access_token").Length == 0)
            {
                // NOT applied: a token with no value is worse than the one already stored.
                return AuthStepResult.Soft("Microsoft returned no access token.");
            }

            MsaOAuth.StoreToken(Data, body, clientId);

            return AuthStepResult.Working("Refreshed");
        }
    }

    public override string ToString() => nameof(MSARefreshStep);
}

/// <summary>
/// The parts of the Microsoft OAuth exchange that both the device-code and refresh steps need.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because <see cref="StoreToken"/> is the one place that decides what a
/// successful sign-in leaves behind -- including keeping a ROTATED refresh token, which is easy to get
/// wrong in one copy and not the other and shows up a week later as an unexplained sign-out.
/// </remarks>
internal static class MsaOAuth
{
    public const string Scope = "XboxLive.SignIn XboxLive.offline_access";

    public const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    public static void StoreToken(AccountData data, JsonObject token, string clientId)
    {
        var now = DateTimeOffset.UtcNow;

        data.MsaClientId = clientId;
        data.MsaToken.IssueInstant = now;

        // Upstream reads the LOCAL clock for this one line while using UTC for the line above it. Both
        // serialize to the same instant, so it is untidy rather than wrong; UTC is used throughout here.
        data.MsaToken.NotAfter = now.AddSeconds(Json.EnsureInteger(token, "expires_in"));

        /*
         * A ROTATED REFRESH TOKEN REPLACES THE OLD ONE, but an absent one does NOT clear it. Microsoft
         * may or may not return a new refresh token on a refresh; blindly assigning would wipe a
         * perfectly good token whenever the response omits it, and the account would then survive
         * exactly until the access token expired.
         */
        var refreshed = Json.EnsureString(token, "refresh_token");

        if (refreshed.Length != 0)
        {
            data.MsaToken.RefreshToken = refreshed;
        }

        data.MsaToken.Value = Json.EnsureString(token, "access_token");
        data.MsaToken.Validity = Validity.Certain;

        /*
         * Upstream stores the WHOLE response object as the token's extras, so the access and refresh
         * tokens end up stored twice — once in their own fields and once inside "extra", both of which
         * are written to accounts.json. Only string members are carried over here, because this port
         * models extras as strings (every key anything reads — uhs, gtg, xid — is one), which drops
         * the numeric expires_in. Nothing reads it from there.
         */
        data.MsaToken.Extra.Clear();

        foreach (var (key, value) in token)
        {
            if (value is JsonValue json && json.TryGetValue<string>(out var text))
            {
                data.MsaToken.Extra[key] = text;
            }
        }
    }

    /// <summary>The error to report, preferring the human-readable description.</summary>
    public static string? ErrorOf(JsonObject obj)
    {
        var error = Json.EnsureString(obj, "error");
        var description = Json.EnsureString(obj, "error_description");

        if (error.Length == 0 && description.Length == 0)
        {
            return null;
        }

        return description.Length != 0 ? description : error;
    }

    public static JsonObject ParseObject(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // An unparseable body reads as an empty object, so the caller falls through to its own
            // "required fields missing" and status-code handling rather than special-casing this.
            return [];
        }
    }
}
