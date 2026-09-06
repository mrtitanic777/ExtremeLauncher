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
 * Characterization tests for the authentication chain. Upstream has no Qt test for any of it, and it
 * is not testable at all against the real services — every assertion here runs against a stub handler.
 *
 * The distinction these tests care most about is FAILED_SOFT versus OFFLINE. It decides whether the
 * account comes to rest Errored or Offline, which is the difference between "your login is broken" and
 * "you have no internet", and getting it backwards sends users to re-authenticate for no reason.
 */

using System.Net;
using System.Text;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.Tasks;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class AuthFlowTests
{
    // ================================================================== stub plumbing

    /// <summary>Answers requests from a routing table, or throws to simulate a dead network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return _respond(request);
        }
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string body = "")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new HttpClient(handler);
    }

    private static HttpClient Always(string body) => new(new StubHandler(_ => Ok(body)));

    private static HttpClient AlwaysFailing(HttpStatusCode status, string body = "")
        => new(new StubHandler(_ => Error(status, body)));

    /// <summary>A network that is not there at all, as opposed to a server that refuses.</summary>
    private static HttpClient Unreachable()
        => new(new ThrowingHandler(new HttpRequestException("No such host is known.")));

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromException<HttpResponseMessage>(_exception);
    }

    private const string XTokenBody = """
        {
           "IssueInstant": "2020-12-07T19:52:08Z",
           "NotAfter":     "2020-12-21T19:52:08Z",
           "Token":        "xbox-token",
           "DisplayClaims": { "xui": [ { "uhs": "userhash" } ] }
        }
        """;

    private static AccountData MsaAccount()
    {
        var data = new AccountData { Type = AccountType.Msa };
        data.MsaToken.Value = "msa-token";

        return data;
    }

    // ================================================================== XboxUserStep

    [Fact]
    public async Task TheXboxUserStepStoresTheTokenItGetsBack()
    {
        var data = MsaAccount();
        using var client = ClientFor(_ => Ok(XTokenBody), out var handler);

        var result = await new XboxUserStep(data, client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("xbox-token", data.UserToken.Value);
        Assert.Equal("userhash", data.UserToken.Extra["uhs"]);

        // The Microsoft token goes out as an RPS ticket, with the "d=" prefix the endpoint requires.
        Assert.Contains(@"""RpsTicket"": ""d=msa-token""", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("1", handler.Requests[0].Headers.GetValues("x-xbl-contract-version").Single());
    }

    [Fact]
    public async Task AServerThatRefusesLeavesTheAccountErrored()
    {
        using var client = AlwaysFailing(HttpStatusCode.BadRequest);

        var result = await new XboxUserStep(MsaAccount(), client).PerformAsync(CancellationToken.None);

        // The server answered; the answer was no. That is a broken login, not a broken network.
        Assert.Equal(AccountTaskState.FailedSoft, result.State);
    }

    [Fact]
    public async Task ANetworkThatIsNotThereLeavesTheAccountOffline()
    {
        using var client = Unreachable();

        var result = await new XboxUserStep(MsaAccount(), client).PerformAsync(CancellationToken.None);

        // Nothing was learned about the account, so it must not be marked as broken.
        Assert.Equal(AccountTaskState.Offline, result.State);
    }

    [Fact]
    public async Task AnUnreadableXboxResponseIsASoftFailure()
    {
        using var client = Always("{ \"nonsense\": true }");

        var result = await new XboxUserStep(MsaAccount(), client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);
    }

    // ================================================================== XboxAuthorizationStep

    private static XboxAuthorizationStep AuthorizationStep(AccountData data, HttpClient client)
        => new(data, client, d => d.XboxApiToken, "http://xboxlive.com", "Xbox");

    private static AccountData AccountWithUserToken(string userHash = "userhash")
    {
        var data = MsaAccount();
        data.UserToken.Value = "user-token";
        data.UserToken.Extra["uhs"] = userHash;

        return data;
    }

    [Fact]
    public async Task AnAuthorizationTokenLandsInItsOwnSlot()
    {
        var data = AccountWithUserToken();
        using var client = ClientFor(_ => Ok(XTokenBody), out var handler);

        var result = await AuthorizationStep(data, client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);

        // Into XboxApiToken, and nowhere else: the two relying parties get separate tokens.
        Assert.Equal("xbox-token", data.XboxApiToken.Value);
        Assert.Equal(string.Empty, data.MojangservicesToken.Value);
        Assert.Equal("userhash", data.XboxApiToken.Extra["uhs"]);

        Assert.Contains(@"""RelyingParty"": ""http://xboxlive.com""", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("user-token", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTwoRelyingPartiesFillDifferentTokens()
    {
        var data = AccountWithUserToken();
        using var client = Always(XTokenBody);

        await new XboxAuthorizationStep(data, client, d => d.MojangservicesToken, "rp://api.minecraftservices.com/", "Mojang")
            .PerformAsync(CancellationToken.None);

        Assert.Equal("xbox-token", data.MojangservicesToken.Value);
        Assert.Equal(string.Empty, data.XboxApiToken.Value);
    }

    [Fact]
    public async Task AChangedUserHashIsRefused()
    {
        // The reply is about a different person than the token that was sent. Nothing built on it
        // could be trusted, so the chain stops here rather than logging someone else in.
        var data = AccountWithUserToken("the-real-user");
        using var client = Always(XTokenBody);

        var result = await AuthorizationStep(data, client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);
        Assert.Contains("user hash", result.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, data.XboxApiToken.Value);
    }

    [Theory]
    [InlineData(2148916233, "does not have an XBox Live profile")]
    [InlineData(2148916235, "not available in your country")]
    [InlineData(2148916238, "underaged")]
    [InlineData(2148916236, "proof of age")]
    [InlineData(2148916237, "limit for playtime")]
    [InlineData(2148916227, "banned by Xbox")]
    [InlineData(2148916229, "guardian")]
    [InlineData(2148916234, "Terms of Service")]
    public async Task EachKnownXstsErrorGetsItsOwnExplanation(long code, string expected)
    {
        using var client = AlwaysFailing(HttpStatusCode.Unauthorized, $$"""{ "XErr": {{code}}, "Message": "" }""");

        var result = await AuthorizationStep(AccountWithUserToken(), client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);

        // UPSTREAM BUG (#7) FIXED: upstream emits this specific message and then immediately emits
        // "Unknown STS error" on top of it, so the user is told nothing useful exactly when the
        // launcher knew precisely what was wrong.
        Assert.DoesNotContain("Unknown STS error", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnrecognizedXstsCodeIsReportedWithItsNumber()
    {
        using var client = AlwaysFailing(HttpStatusCode.Unauthorized, @"{ ""XErr"": 1234567 }");

        var result = await AuthorizationStep(AccountWithUserToken(), client).PerformAsync(CancellationToken.None);

        // Not actionable, but at least searchable.
        Assert.Contains("1234567", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnXstsBodyWithNoErrorCodeSaysSo()
    {
        using var client = AlwaysFailing(HttpStatusCode.Unauthorized, @"{ ""Message"": ""nope"" }");

        var result = await AuthorizationStep(AccountWithUserToken(), client).PerformAsync(CancellationToken.None);

        Assert.Contains("XErr element is missing", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnparseableXstsErrorBodySaysThatInstead()
    {
        using var client = AlwaysFailing(HttpStatusCode.Unauthorized, "<html>not json</html>");

        var result = await AuthorizationStep(AccountWithUserToken(), client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);
        Assert.Contains("Cannot parse", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyA401CarriesAnXstsErrorCode()
    {
        // A 500 has no XErr body to read, so it falls through to the generic message.
        using var client = AlwaysFailing(HttpStatusCode.InternalServerError, @"{ ""XErr"": 2148916238 }");

        var result = await AuthorizationStep(AccountWithUserToken(), client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);
        Assert.Contains("Failed to get authorization", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("underaged", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableXstsServiceIsOfflineNotErrored()
    {
        using var client = Unreachable();

        var result = await AuthorizationStep(AccountWithUserToken(), client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Offline, result.State);
    }

    // ================================================================== LauncherLoginStep

    [Fact]
    public async Task TheLauncherLoginStepStoresTheGameToken()
    {
        var data = MsaAccount();
        data.MojangservicesToken.Value = "mojang-xsts";
        data.MojangservicesToken.Extra["uhs"] = "userhash";

        using var client = ClientFor(
            _ => Ok("""{ "username": "u", "access_token": "the-game-token", "expires_in": 86400 }"""),
            out var handler);

        var result = await new LauncherLoginStep(data, client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("the-game-token", data.YggdrasilToken.Value);

        // The Mojang-scoped XSTS token and its user hash, in the XBL3.0 form the endpoint wants.
        Assert.Contains(@"XBL3.0 x=userhash;mojang-xsts", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableLoginResponseIsASoftFailure()
    {
        using var client = Always("{}");

        var result = await new LauncherLoginStep(MsaAccount(), client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);
    }

    // ================================================================== XboxProfileStep

    [Fact]
    public async Task TheXboxProfileStepIsOnlyALivenessCheck()
    {
        var data = MsaAccount();
        data.UserToken.Extra["uhs"] = "userhash";
        data.XboxApiToken.Value = "xbox-api";

        using var client = ClientFor(_ => Ok(@"{ ""profileUsers"": [] }"), out var handler);

        var result = await new XboxProfileStep(data, client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("XBL3.0 x=userhash;xbox-api", handler.Requests[0].Headers.GetValues("Authorization").Single());

        // The body is read and discarded — nothing from it reaches the account. The gamertag comes
        // from the XSTS display claims that XboxAuthorizationStep already stored.
        Assert.Equal("Xbox profile missing", data.AccountDisplayString);
    }

    [Fact]
    public async Task AFailingXboxProfileStopsTheChain()
    {
        using var client = AlwaysFailing(HttpStatusCode.Forbidden);

        var result = await new XboxProfileStep(MsaAccount(), client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);
    }

    // ================================================================== EntitlementsStep

    [Fact]
    public async Task EntitlementsAreStoredWhenTheyArrive()
    {
        var data = MsaAccount();
        using var client = ClientFor(
            _ => Ok("""{ "items": [ { "name": "product_minecraft" }, { "name": "game_minecraft" } ] }"""),
            out var handler);

        var step = new EntitlementsStep(data, client);
        var result = await step.PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.True(data.Entitlement.OwnsMinecraft);

        // A fresh correlation id per request, sent in the query string.
        Assert.Contains(step.RequestId, handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEntitlementsStepNeverFails()
    {
        var data = MsaAccount();
        using var client = AlwaysFailing(HttpStatusCode.InternalServerError, "kaboom");

        // Inherited: errors are not checked at all. Ownership is re-derivable from whether a profile
        // comes back, so a failure here does not stop the chain.
        Assert.Equal(AccountTaskState.Working, (await new EntitlementsStep(data, client).PerformAsync(default)).State);

        using var dead = Unreachable();
        Assert.Equal(AccountTaskState.Working, (await new EntitlementsStep(data, dead).PerformAsync(default)).State);
    }

    // ================================================================== MinecraftProfileStep

    [Fact]
    public async Task AProfileIsStoredWhenItArrives()
    {
        var data = MsaAccount();
        using var client = Always("""
            { "id": "abc", "name": "Steve", "skins": [], "capes": [] }
            """);

        var result = await new MinecraftProfileStep(data, client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal("Steve", data.Profile.Name);
    }

    [Fact]
    public async Task AnAccountWithNoProfileIsAValidOutcome()
    {
        var data = MsaAccount();
        data.Profile.Id = "stale";

        using var client = AlwaysFailing(HttpStatusCode.NotFound);

        var result = await new MinecraftProfileStep(data, client).PerformAsync(CancellationToken.None);

        // A 404 means the account exists and has never picked a name. The chain continues.
        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal(string.Empty, data.Profile.Id);
    }

    [Fact]
    public async Task AnUnparseableProfileClearsWhatWasThere()
    {
        var data = MsaAccount();
        data.Profile.Id = "stale";
        data.Profile.Name = "Stale";

        using var client = Always(@"{ ""id"": 5 }");

        var result = await new MinecraftProfileStep(data, client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.FailedSoft, result.State);

        // Cleared, not left half-written: the parser stops where it fails, so what it leaves behind is
        // a fragment rather than a profile.
        Assert.Equal(string.Empty, data.Profile.Id);
        Assert.Equal(string.Empty, data.Profile.Name);
    }

    // ================================================================== GetSkinStep

    [Fact]
    public async Task TheSkinImageIsStored()
    {
        var data = MsaAccount();
        data.Profile.Skin.Url = "https://textures.invalid/skin.png";

        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x89, 0x50, 0x4e, 0x47]),
        }));

        var result = await new GetSkinStep(data, client).PerformAsync(CancellationToken.None);

        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Equal([0x89, 0x50, 0x4e, 0x47], data.Profile.Skin.Data);
    }

    [Fact]
    public async Task TheSkinStepNeverFails()
    {
        var data = MsaAccount();
        data.Profile.Skin.Url = "https://textures.invalid/skin.png";

        // An account with no skin is still perfectly playable.
        using var client = AlwaysFailing(HttpStatusCode.NotFound);
        Assert.Equal(AccountTaskState.Working, (await new GetSkinStep(data, client).PerformAsync(default)).State);
        Assert.Empty(data.Profile.Skin.Data);

        using var dead = Unreachable();
        Assert.Equal(AccountTaskState.Working, (await new GetSkinStep(data, dead).PerformAsync(default)).State);
    }

    [Fact]
    public async Task AnEmptySkinUrlIsNotRequestedAtAll()
    {
        var data = MsaAccount();
        using var client = ClientFor(_ => Ok("should not be reached"), out var handler);

        var result = await new GetSkinStep(data, client).PerformAsync(CancellationToken.None);

        // Upstream builds a QUrl from the empty string and lets the request fail; Uri throws on it, so
        // the emptiness is checked instead. Same outcome, no exception.
        Assert.Equal(AccountTaskState.Working, result.State);
        Assert.Empty(handler.Requests);
    }

    // ================================================================== the flow

    /// <summary>A step that reports whatever it is told to, and records that it ran.</summary>
    private sealed class ScriptedStep : AuthStep
    {
        private readonly AuthStepResult _result;
        private readonly List<string> _journal;

        public ScriptedStep(AccountData data, string name, List<string> journal, AuthStepResult? result = null)
            : base(data)
        {
            Describe = name;
            _journal = journal;
            _result = result ?? AuthStepResult.Working();
        }

        public override string Describe { get; }

        public override Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
        {
            _journal.Add(Describe);
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task EveryStepRunsInOrderAndTheAccountEndsUpOnline()
    {
        var data = MsaAccount();
        List<string> journal = [];

        var flow = new AuthFlow(data, [
            new ScriptedStep(data, "first", journal),
            new ScriptedStep(data, "second", journal),
        ]);

        Assert.True(await flow.RunAsync());

        Assert.Equal(["first", "second"], journal);
        Assert.Equal(AccountTaskState.Succeeded, flow.TaskState);
        Assert.Equal(AccountState.Online, data.AccountState);

        // Every step confirmed something, so the account is now known good rather than merely believed.
        Assert.Equal(Validity.Certain, data.Validity);
    }

    [Fact]
    public async Task TheChainStopsAtTheFirstStepThatDoesNotReportWorking()
    {
        var data = MsaAccount();
        List<string> journal = [];

        var flow = new AuthFlow(data, [
            new ScriptedStep(data, "first", journal),
            new ScriptedStep(data, "broken", journal, AuthStepResult.Soft("it broke")),
            new ScriptedStep(data, "never", journal),
        ]);

        Assert.False(await flow.RunAsync());

        Assert.Equal(["first", "broken"], journal);
        Assert.Equal("it broke", data.ErrorString);
        Assert.NotEqual(Validity.Certain, data.Validity);
    }

    [Theory]
    [InlineData(AccountTaskState.Offline, AccountState.Offline)]
    [InlineData(AccountTaskState.Disabled, AccountState.Disabled)]
    [InlineData(AccountTaskState.FailedSoft, AccountState.Errored)]
    [InlineData(AccountTaskState.FailedHard, AccountState.Expired)]
    [InlineData(AccountTaskState.FailedGone, AccountState.Gone)]
    public async Task EachFailureLeavesTheAccountInItsOwnRestingState(AccountTaskState state, AccountState expected)
    {
        var data = MsaAccount();

        // The account list shows the difference, and only one of these is worth retrying by itself.
        var flow = new AuthFlow(data, [new ScriptedStep(data, "x", [], new AuthStepResult(state, "why"))]);

        Assert.False(await flow.RunAsync());

        Assert.Equal(expected, data.AccountState);
        Assert.Equal("why", data.ErrorString);
        Assert.Equal(state, flow.TaskState);
    }

    [Fact]
    public async Task AnOfflineAccountHasNothingToCheckAndSoSucceeds()
    {
        var data = new AccountData { Type = AccountType.Offline };
        using var client = Unreachable();

        var flow = AuthFlow.CreateMsaFlow(data, client, new ScriptedStep(data, "oauth", []));

        // Surprising to read and correct in effect: an offline account gets no steps at all, so the
        // empty list runs to the end without incident. No network is touched.
        Assert.Empty(flow.Steps);
        Assert.True(await flow.RunAsync());
        Assert.Equal(AccountState.Online, data.AccountState);
    }

    [Fact]
    public void TheStandardChainIsInDependencyOrder()
    {
        var data = MsaAccount();
        using var client = Always("{}");

        var flow = AuthFlow.CreateMsaFlow(data, client, new ScriptedStep(data, "oauth", []));

        // Each step reads what the ones before it wrote. Reordering any of these breaks the next.
        Assert.Collection(
            flow.Steps,
            step => Assert.Equal("oauth", step.Describe),
            step => Assert.IsType<XboxUserStep>(step),
            step => Assert.Contains("Xbox services", step.Describe, StringComparison.Ordinal),
            step => Assert.Contains("Mojang services", step.Describe, StringComparison.Ordinal),
            step => Assert.IsType<LauncherLoginStep>(step),
            step => Assert.IsType<XboxProfileStep>(step),
            step => Assert.IsType<EntitlementsStep>(step),
            step => Assert.IsType<MinecraftProfileStep>(step),
            step => Assert.IsType<GetSkinStep>(step));
    }

    [Fact]
    public async Task ANewFlowStartsWithAClearedError()
    {
        var data = MsaAccount();
        data.ErrorString = "left over from last time";

        _ = new AuthFlow(data, []);

        // Cleared at construction, so a stale message never outlives the attempt that produced it.
        Assert.Equal(string.Empty, data.ErrorString);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task CancellingTheFlowReportsAsAborted()
    {
        var data = MsaAccount();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var flow = new AuthFlow(data, [new ScriptedStep(data, "x", [])]);

        Assert.False(await flow.RunAsync(cts.Token));
        Assert.Equal(TaskState.AbortedByUser, flow.State);
    }
}
