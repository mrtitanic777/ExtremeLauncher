// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2024 Trial97 <alexandru.tripon97@gmail.com>
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
 * Ported from launcher/minecraft/auth/steps/MSADeviceCodeStep.{h,cpp}.
 *
 * The OAuth 2.0 device authorization grant (RFC 8628): ask Microsoft for a short code, show it to the
 * user, and poll until they have typed it into a browser somewhere. This is the login flow that needs
 * no embedded browser and no loopback listener, which is why it is the one ported first — the
 * authorization-code flow in MSAStep.cpp is built on QOAuth2AuthorizationCodeFlow and a local HTTP
 * reply handler, and belongs with the UI wave.
 *
 * THE POLLING RULES ARE THE SPEC'S, and each has a reason worth keeping:
 *   - "authorization_pending" means keep waiting at the current interval.
 *   - "slow_down" means add five seconds, permanently, for this and every later request.
 *   - a connection timeout means DOUBLE the interval, likewise permanently.
 *   - any other transport failure means retry without changing the interval.
 * Polling faster than the server allows gets the client throttled or blocked outright.
 */

using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft.Auth;

/// <summary>What the user has to be shown to complete a device-code login.</summary>
public sealed record DeviceCodeInfo(string VerificationUri, string UserCode, int ExpiresInSeconds);

public sealed class MSADeviceCodeStep : AuthStep
{
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

    /// <summary>The spec's default, used when the server does not name one.</summary>
    private const int DefaultIntervalSeconds = 5;

    private readonly HttpClient _client;
    private readonly string _clientId;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private int _intervalSeconds = DefaultIntervalSeconds;

    public MSADeviceCodeStep(
        AccountData data,
        HttpClient client,
        string clientId,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(data)
    {
        _client = client;
        _clientId = clientId;

        // Injected so the polling loop can be tested without waiting out real intervals.
        _delay = delay ?? Task.Delay;
    }

    public override string Describe => "Logging in with Microsoft account (device code).";

    /// <summary>Raised once the code is known, so the user can be told where to enter it.</summary>
    public event EventHandler<DeviceCodeInfo>? AuthorizeWithBrowser;

    /// <summary>The interval currently being honoured, in seconds. Only grows.</summary>
    public int IntervalSeconds => _intervalSeconds;

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        /*
         * Upstream restarts the whole request when the code is about to expire — startPoolTimer calls
         * perform() rather than giving up, on the grounds that a user who has not finished in fifteen
         * minutes deserves a fresh code rather than an error. That is a loop here rather than a
         * re-entrant call, so the recursion depth does not grow with the number of expiries.
         */
        while (true)
        {
            var (result, retry) = await RequestAndPollAsync(cancellationToken).ConfigureAwait(false);

            if (!retry)
            {
                return result;
            }
        }
    }

    private async Task<(AuthStepResult Result, bool Retry)> RequestAndPollAsync(CancellationToken cancellationToken)
    {
        var response = await AuthHttp.PostAsync(
            _client,
            new Uri(DeviceCodeUrl),
            $"client_id={Uri.EscapeDataString(_clientId)}&scope={Uri.EscapeDataString(MsaOAuth.Scope)}",
            "application/x-www-form-urlencoded",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Accept"] = "application/json" },
            cancellationToken).ConfigureAwait(false);

        var authorization = ParseObject(response.Body);

        // Checked BEFORE the status code, and deliberately so: this endpoint reports a refused client
        // id as a 400 whose body says exactly what is wrong, and the body is the better message.
        if (ErrorOf(authorization) is { } authorizationError)
        {
            return (AuthStepResult.Hard($"Device authorization failed: {authorizationError}"), false);
        }

        if (!response.Successful)
        {
            return (AuthStepResult.Hard("Failed to retrieve device authorization"), false);
        }

        var deviceCode = Json.EnsureString(authorization, "device_code");
        var userCode = Json.EnsureString(authorization, "user_code");
        var verificationUri = Json.EnsureString(authorization, "verification_uri");
        var expiresIn = Json.EnsureInteger(authorization, "expires_in");

        if (deviceCode.Length == 0 || userCode.Length == 0 || verificationUri.Length == 0 || expiresIn == 0)
        {
            return (AuthStepResult.Hard("Device authorization failed: required fields missing"), false);
        }

        var interval = Json.EnsureInteger(authorization, "interval");

        if (interval != 0)
        {
            _intervalSeconds = interval;
        }

        AuthorizeWithBrowser?.Invoke(this, new DeviceCodeInfo(verificationUri, userCode, expiresIn));

        return await PollAsync(deviceCode, TimeSpan.FromSeconds(expiresIn), cancellationToken).ConfigureAwait(false);
    }

    private async Task<(AuthStepResult Result, bool Retry)> PollAsync(
        string deviceCode,
        TimeSpan expiresIn,
        CancellationToken cancellationToken)
    {
        // Measured rather than timed: the expiry is a deadline the server set, and the wall clock is
        // what it is measured against.
        var deadline = DateTimeOffset.UtcNow + expiresIn;

        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;

            // Not enough time left for another poll, so ask for a fresh code instead of burning the
            // last few seconds on a request that cannot succeed.
            if (remaining < TimeSpan.FromSeconds(_intervalSeconds))
            {
                return (default, true);
            }

            await _delay(TimeSpan.FromSeconds(_intervalSeconds), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var response = await AuthHttp.PostAsync(
                _client,
                new Uri(MsaOAuth.TokenUrl),
                $"client_id={Uri.EscapeDataString(_clientId)}"
                + "&grant_type=urn:ietf:params:oauth:grant-type:device_code"
                + $"&device_code={Uri.EscapeDataString(deviceCode)}",
                "application/x-www-form-urlencoded",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Accept"] = "application/json" },
                cancellationToken).ConfigureAwait(false);

            // A connection timeout. RFC 8628 §3.5: "clients MUST unilaterally reduce their polling
            // frequency before retrying", by doubling the interval.
            if (!response.Reached)
            {
                _intervalSeconds *= 2;
                continue;
            }

            var token = ParseObject(response.Body);
            var error = Json.EnsureString(token, "error");

            switch (error)
            {
                // §3.5: the interval MUST be increased by five seconds for this and all later requests.
                case "slow_down":
                    _intervalSeconds += 5;
                    continue;

                // §3.5: the user simply has not finished typing the code yet.
                case "authorization_pending":
                    continue;
            }

            if (ErrorOf(token) is { } tokenError)
            {
                return (AuthStepResult.Hard($"Device Access failed: {tokenError}"), false);
            }

            if (!response.Successful)
            {
                // Some other server-side failure. Retried at the current interval rather than
                // abandoned, because the user may still be part-way through the browser flow.
                continue;
            }

            StoreToken(token);
            return (AuthStepResult.Working("Got"), false);
        }
    }

    /// <remarks>
    /// The body lives in <see cref="MsaOAuth"/> because the refresh step needs exactly the same rules,
    /// and "what a successful sign-in leaves behind" is not a thing to have two copies of.
    /// </remarks>
    private void StoreToken(JsonObject token) => MsaOAuth.StoreToken(Data, token, _clientId);

    /// <summary>The error to report, preferring the human-readable description.</summary>
    private static string? ErrorOf(JsonObject obj) => MsaOAuth.ErrorOf(obj);

    private static JsonObject ParseObject(byte[] data) => MsaOAuth.ParseObject(data);

    public override string ToString()
        => $"{nameof(MSADeviceCodeStep)}(interval={_intervalSeconds.ToString(CultureInfo.InvariantCulture)}s)";
}
