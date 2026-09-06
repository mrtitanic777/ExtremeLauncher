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
 * Characterization tests for the auth response parsers. Upstream has no Qt test for any of this, and
 * the bodies below are the shapes documented in Parsers.cpp's own comments.
 *
 * These are the only part of the auth chain that can be tested without a live Microsoft login, which
 * is exactly why they were pulled across before the network steps.
 */

using System.Text;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class ParsersTests
{
    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
        => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond)).ToUniversalTime();

    // ================================================================== Xbox tokens

    private const string XTokenResponse = """
        {
           "IssueInstant": "2020-12-07T19:52:08.4463796Z",
           "NotAfter":     "2020-12-21T19:52:08.4463796Z",
           "Token":        "some-xbox-token",
           "DisplayClaims": { "xui": [ { "uhs": "userhash" } ] }
        }
        """;

    [Fact]
    public void AnXboxTokenIsReadWhole()
    {
        var token = new Token();

        Assert.True(Parsers.ParseXTokenResponse(Body(XTokenResponse), token));

        Assert.Equal("some-xbox-token", token.Value);
        Assert.Equal("userhash", token.Extra["uhs"]);
        Assert.Equal(Validity.Certain, token.Validity);

        // Seven fractional digits and a trailing Z, which is what Xbox actually sends. Compared to the
        // second: the sub-second part is real but nothing depends on it.
        Assert.Equal(
            new DateTimeOffset(2020, 12, 7, 19, 52, 8, TimeSpan.Zero),
            TruncateToSeconds(token.IssueInstant!.Value));

        Assert.Equal(
            new DateTimeOffset(2020, 12, 21, 19, 52, 8, TimeSpan.Zero),
            TruncateToSeconds(token.NotAfter!.Value));

        // The zone survives, rather than the timestamp being reinterpreted as local time.
        Assert.Equal(TimeSpan.Zero, token.IssueInstant!.Value.Offset);
    }

    [Fact]
    public void EveryDisplayClaimIsKept()
    {
        var token = new Token();

        Assert.True(Parsers.ParseXTokenResponse(
            Body("""
                {
                   "IssueInstant": "2020-12-07T19:52:08Z",
                   "NotAfter":     "2020-12-21T19:52:08Z",
                   "Token":        "t",
                   "DisplayClaims": { "xui": [ { "uhs": "userhash", "gtg": "Some Gamertag", "xid": "123" } ] }
                }
                """),
            token));

        // "Consume all display claims, whatever that means" — the gamertag arrives this way.
        Assert.Equal("Some Gamertag", token.Extra["gtg"]);
        Assert.Equal("123", token.Extra["xid"]);
    }

    [Fact]
    public void ClaimsBeforeTheFirstUserHashAreSkipped()
    {
        var token = new Token();

        Assert.True(Parsers.ParseXTokenResponse(
            Body("""
                {
                   "IssueInstant": "2020-12-07T19:52:08Z",
                   "NotAfter":     "2020-12-21T19:52:08Z",
                   "Token":        "t",
                   "DisplayClaims": { "xui": [ { "irrelevant": "x" }, { "uhs": "userhash" } ] }
                }
                """),
            token));

        Assert.Equal("userhash", token.Extra["uhs"]);
        Assert.False(token.Extra.ContainsKey("irrelevant"));
    }

    [Fact]
    public void AClaimWithNoUserHashAnywhereIsRefused()
    {
        // Without the user hash the token cannot be used to authorize anything.
        Assert.False(Parsers.ParseXTokenResponse(
            Body("""
                {
                   "IssueInstant": "2020-12-07T19:52:08Z",
                   "NotAfter":     "2020-12-21T19:52:08Z",
                   "Token":        "t",
                   "DisplayClaims": { "xui": [ { "gtg": "Some Gamertag" } ] }
                }
                """),
            new Token()));
    }

    [Fact]
    public void ANonStringDisplayClaimFailsTheWholeResponse()
    {
        // Not skipped: a claim of an unexpected type means the response is not what it claims to be.
        Assert.False(Parsers.ParseXTokenResponse(
            Body("""
                {
                   "IssueInstant": "2020-12-07T19:52:08Z",
                   "NotAfter":     "2020-12-21T19:52:08Z",
                   "Token":        "t",
                   "DisplayClaims": { "xui": [ { "uhs": "userhash", "xid": 123 } ] }
                }
                """),
            new Token()));
    }

    [Theory]
    [InlineData(@"{ ""NotAfter"": ""2020-12-21T19:52:08Z"", ""Token"": ""t"" }")]
    [InlineData(@"{ ""IssueInstant"": ""not a date"", ""NotAfter"": ""2020-12-21T19:52:08Z"", ""Token"": ""t"" }")]
    [InlineData(@"{ ""IssueInstant"": ""2020-12-07T19:52:08Z"", ""NotAfter"": ""2020-12-21T19:52:08Z"" }")]
    [InlineData(@"{ ""IssueInstant"": ""2020-12-07T19:52:08Z"", ""NotAfter"": ""2020-12-21T19:52:08Z"", ""Token"": 5 }")]
    public void AnIncompleteXboxTokenIsRefused(string json)
        => Assert.False(Parsers.ParseXTokenResponse(Body(json), new Token()));

    [Fact]
    public void AnXboxErrorBodyIsRefusedRatherThanRead()
    {
        // NOT HANDLED upstream either: 2148916238 means a child account not linked to a family. It
        // deserves a real message and currently surfaces as a bare parse failure.
        Assert.False(Parsers.ParseXTokenResponse(
            Body(@"{ ""Identity"": ""0"", ""XErr"": 2148916238, ""Message"": """" }"),
            new Token()));
    }

    [Fact]
    public void MalformedJsonIsAFailureRatherThanAnException()
    {
        // These bodies come off the network; a truncated response must not take the process down.
        Assert.False(Parsers.ParseXTokenResponse(Body("{ not json"), new Token()));
        Assert.False(Parsers.ParseMinecraftEntitlements(Body("<html>502 Bad Gateway</html>"), new MinecraftEntitlement()));
        Assert.False(Parsers.ParseMinecraftProfile([], new MinecraftProfile()));
    }

    [Fact]
    public void ABodyThatIsNotAnObjectReadsAsAnEmptyOne()
    {
        // Valid JSON, wrong shape: every lookup misses, so the required fields are absent.
        Assert.False(Parsers.ParseMinecraftProfile(Body("[1, 2, 3]"), new MinecraftProfile()));

        // Entitlements have no required fields, so the same body succeeds with nothing owned.
        var entitlement = new MinecraftEntitlement();
        Assert.True(Parsers.ParseMinecraftEntitlements(Body("[1, 2, 3]"), entitlement));
        Assert.False(entitlement.OwnsMinecraft);
    }

    // ================================================================== the profile endpoint

    private const string ProfileResponse = """
        {
            "id": "5627dd98e6be3c21b8a8e92344183641",
            "name": "Steve",
            "skins": [
                { "id": "old", "state": "INACTIVE", "url": "http://textures.minecraft.net/old", "variant": "classic" },
                { "id": "cur", "state": "ACTIVE",   "url": "http://textures.minecraft.net/cur", "variant": "slim" }
            ],
            "capes": [
                { "id": "migrator", "state": "INACTIVE", "url": "https://x/m", "alias": "Migrator" },
                { "id": "vanilla",  "state": "ACTIVE",   "url": "https://x/v", "alias": "Vanilla"  }
            ]
        }
        """;

    [Fact]
    public void AProfileIsReadWhole()
    {
        var profile = new MinecraftProfile();

        Assert.True(Parsers.ParseMinecraftProfile(Body(ProfileResponse), profile));

        Assert.Equal("5627dd98e6be3c21b8a8e92344183641", profile.Id);
        Assert.Equal("Steve", profile.Name);
        Assert.Equal(Validity.Certain, profile.Validity);

        // Only the active skin, and every cape.
        Assert.Equal("cur", profile.Skin.Id);
        Assert.Equal("slim", profile.Skin.Variant);
        Assert.Equal(2, profile.Capes.Count);
        Assert.Equal("vanilla", profile.CurrentCape);
    }

    [Fact]
    public void SkinUrlsAreUpgradedToHttps()
    {
        var profile = new MinecraftProfile();
        Parsers.ParseMinecraftProfile(Body(ProfileResponse), profile);

        // Mojang still serves these over plain http; the launcher will not fetch them that way.
        Assert.Equal("https://textures.minecraft.net/cur", profile.Skin.Url);
    }

    [Fact]
    public void OnlyTheTexturesHostIsRewritten()
    {
        var profile = new MinecraftProfile();

        Parsers.ParseMinecraftProfile(
            Body("""
                {
                    "id": "x", "name": "Steve",
                    "skins": [ { "id": "a", "state": "ACTIVE", "url": "http://example.com/s", "variant": "classic" } ],
                    "capes": []
                }
                """),
            profile);

        Assert.Equal("http://example.com/s", profile.Skin.Url);
    }

    [Fact]
    public void AMalformedSkinIsSkippedRatherThanFailingTheProfile()
    {
        var profile = new MinecraftProfile();

        // Losing a skin should not cost the user their name and capes.
        Assert.True(Parsers.ParseMinecraftProfile(
            Body("""
                {
                    "id": "x", "name": "Steve",
                    "skins": [ { "id": "a", "state": "ACTIVE", "url": 42, "variant": "classic" } ],
                    "capes": [ { "id": "c", "state": "ACTIVE", "url": "u", "alias": "C" } ]
                }
                """),
            profile));

        Assert.Equal(string.Empty, profile.Skin.Id);
        Assert.Single(profile.Capes);
    }

    [Fact]
    public void AProfileWithNoSkinsOrCapesIsStillAProfile()
    {
        var profile = new MinecraftProfile();

        Assert.True(Parsers.ParseMinecraftProfile(Body(@"{ ""id"": ""x"", ""name"": ""Steve"" }"), profile));

        Assert.Equal("Steve", profile.Name);
        Assert.Empty(profile.Capes);
    }

    [Theory]
    [InlineData(@"{ ""name"": ""Steve"" }")]
    [InlineData(@"{ ""id"": ""x"" }")]
    [InlineData(@"{ ""id"": 5, ""name"": ""Steve"" }")]
    public void AProfileWithoutIdOrNameIsRefused(string json)
        => Assert.False(Parsers.ParseMinecraftProfile(Body(json), new MinecraftProfile()));

    [Fact]
    public void AnActiveCapeMissingItsUrlLeavesCurrentCapeDangling()
    {
        var profile = new MinecraftProfile();

        Assert.True(Parsers.ParseMinecraftProfile(
            Body("""
                {
                    "id": "x", "name": "Steve",
                    "capes": [ { "id": "vanilla", "state": "ACTIVE", "alias": "Vanilla" } ]
                }
                """),
            profile));

        // UPSTREAM BUG (#6), reproduced: CurrentCape is set before the url is validated, so it names a
        // cape that never made it into the map. Contained rather than harmless — the account writer
        // drops an unknown CurrentCape on the way back in.
        Assert.Equal("vanilla", profile.CurrentCape);
        Assert.Empty(profile.Capes);
    }

    // ================================================================== the session server

    private static string SessionResponse(string id, string texturesJson)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(texturesJson));

        return $$"""
            {
                "id": "{{id}}",
                "name": "Steve",
                "properties": [ { "name": "textures", "value": "{{payload}}" } ]
            }
            """;
    }

    [Fact]
    public void ASessionProfileIsReadThroughItsBase64Payload()
    {
        var profile = new MinecraftProfile();

        Assert.True(Parsers.ParseMinecraftProfileMojang(
            Body(SessionResponse(
                "5627dd98e6be3c21b8a8e92344183641",
                """{ "textures": { "SKIN": { "url": "https://x/s" }, "CAPE": { "url": "https://x/c" } } }""")),
            profile));

        Assert.Equal("Steve", profile.Name);
        Assert.Equal("https://x/s", profile.Skin.Url);
        Assert.Equal(Validity.Certain, profile.Validity);

        // Keyed by the literal "cape", not by an id — the session server does not return one.
        Assert.Equal("cape", profile.CurrentCape);
        Assert.Equal("https://x/c", profile.Capes["cape"].Url);
    }

    [Fact]
    public void ASessionProfileWithNoCapeGetsNone()
    {
        var profile = new MinecraftProfile();

        Assert.True(Parsers.ParseMinecraftProfileMojang(
            Body(SessionResponse("x", """{ "textures": { "SKIN": { "url": "https://x/s" } } }""")),
            profile));

        Assert.Empty(profile.Capes);
        Assert.Equal(string.Empty, profile.CurrentCape);
    }

    [Fact]
    public void ASkinModelIsTakenFromItsMetadataWhenPresent()
    {
        var profile = new MinecraftProfile();

        Parsers.ParseMinecraftProfileMojang(
            Body(SessionResponse(
                "x",
                """{ "textures": { "SKIN": { "url": "u", "metadata": { "model": "slim" } } } }""")),
            profile);

        Assert.Equal("slim", profile.Skin.Variant);
    }

    [Fact]
    public void ASessionProfileWithNoTexturesAtAllFallsBackToADefaultSkin()
    {
        var profile = new MinecraftProfile();

        // A uuid whose Java hashCode is even, so Steve and the classic model.
        Assert.True(Parsers.ParseMinecraftProfileMojang(
            Body(SessionResponse("36532b5ec4423dbba24cc7e55d0f979a", """{ "textures": {} }""")),
            profile));

        Assert.Contains("1a4af718455d4aab", profile.Skin.Url, StringComparison.Ordinal);

        // Upper case here, lower case from the profile endpoint. Inherited inconsistency.
        Assert.Equal("CLASSIC", profile.Skin.Variant);
        Assert.Equal("00000000-0000-0000-0000-000000000000", profile.Skin.Id);

        // And the odd one gets Alex's slim model.
        var alex = new MinecraftProfile();
        Parsers.ParseMinecraftProfileMojang(
            Body(SessionResponse("5627dd98e6be3c21b8a8e92344183641", """{ "textures": {} }""")),
            alex);

        Assert.Contains("83cee5ca6afcdb17", alex.Skin.Url, StringComparison.Ordinal);
        Assert.Equal("SLIM", alex.Skin.Variant);
    }

    [Fact]
    public void AMissingTexturePayloadIsRefused()
    {
        Assert.False(Parsers.ParseMinecraftProfileMojang(
            Body(@"{ ""id"": ""x"", ""name"": ""Steve"", ""properties"": [] }"),
            new MinecraftProfile()));

        // A property by another name is not the one being looked for.
        Assert.False(Parsers.ParseMinecraftProfileMojang(
            Body(@"{ ""id"": ""x"", ""name"": ""Steve"", ""properties"": [ { ""name"": ""other"", ""value"": ""eyJ9"" } ] }"),
            new MinecraftProfile()));
    }

    [Fact]
    public void AnUndecodableTexturePayloadIsRefused()
        => Assert.False(Parsers.ParseMinecraftProfileMojang(
            Body(@"{ ""id"": ""x"", ""name"": ""Steve"", ""properties"": [ { ""name"": ""textures"", ""value"": ""!!!"" } ] }"),
            new MinecraftProfile()));

    [Fact]
    public void ASessionPayloadWithoutTexturesIsRefused()
        => Assert.False(Parsers.ParseMinecraftProfileMojang(
            Body(SessionResponse("x", """{ "profileName": "Steve" }""")),
            new MinecraftProfile()));

    [Fact]
    public void ANonObjectSessionBodyThrowsRatherThanReturningFalse()
    {
        // The one asymmetry in this file: this parser demands an object where the others tolerate
        // anything. A plain-text gateway error therefore leaves by a different door.
        Assert.Throws<JsonException>(
            () => Parsers.ParseMinecraftProfileMojang(Body("[1, 2, 3]"), new MinecraftProfile()));
    }

    // ================================================================== default model selection

    [Fact]
    public void TheDefaultModelComesFromTheUuidHashParity()
    {
        // Even Java hashCode is Steve, odd is Alex. The two 64-bit halves XORed, then folded to 32.

        // 0 ^ 0 = 0, even.
        Assert.True(Parsers.IsDefaultModelSteve("00000000000000000000000000000000"));

        // 0 ^ 1 = 1, odd.
        Assert.False(Parsers.IsDefaultModelSteve("00000000000000000000000000000001"));

        // The offline uuids for "Steve" and "Alex" — which have nothing to do with which model they
        // get. The player named Steve draws the Alex model, and that is correct.
        Assert.False(Parsers.IsDefaultModelSteve("5627dd98e6be3c21b8a8e92344183641"));
        Assert.True(Parsers.IsDefaultModelSteve("36532b5ec4423dbba24cc7e55d0f979a"));
    }

    [Fact]
    public void DashesInTheUuidDoNotChangeTheModel()
        => Assert.Equal(
            Parsers.IsDefaultModelSteve("5627dd98e6be3c21b8a8e92344183641"),
            Parsers.IsDefaultModelSteve("5627dd98-e6be-3c21-b8a8-e92344183641"));

    [Fact]
    public void AUuidOfTheWrongLengthGetsSteve()
    {
        // Nothing to hash, so the safe default. Also what a non-hex id gets: the halves parse as zero.
        Assert.True(Parsers.IsDefaultModelSteve("too-short"));
        Assert.True(Parsers.IsDefaultModelSteve(string.Empty));
        Assert.True(Parsers.IsDefaultModelSteve("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"));
    }

    // ================================================================== entitlements

    [Fact]
    public void EntitlementsAreReadFromTheItemsList()
    {
        var entitlement = new MinecraftEntitlement();

        Assert.True(Parsers.ParseMinecraftEntitlements(
            Body("""
                { "items": [ { "name": "product_minecraft" }, { "name": "game_minecraft" } ] }
                """),
            entitlement));

        Assert.True(entitlement.OwnsMinecraft);
        Assert.True(entitlement.CanPlayMinecraft);
        Assert.Equal(Validity.Certain, entitlement.Validity);
    }

    [Fact]
    public void OwningAndBeingAbleToPlayAreSeparate()
    {
        var entitlement = new MinecraftEntitlement();

        // A Game Pass subscriber can play without owning it.
        Parsers.ParseMinecraftEntitlements(Body(@"{ ""items"": [ { ""name"": ""game_minecraft"" } ] }"), entitlement);

        Assert.True(entitlement.CanPlayMinecraft);
        Assert.False(entitlement.OwnsMinecraft);
    }

    [Fact]
    public void AnEmptyEntitlementListIsAValidAnswer()
    {
        var entitlement = new MinecraftEntitlement { OwnsMinecraft = true, CanPlayMinecraft = true };

        // Succeeds, and clears what was there: the account genuinely owns nothing.
        Assert.True(Parsers.ParseMinecraftEntitlements(Body(@"{ ""items"": [] }"), entitlement));

        Assert.False(entitlement.OwnsMinecraft);
        Assert.False(entitlement.CanPlayMinecraft);
        Assert.Equal(Validity.Certain, entitlement.Validity);
    }

    [Fact]
    public void UnknownEntitlementsAreIgnored()
    {
        var entitlement = new MinecraftEntitlement();

        Assert.True(Parsers.ParseMinecraftEntitlements(
            Body(@"{ ""items"": [ { ""name"": ""product_minecraft_bedrock"" }, { ""id"": ""no name here"" } ] }"),
            entitlement));

        Assert.False(entitlement.OwnsMinecraft);
    }

    // ================================================================== rollout

    [Fact]
    public void TheRolloutFlagIsRead()
    {
        Assert.True(Parsers.ParseRolloutResponse(
            Body(@"{ ""feature"": ""msamigration"", ""rollout"": true }"),
            out var rollout));

        Assert.True(rollout);
    }

    [Fact]
    public void ARolloutForSomeOtherFeatureIsRefused()
    {
        // Reading another feature's flag as this one's would migrate accounts on someone else's say-so.
        Assert.False(Parsers.ParseRolloutResponse(
            Body(@"{ ""feature"": ""something-else"", ""rollout"": true }"),
            out var rollout));

        Assert.False(rollout);
    }

    [Fact]
    public void ARolloutThatIsNotABooleanIsRefused()
        => Assert.False(Parsers.ParseRolloutResponse(
            Body(@"{ ""feature"": ""msamigration"", ""rollout"": ""true"" }"),
            out _));

    // ================================================================== the launcher login

    [Fact]
    public void TheMojangTokenIsReadAndGivenAnExpiry()
    {
        var token = new Token();
        var before = DateTimeOffset.UtcNow;

        Assert.True(Parsers.ParseMojangResponse(
            Body("""
                { "username": "some-uuid", "access_token": "jwt-goes-here", "expires_in": 86400, "token_type": "Bearer" }
                """),
            token));

        Assert.Equal("jwt-goes-here", token.Value);
        Assert.Equal(Validity.Certain, token.Validity);

        // The response carries a lifetime, not a deadline, so the clock is read here.
        Assert.InRange(token.NotAfter!.Value, before.AddSeconds(86_400), DateTimeOffset.UtcNow.AddSeconds(86_401));
    }

    [Fact]
    public void TheMojangResponseMustCarryAUsernameEvenThoughItIsDiscarded()
    {
        // A well-formedness check on the response, not a source of the account's name.
        Assert.False(Parsers.ParseMojangResponse(
            Body(@"{ ""access_token"": ""jwt"", ""expires_in"": 86400 }"),
            new Token()));
    }

    [Theory]
    [InlineData(@"{ ""username"": ""u"", ""access_token"": ""jwt"" }")]
    [InlineData(@"{ ""username"": ""u"", ""access_token"": ""jwt"", ""expires_in"": ""86400"" }")]
    [InlineData(@"{ ""username"": ""u"", ""expires_in"": 86400 }")]
    public void AnIncompleteMojangResponseIsRefused(string json)
        => Assert.False(Parsers.ParseMojangResponse(Body(json), new Token()));
}
