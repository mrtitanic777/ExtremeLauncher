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
 * Ported from launcher/minecraft/auth/steps/{XboxUser,XboxAuthorization,LauncherLogin,XboxProfile,
 * Entitlements,MinecraftProfile,GetSkin}Step.cpp.
 *
 * The service calls between "we have a Microsoft token" and "we have a playable account":
 *
 *   MSA token -> Xbox user token -> XSTS token (twice, for two relying parties)
 *             -> Minecraft access token -> entitlements -> profile -> skin
 *
 * Each step reads what the ones before it left in AccountData and writes its own piece back. They are
 * ordered, not independent.
 */

using System.Globalization;
using System.Text.Json.Nodes;

namespace ExtremeLauncher.Minecraft.Auth;

/// <summary>Trades the Microsoft token for an Xbox Live user token.</summary>
public sealed class XboxUserStep : AuthStep
{
    private readonly HttpClient _client;

    public XboxUserStep(AccountData data, HttpClient client) : base(data) => _client = client;

    public override string Describe => "Logging in as an Xbox user.";

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        var body = $$"""
            {
                "Properties": {
                    "AuthMethod": "RPS",
                    "SiteName": "user.auth.xboxlive.com",
                    "RpsTicket": "d={{Data.MsaToken.Value}}"
                },
                "RelyingParty": "http://auth.xboxlive.com",
                "TokenType": "JWT"
            }
            """;

        var response = await AuthHttp.PostAsync(
            _client,
            new Uri("https://user.auth.xboxlive.com/user/authenticate"),
            body,
            "application/json",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Accept"] = "application/json",

                // Prevents a 400 Bad Request. See Microsoft's "HTTP standard headers" GDK reference.
                ["x-xbl-contract-version"] = "1",
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.Successful)
        {
            return response.IsApplicationError
                ? AuthStepResult.Soft($"XBox user authentication failed: {response.ErrorString}")
                : AuthStepResult.Offline($"XBox user authentication failed: {response.ErrorString}");
        }

        var token = new Token();

        if (!Parsers.ParseXTokenResponse(response.Body, token))
        {
            return AuthStepResult.Soft("XBox user authentication response could not be understood.");
        }

        Data.UserToken = token;
        return AuthStepResult.Working("Got Xbox user token");
    }
}

/// <summary>
/// Exchanges the Xbox user token for an XSTS token scoped to one relying party.
/// </summary>
/// <remarks>
/// Run twice in the chain, for two different audiences: Xbox's own services, and Mojang's. The two
/// tokens are kept separately because the later steps need different ones.
/// </remarks>
public sealed class XboxAuthorizationStep : AuthStep
{
    private readonly HttpClient _client;
    private readonly Func<AccountData, Token> _target;
    private readonly string _relyingParty;
    private readonly string _authorizationKind;

    public XboxAuthorizationStep(
        AccountData data,
        HttpClient client,
        Func<AccountData, Token> target,
        string relyingParty,
        string authorizationKind)
        : base(data)
    {
        _client = client;
        _target = target;
        _relyingParty = relyingParty;
        _authorizationKind = authorizationKind;
    }

    public override string Describe => $"Getting authorization to access {_authorizationKind} services.";

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        var body = $$"""
            {
                "Properties": {
                    "SandboxId": "RETAIL",
                    "UserTokens": [
                        "{{Data.UserToken.Value}}"
                    ]
                },
                "RelyingParty": "{{_relyingParty}}",
                "TokenType": "JWT"
            }
            """;

        var response = await AuthHttp.PostAsync(
            _client,
            new Uri("https://xsts.auth.xboxlive.com/xsts/authorize"),
            body,
            "application/json",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Accept"] = "application/json" },
            cancellationToken).ConfigureAwait(false);

        if (!response.Successful)
        {
            if (!response.IsApplicationError)
            {
                return AuthStepResult.Offline(
                    $"Failed to get authorization for {_authorizationKind} services: {response.ErrorString}");
            }

            return DescribeStsError(response)
                   ?? AuthStepResult.Soft(
                       $"Failed to get authorization for {_authorizationKind} services. {response.ErrorString}.");
        }

        var token = new Token();

        if (!Parsers.ParseXTokenResponse(response.Body, token))
        {
            return AuthStepResult.Soft($"Could not parse authorization response for access to {_authorizationKind} services.");
        }

        // The user hash identifies who the token is for. If it changed between the user token and this
        // one, the two are not about the same person and nothing built on them can be trusted.
        if (token.Extra.GetValueOrDefault("uhs", string.Empty) != Data.UserToken.Extra.GetValueOrDefault("uhs", string.Empty))
        {
            return AuthStepResult.Soft(
                $"Server has changed {_authorizationKind} authorization user hash in the reply. Something is wrong.");
        }

        var target = _target(Data);

        target.Value = token.Value;
        target.RefreshToken = token.RefreshToken;
        target.IssueInstant = token.IssueInstant;
        target.NotAfter = token.NotAfter;
        target.Validity = token.Validity;
        target.Extra.Clear();

        foreach (var (key, value) in token.Extra)
        {
            target.Extra[key] = value;
        }

        return AuthStepResult.Working($"Got authorization to access {_relyingParty}");
    }

    /// <summary>
    /// Turns an XSTS refusal into something the user can act on.
    /// </summary>
    /// <returns><see langword="null"/> when the response carries no recognizable XErr code.</returns>
    /// <remarks>
    /// UPSTREAM BUG (#7), FIXED HERE. `processSTSError()` returns true when it has already emitted a
    /// specific, actionable message — and the caller's branches are the wrong way round, so a
    /// recognized error emits its real explanation and then immediately emits "Unknown STS error"
    /// on top of it. The user ends up told nothing useful precisely when the launcher knew exactly
    /// what was wrong. The signal-based double-emit has no equivalent in a step that returns one
    /// result, so this cannot be reproduced structurally; reproducing its visible effect would mean
    /// deliberately throwing away the good message. Fixed instead: the specific message wins.
    /// </remarks>
    private AuthStepResult? DescribeStsError(AuthResponse response)
    {
        // Only a 401 carries an XErr body; anything else is a plain refusal.
        if (response.Status != System.Net.HttpStatusCode.Unauthorized)
        {
            return null;
        }

        JsonNode? document;

        try
        {
            document = JsonNode.Parse(response.Body);
        }
        catch (System.Text.Json.JsonException e)
        {
            return AuthStepResult.Soft(
                $"Cannot parse {_authorizationKind} authorization error response as JSON: {e.Message}");
        }

        if (document is not JsonObject obj || !Parsers.GetNumber(obj["XErr"], out var code))
        {
            return AuthStepResult.Soft($"XErr element is missing from {_authorizationKind} authorization error response.");
        }

        return AuthStepResult.Soft((long)code switch
        {
            2148916233 =>
                "This Microsoft account does not have an XBox Live profile. Buy the game on "
                + "https://www.minecraft.net/en-us/store/minecraft-java-edition first.",

            // The "Grulovia" error, named for the fictional country in the original bug report.
            2148916235 => "XBox Live is not available in your country. You've been blocked.",

            2148916238 =>
                "This Microsoft account is underaged and is not linked to a family.\n\n"
                + "Please set up your account according to https://help.minecraft.net/hc/en-us/articles/4408968616077.",

            2148916236 =>
                "This Microsoft account requires proof of age to play. "
                + "Please login to https://login.live.com/login.srf to provide proof of age.",

            2148916237 =>
                "This Microsoft account has reached its limit for playtime. "
                + "This Microsoft account has been blocked from logging in.",

            2148916227 =>
                "This Microsoft account was banned by Xbox for violating one or more "
                + "Community Standards for Xbox and is unable to be used.",

            2148916229 =>
                "This Microsoft account is currently restricted and your guardian has not given you permission to play "
                + "online. Login to https://account.microsoft.com/family/ and have your guardian change your permissions.",

            2148916234 => "This Microsoft account has not accepted Xbox's Terms of Service. Please login and accept them.",

            var other =>
                "XSTS authentication ended with unrecognized error(s):\n\n"
                + other.ToString(CultureInfo.InvariantCulture),
        });
    }
}

/// <summary>Trades the Mojang-scoped XSTS token for the access token the game is launched with.</summary>
public sealed class LauncherLoginStep : AuthStep
{
    private readonly HttpClient _client;

    public LauncherLoginStep(AccountData data, HttpClient client) : base(data) => _client = client;

    public override string Describe => "Accessing Mojang services.";

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        var userHash = Data.MojangservicesToken.Extra.GetValueOrDefault("uhs", string.Empty);

        var body = $$"""
            {
                "xtoken": "XBL3.0 x={{userHash}};{{Data.MojangservicesToken.Value}}",
                "platform": "PC_LAUNCHER"
            }
            """;

        var response = await AuthHttp.PostAsync(
            _client,
            new Uri("https://api.minecraftservices.com/launcher/login"),
            body,
            "application/json",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Accept"] = "application/json" },
            cancellationToken).ConfigureAwait(false);

        if (!response.Successful)
        {
            return response.IsApplicationError
                ? AuthStepResult.Soft($"Failed to get Minecraft access token: {response.ErrorString}")
                : AuthStepResult.Offline($"Failed to get Minecraft access token: {response.ErrorString}");
        }

        return Parsers.ParseMojangResponse(response.Body, Data.YggdrasilToken)
            ? AuthStepResult.Working()
            : AuthStepResult.Soft("Failed to parse the Minecraft access token response.");
    }
}

/// <summary>
/// Fetches the Xbox profile.
/// </summary>
/// <remarks>
/// The response is DISCARDED — upstream logs it and does nothing else. The step is a liveness check on
/// the Xbox-scoped token, not a source of data: the gamertag the account list displays comes from the
/// "gtg" display claim on that token, which XboxAuthorizationStep already stored.
/// </remarks>
public sealed class XboxProfileStep : AuthStep
{
    private const string Settings =
        "GameDisplayName,AppDisplayName,AppDisplayPicRaw,GameDisplayPicRaw,"
        + "PublicGamerpic,ShowUserAsAvatar,Gamerscore,Gamertag,ModernGamertag,ModernGamertagSuffix,"
        + "UniqueModernGamertag,AccountTier,TenureLevel,XboxOneRep,"
        + "PreferredColor,Location,Bio,Watermarks,"
        + "RealName,RealNameOverride,IsQuarantined";

    private readonly HttpClient _client;

    public XboxProfileStep(AccountData data, HttpClient client) : base(data) => _client = client;

    public override string Describe => "Fetching Xbox profile.";

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        var userHash = Data.UserToken.Extra.GetValueOrDefault("uhs", string.Empty);

        var url = new Uri(
            "https://profile.xboxlive.com/users/me/profile/settings?settings=" + Uri.EscapeDataString(Settings));

        var response = await AuthHttp.GetAsync(
            _client,
            url,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Accept"] = "application/json",
                ["x-xbl-contract-version"] = "3",
                ["Authorization"] = $"XBL3.0 x={userHash};{Data.XboxApiToken.Value}",
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.Successful)
        {
            return response.IsApplicationError
                ? AuthStepResult.Soft($"Failed to retrieve the Xbox profile: {response.ErrorString}")
                : AuthStepResult.Offline($"Failed to retrieve the Xbox profile: {response.ErrorString}");
        }

        return AuthStepResult.Working("Got Xbox profile");
    }
}

/// <summary>
/// Asks what the account is entitled to.
/// </summary>
/// <remarks>
/// NEVER FAILS, inherited. Errors are not checked at all and a parse failure is ignored, so a request
/// that returns nothing usable leaves the entitlement at whatever it already was and the chain carries
/// on. That is defensible — ownership is re-derivable from whether a profile comes back — but it does
/// mean this step is silent about its own failures.
/// </remarks>
public sealed class EntitlementsStep : AuthStep
{
    private readonly HttpClient _client;

    public EntitlementsStep(AccountData data, HttpClient client) : base(data) => _client = client;

    public override string Describe => "Determining game ownership.";

    /// <summary>The id sent with the request, kept for correlation.</summary>
    public string RequestId { get; private set; } = string.Empty;

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        RequestId = Guid.NewGuid().ToString("D");

        var response = await AuthHttp.GetAsync(
            _client,
            new Uri("https://api.minecraftservices.com/entitlements/license?requestId=" + RequestId),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Accept"] = "application/json",
                ["Authorization"] = $"Bearer {Data.YggdrasilToken.Value}",
            },
            cancellationToken).ConfigureAwait(false);

        // NOT CHECKED, as upstream does not check it: no error handling, no presence check on the
        // request id that was just generated for correlation, no JWT validation.
        Parsers.ParseMinecraftEntitlements(response.Body, Data.Entitlement);

        return AuthStepResult.Working("Got entitlements");
    }
}

/// <summary>Fetches the player's name, skin and capes.</summary>
public sealed class MinecraftProfileStep : AuthStep
{
    private readonly HttpClient _client;

    public MinecraftProfileStep(AccountData data, HttpClient client) : base(data) => _client = client;

    public override string Describe => "Fetching the Minecraft profile.";

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        var response = await AuthHttp.GetAsync(
            _client,
            new Uri("https://api.minecraftservices.com/minecraft/profile"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Accept"] = "application/json",
                ["Authorization"] = $"Bearer {Data.YggdrasilToken.Value}",
            },
            cancellationToken).ConfigureAwait(false);

        // A 404 means the account exists and has simply never picked a name. That is a valid state to
        // finish in, not a failure — the launcher will offer to create one.
        if (response.Status == System.Net.HttpStatusCode.NotFound && response.Reached)
        {
            Data.Profile = new MinecraftProfile();
            return AuthStepResult.Working("Account has no Minecraft profile.");
        }

        if (!response.Successful)
        {
            return response.IsApplicationError
                ? AuthStepResult.Soft($"Minecraft Java profile acquisition failed: {response.ErrorString}")
                : AuthStepResult.Offline($"Minecraft Java profile acquisition failed: {response.ErrorString}");
        }

        if (!Parsers.ParseMinecraftProfile(response.Body, Data.Profile))
        {
            // Cleared rather than left half-written: the parsers stop where they fail, so what is in
            // there is a fragment of a profile rather than a profile.
            Data.Profile = new MinecraftProfile();
            return AuthStepResult.Soft("Minecraft Java profile response could not be parsed");
        }

        return AuthStepResult.Working("Minecraft Java profile acquisition succeeded.");
    }
}

/// <summary>
/// Downloads the skin image itself.
/// </summary>
/// <remarks>
/// NEVER FAILS, inherited: a skin that will not download leaves the account perfectly usable, so the
/// error is swallowed and the chain continues.
/// </remarks>
public sealed class GetSkinStep : AuthStep
{
    private readonly HttpClient _client;

    public GetSkinStep(AccountData data, HttpClient client) : base(data) => _client = client;

    public override string Describe => "Getting skin.";

    public override async Task<AuthStepResult> PerformAsync(CancellationToken cancellationToken)
    {
        // Upstream builds a QUrl from whatever is there, including an empty string, and lets the
        // request fail. Uri's constructor throws on that instead, so the emptiness is checked here.
        if (!Uri.TryCreate(Data.Profile.Skin.Url, UriKind.Absolute, out var url))
        {
            return AuthStepResult.Working("Got skin");
        }

        var response = await AuthHttp.GetAsync(_client, url, headers: null, cancellationToken).ConfigureAwait(false);

        if (response.Successful)
        {
            Data.Profile.Skin.Data = response.Body;
        }

        return AuthStepResult.Working("Got skin");
    }
}
