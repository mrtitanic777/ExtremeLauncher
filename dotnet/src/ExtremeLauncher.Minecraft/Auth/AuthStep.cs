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
 * Ported from launcher/minecraft/auth/AuthStep.h and the request plumbing repeated across
 * launcher/minecraft/auth/steps/.
 *
 * One link in the authentication chain. Upstream a step is a QObject that emits finished(state,
 * message); here it is an awaitable returning that pair, which is the same contract without the
 * lifetime questions.
 *
 * WHY THESE STEPS DO NOT USE ExtremeLauncher.Net. Every other download in the port goes through
 * NetRequest, and these deliberately do not, for two reasons that are both upstream's:
 *
 *   - NetRequest DISCARDS THE BODY of a failed response, and these steps need it. XSTS returns its
 *     real diagnosis — "this account is underage and not linked to a family" — as an XErr code in the
 *     body of a 401, and the device-code flow reads "authorization_pending" out of the body of a 400.
 *     Routing auth through NetRequest would turn every one of those into a bare "authentication
 *     failed".
 *   - Upstream calls setAskRetry(false) on all of them: a login is interactive and time-boxed, so
 *     silently retrying is wrong. NetJob's retry machinery has nothing to offer here.
 */

using System.Net;
using System.Text;

namespace ExtremeLauncher.Minecraft.Auth;

/// <summary>
/// How far the chain got. Used to decide both whether to continue and what to tell the user.
/// </summary>
public enum AccountTaskState
{
    Created,

    Working,

    Succeeded,

    /// <summary>The MSA client id has changed; the user has to sign in again from scratch.</summary>
    Disabled,

    /// <summary>Soft failure: authentication went through partially.</summary>
    FailedSoft,

    /// <summary>Hard failure: the main tokens are invalid.</summary>
    FailedHard,

    /// <summary>Hard failure: the tokens are invalid and the account no longer exists.</summary>
    FailedGone,

    /// <summary>Soft failure: the first step could not reach the server at all.</summary>
    Offline,
}

/// <summary>What a step reports back when it finishes.</summary>
public readonly record struct AuthStepResult(AccountTaskState State, string Message)
{
    /// <summary>Carry on to the next step.</summary>
    public static AuthStepResult Working(string message = "") => new(AccountTaskState.Working, message);

    public static AuthStepResult Soft(string message) => new(AccountTaskState.FailedSoft, message);

    public static AuthStepResult Hard(string message) => new(AccountTaskState.FailedHard, message);

    public static AuthStepResult Offline(string message) => new(AccountTaskState.Offline, message);

    public static AuthStepResult Disabled(string message) => new(AccountTaskState.Disabled, message);
}

public abstract class AuthStep
{
    protected AuthStep(AccountData data) => Data = data;

    protected AccountData Data { get; }

    /// <summary>What to show the user while this step runs.</summary>
    public abstract string Describe { get; }

    public abstract Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of one HTTP call, including the body of a failed response.</summary>
/// <param name="Reached">Whether the server answered at all. False means a transport failure.</param>
public readonly record struct AuthResponse(bool Reached, HttpStatusCode Status, byte[] Body, string ErrorString)
{
    /// <summary>Whether the server answered with a success status.</summary>
    public bool Successful => Reached && (int)Status is >= 200 and < 300;

    /// <summary>
    /// Whether the failure came from the server rather than from the network.
    /// </summary>
    /// <remarks>
    /// Upstream's Net::isApplicationError, which lists the QNetworkReply errors that correspond to an
    /// HTTP status. The distinction decides the account's resting state: a server that answered with a
    /// refusal leaves the account Errored, while a network that never carried the question leaves it
    /// Offline — and only the second is worth retrying automatically.
    /// </remarks>
    public bool IsApplicationError => Reached && !Successful;
}

/// <summary>One request, one response, body kept either way.</summary>
public static class AuthHttp
{
    public static Task<AuthResponse> GetAsync(
        HttpClient client,
        Uri url,
        IEnumerable<KeyValuePair<string, string>>? headers,
        CancellationToken cancellationToken)
        => SendAsync(client, new HttpRequestMessage(HttpMethod.Get, url), headers, cancellationToken);

    public static Task<AuthResponse> PostAsync(
        HttpClient client,
        Uri url,
        string body,
        string contentType,
        IEnumerable<KeyValuePair<string, string>>? headers,
        CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        return SendAsync(client, new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, headers, cancellationToken);
    }

    private static async Task<AuthResponse> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        IEnumerable<KeyValuePair<string, string>>? headers,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            foreach (var (name, value) in headers ?? [])
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            try
            {
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                // Read unconditionally: the body of a 400 or 401 is where the real diagnosis lives.
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                return new AuthResponse(
                    Reached: true,
                    response.StatusCode,
                    body,
                    response.IsSuccessStatusCode ? string.Empty : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (HttpRequestException e)
            {
                return new AuthResponse(Reached: false, default, [], e.Message);
            }
            catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                // A timeout, which HttpClient reports as a cancellation of a token nobody cancelled.
                return new AuthResponse(Reached: false, default, [], e.Message);
            }
        }
    }
}
