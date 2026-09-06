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
 * Characterization tests for the device-code login. Upstream has no Qt test for it.
 *
 * The polling rules are RFC 8628's, and the interval arithmetic is the part worth pinning: polling
 * faster than the server allows gets the client throttled or blocked, and every one of these rules
 * exists to stop that happening. The delays are injected, so none of this waits on a real clock.
 */

using System.Net;
using System.Text;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class MSADeviceCodeStepTests
{
    private const string DeviceCodePath = "/devicecode";

    /// <summary>
    /// Answers each endpoint from its own script, repeating the last entry once the script runs out.
    /// </summary>
    /// <remarks>
    /// Responses are BUILT PER CALL rather than queued as instances: the step disposes each response
    /// it reads, so a repeated entry has to be a fresh object every time or the second read throws.
    /// </remarks>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        /// <summary>A queued answer: a status and body, or an exception standing in for a dead link.</summary>
        private readonly record struct Answer(HttpStatusCode Status, string Body, Exception? Failure);

        private readonly List<Answer> _deviceCode = [];
        private readonly List<Answer> _token = [];

        private int _deviceCodeIndex;
        private int _tokenIndex;

        public List<string> Log { get; } = [];

        public ScriptedHandler DeviceCode(HttpStatusCode status, string body)
        {
            _deviceCode.Add(new Answer(status, body, null));
            return this;
        }

        public ScriptedHandler Token(HttpStatusCode status, string body)
        {
            _token.Add(new Answer(status, body, null));
            return this;
        }

        /// <summary>Queues a connection failure rather than a response.</summary>
        public ScriptedHandler TokenTimeout()
        {
            _token.Add(new Answer(default, string.Empty, new HttpRequestException("The operation timed out.")));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var isDeviceCode = request.RequestUri!.AbsolutePath.EndsWith(DeviceCodePath, StringComparison.Ordinal);
            Log.Add(isDeviceCode ? "devicecode" : "token");

            var answer = isDeviceCode
                ? Next(_deviceCode, ref _deviceCodeIndex)
                : Next(_token, ref _tokenIndex);

            return answer.Failure is { } failure
                ? Task.FromException<HttpResponseMessage>(failure)
                : Task.FromResult(new HttpResponseMessage(answer.Status)
                {
                    Content = new StringContent(answer.Body, Encoding.UTF8),
                });
        }

        private static Answer Next(List<Answer> script, ref int index)
        {
            var answer = script[Math.Min(index, script.Count - 1)];
            index++;

            return answer;
        }
    }

    private const string GoodDeviceCode = """
        {
            "device_code": "the-device-code",
            "user_code": "ABCD-EFGH",
            "verification_uri": "https://microsoft.com/link",
            "expires_in": 900,
            "interval": 5
        }
        """;

    private const string GoodToken = """
        {
            "access_token": "the-access-token",
            "refresh_token": "the-refresh-token",
            "token_type": "Bearer",
            "expires_in": 3600
        }
        """;

    private static (MSADeviceCodeStep Step, List<TimeSpan> Delays, AccountData Data) Build(ScriptedHandler handler)
    {
        var data = new AccountData { Type = AccountType.Msa };
        List<TimeSpan> delays = [];

        var step = new MSADeviceCodeStep(
            data,
            new HttpClient(handler),
            "some-client-id",
            (span, _) =>
            {
                delays.Add(span);
                return Task.CompletedTask;
            });

        return (step, delays, data);
    }

    // ================================================================== the happy path

    [Fact]
    public async Task AFinishedLoginStoresTheMicrosoftToken()
    {
        var handler = new ScriptedHandler().DeviceCode(HttpStatusCode.OK, GoodDeviceCode).Token(HttpStatusCode.OK, GoodToken);
        var (step, _, data) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("the-access-token", data.MsaToken.Value);
        Assert.Equal("the-refresh-token", data.MsaToken.RefreshToken);
        Assert.Equal("some-client-id", data.MsaClientId);
        Assert.Equal(Validity.Certain, data.MsaToken.Validity);

        // The response carries a lifetime, not a deadline, so the clock is read on arrival.
        Assert.InRange(
            data.MsaToken.NotAfter!.Value,
            DateTimeOffset.UtcNow.AddSeconds(3_500),
            DateTimeOffset.UtcNow.AddSeconds(3_601));
    }

    [Fact]
    public async Task TheUserIsToldWhereToEnterTheCode()
    {
        var handler = new ScriptedHandler().DeviceCode(HttpStatusCode.OK, GoodDeviceCode).Token(HttpStatusCode.OK, GoodToken);
        var (step, _, _) = Build(handler);

        DeviceCodeInfo? announced = null;
        step.AuthorizeWithBrowser += (_, info) => announced = info;

        await step.PerformAsync(CancellationToken.None);

        Assert.NotNull(announced);
        Assert.Equal("https://microsoft.com/link", announced.VerificationUri);
        Assert.Equal("ABCD-EFGH", announced.UserCode);
        Assert.Equal(900, announced.ExpiresInSeconds);
    }

    [Fact]
    public async Task ExtraStringMembersOfTheResponseAreKept()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, _, data) = Build(handler);
        await step.PerformAsync(CancellationToken.None);

        // Upstream stores the whole response object, so the tokens end up on disk twice. Only string
        // members are carried here, which drops the numeric expires_in; nothing reads it from there.
        Assert.Equal("Bearer", data.MsaToken.Extra["token_type"]);
        Assert.False(data.MsaToken.Extra.ContainsKey("expires_in"));
    }

    // ================================================================== the polling rules

    [Fact]
    public async Task PendingAuthorizationJustKeepsWaiting()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .Token(HttpStatusCode.BadRequest, @"{ ""error"": ""authorization_pending"" }")
            .Token(HttpStatusCode.BadRequest, @"{ ""error"": ""authorization_pending"" }")
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, delays, data) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("the-access-token", data.MsaToken.Value);

        // Three polls at the server's interval, unchanged: the user simply has not finished typing.
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], delays);
        Assert.Equal(5, step.IntervalSeconds);
    }

    [Fact]
    public async Task SlowDownAddsFiveSecondsPermanently()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .Token(HttpStatusCode.BadRequest, @"{ ""error"": ""slow_down"" }")
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, delays, _) = Build(handler);
        await step.PerformAsync(CancellationToken.None);

        // RFC 8628 3.5: increased for this and ALL SUBSEQUENT requests, not just the next one.
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], delays);
        Assert.Equal(10, step.IntervalSeconds);
    }

    [Fact]
    public async Task RepeatedSlowDownsKeepAccumulating()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .Token(HttpStatusCode.BadRequest, @"{ ""error"": ""slow_down"" }")
            .Token(HttpStatusCode.BadRequest, @"{ ""error"": ""slow_down"" }")
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, delays, _) = Build(handler);
        await step.PerformAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)], delays);
    }

    [Fact]
    public async Task AConnectionTimeoutDoublesTheInterval()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .TokenTimeout()
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, delays, _) = Build(handler);
        await step.PerformAsync(CancellationToken.None);

        // RFC 8628 3.5 recommends exponential backoff on a connection timeout specifically.
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], delays);
        Assert.Equal(10, step.IntervalSeconds);
    }

    [Fact]
    public async Task AnOrdinaryServerFailureRetriesWithoutSlowingDown()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .Token(HttpStatusCode.InternalServerError, "{}")
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, delays, _) = Build(handler);
        await step.PerformAsync(CancellationToken.None);

        // The user may still be part-way through the browser flow, so it keeps trying at the same rate.
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], delays);
    }

    [Fact]
    public async Task TheServersIntervalIsHonouredOverTheDefault()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode.Replace(@"""interval"": 5", @"""interval"": 30", StringComparison.Ordinal))
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, delays, _) = Build(handler);
        await step.PerformAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(30)], delays);
    }

    [Fact]
    public async Task NoIntervalAtAllFallsBackToTheSpecsDefault()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, """
                {
                    "device_code": "d", "user_code": "u",
                    "verification_uri": "https://microsoft.com/link", "expires_in": 900
                }
                """)
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, delays, _) = Build(handler);
        await step.PerformAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(5)], delays);
    }

    // ================================================================== expiry

    [Fact]
    public async Task ACodeAboutToExpireIsReplacedRatherThanAbandoned()
    {
        var handler = new ScriptedHandler()

            // Three seconds left and a five-second interval: there is no time for another poll.
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode.Replace(@"""expires_in"": 900", @"""expires_in"": 3", StringComparison.Ordinal))
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .Token(HttpStatusCode.OK, GoodToken);

        var (step, _, data) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        // A user who has not finished in fifteen minutes gets a fresh code, not an error.
        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("the-access-token", data.MsaToken.Value);
        Assert.Equal(["devicecode", "devicecode", "token"], handler.Log);
    }

    // ================================================================== refusals

    [Fact]
    public async Task AnErrorFromTheDeviceCodeEndpointIsAHardFailure()
    {
        var handler = new ScriptedHandler().DeviceCode(
            HttpStatusCode.BadRequest,
            """{ "error": "unauthorized_client", "error_description": "The client does not exist." }""");

        var (step, _, _) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedHard, result.State);

        // The description is preferred over the code: it is the half a user can act on.
        Assert.Contains("The client does not exist.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheErrorBodyIsPreferredOverTheStatusCode()
    {
        // Checked before the status, deliberately: this endpoint reports a refused client id as a 400
        // whose body says exactly what is wrong, and the body is the better message.
        var handler = new ScriptedHandler().DeviceCode(HttpStatusCode.BadRequest, @"{ ""error"": ""invalid_scope"" }");
        var (step, _, _) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Contains("invalid_scope", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to retrieve", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureWithNoErrorBodyGetsTheGenericMessage()
    {
        var handler = new ScriptedHandler().DeviceCode(HttpStatusCode.ServiceUnavailable, "{}");
        var (step, _, _) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedHard, result.State);
        Assert.Contains("Failed to retrieve device authorization", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"{ ""user_code"": ""u"", ""verification_uri"": ""https://x"", ""expires_in"": 900 }")]
    [InlineData(@"{ ""device_code"": ""d"", ""verification_uri"": ""https://x"", ""expires_in"": 900 }")]
    [InlineData(@"{ ""device_code"": ""d"", ""user_code"": ""u"", ""expires_in"": 900 }")]
    [InlineData(@"{ ""device_code"": ""d"", ""user_code"": ""u"", ""verification_uri"": ""https://x"" }")]
    public async Task AnIncompleteDeviceAuthorizationIsAHardFailure(string body)
    {
        var handler = new ScriptedHandler().DeviceCode(HttpStatusCode.OK, body);
        var (step, _, _) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedHard, result.State);
        Assert.Contains("required fields missing", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnparseableDeviceAuthorizationReadsAsMissingFields()
    {
        var handler = new ScriptedHandler().DeviceCode(HttpStatusCode.OK, "<html>not json</html>");
        var (step, _, _) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedHard, result.State);
        Assert.Contains("required fields missing", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedLoginStopsThePolling()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .Token(HttpStatusCode.BadRequest, """{ "error": "expired_token", "error_description": "The code expired." }""");

        var (step, _, _) = Build(handler);

        var result = await step.PerformAsync(CancellationToken.None);

        // Not one of the two "keep waiting" codes, so it is a real refusal.
        Assert.Equal(AccountTaskState.FailedHard, result.State);
        Assert.Contains("The code expired.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRequestCarriesTheClientIdAndScope()
    {
        var handler = new BodyRecordingHandler();
        var data = new AccountData { Type = AccountType.Msa };

        var step = new MSADeviceCodeStep(data, new HttpClient(handler), "the client id", (_, _) => Task.CompletedTask);
        await step.PerformAsync(CancellationToken.None);

        // Form-encoded, and the client id is escaped rather than pasted in raw.
        Assert.Contains("client_id=the%20client%20id", handler.Body, StringComparison.Ordinal);
        Assert.Contains("XboxLive.SignIn", handler.Body, StringComparison.Ordinal);
        Assert.Contains("XboxLive.offline_access", handler.Body, StringComparison.Ordinal);
    }

    private sealed class BodyRecordingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false);

            // Enough to end the step immediately; only the request is under test.
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(@"{ ""error"": ""stop"" }", Encoding.UTF8),
            };
        }
    }

    // ================================================================== cancellation

    [Fact]
    public async Task CancellationEndsThePollingLoop()
    {
        var handler = new ScriptedHandler()
            .DeviceCode(HttpStatusCode.OK, GoodDeviceCode)
            .Token(HttpStatusCode.BadRequest, @"{ ""error"": ""authorization_pending"" }");

        using var cts = new CancellationTokenSource();
        var data = new AccountData { Type = AccountType.Msa };

        var polls = 0;

        var step = new MSADeviceCodeStep(
            data,
            new HttpClient(handler),
            "id",
            (_, _) =>
            {
                // A user who closes the dialog must not leave a loop polling Microsoft forever.
                if (++polls == 3)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.PerformAsync(cts.Token));
        Assert.Equal(3, polls);
    }
}
