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
 * Characterization tests for the account model. Upstream has no Qt test for any of this.
 *
 * The offline UUID vectors below were computed independently of the implementation — MD5 of
 * "OfflinePlayer:<name>" with the version and variant bits set per RFC 4122 — precisely because a
 * byte-swapped UUID is indistinguishable from a correct one by inspection. They are the contract with
 * the vanilla server, not with this code.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class AccountTests
{
    // ================================================================== offline identity

    [Theory]
    [InlineData("Steve", "5627dd98-e6be-3c21-b8a8-e92344183641")]
    [InlineData("Alex", "36532b5e-c442-3dbb-a24c-c7e55d0f979a")]
    [InlineData("Notch", "b50ad385-829d-3141-a216-7e7d7539ba7f")]
    [InlineData("mrtitanic777", "49589ef9-1216-30b1-a59b-c895b75ed8d1")]
    [InlineData("", "fc5bc365-aedf-30a8-8b89-04e462e29bde")]
    public void OfflineUuidsMatchWhatAVanillaServerWouldAssign(string username, string expected)
        => Assert.Equal(expected, MinecraftAccount.UuidFromUsername(username).ToString("D"));

    [Fact]
    public void OfflineUuidsAreVersionThreeIetfVariant()
    {
        // The bit surgery, checked directly rather than through a vector: a UUID with the wrong
        // version is rejected by some servers outright.
        var bytes = MinecraftAccount.UuidFromUsername("Steve").ToByteArray(bigEndian: true);

        Assert.Equal(0x30, bytes[6] & 0xf0);
        Assert.Equal(0x80, bytes[8] & 0xc0);
    }

    [Fact]
    public void UsernamesAreTreatedAsUtf8()
    {
        // Not ASCII, not UTF-16. Java hashes the UTF-8 bytes, and a name with an accent in it has to
        // land on the same UUID here.
        Assert.Equal(
            "b5535370-74ec-3fe7-b71e-d028db6cef2b",
            MinecraftAccount.UuidFromUsername("Jörg").ToString("D"));
    }

    [Fact]
    public void TheSameNameAlwaysGivesTheSameUuid()
        => Assert.Equal(MinecraftAccount.UuidFromUsername("Steve"), MinecraftAccount.UuidFromUsername("Steve"));

    [Fact]
    public void UsernamesAreCaseSensitive()
        => Assert.NotEqual(MinecraftAccount.UuidFromUsername("Steve"), MinecraftAccount.UuidFromUsername("steve"));

    // ================================================================== offline accounts

    [Fact]
    public void AnOfflineAccountIsFullyFormed()
    {
        var account = MinecraftAccount.CreateOffline("Steve");

        Assert.Equal(AccountType.Offline, account.AccountType);
        Assert.Equal("Steve", account.ProfileName);
        Assert.Equal("offline", account.TypeString);

        // The placeholder token the game accepts when there is nothing to authenticate against.
        Assert.Equal("0", account.AccessToken);
        Assert.Equal(Validity.Certain, account.Data.YggdrasilToken.Validity);

        // Stored without dashes, which is the form the game is passed.
        Assert.Equal("5627dd98e6be3c21b8a8e92344183641", account.ProfileId);
        Assert.True(account.HasProfile);
    }

    [Fact]
    public void AnOfflineAccountNeverOwnsTheGame()
    {
        var account = MinecraftAccount.CreateOffline("Steve");

        // Even if something set the entitlement, the account type decides.
        account.Data.Entitlement.OwnsMinecraft = true;

        Assert.False(account.OwnsMinecraft);
    }

    [Fact]
    public void OfflineAccountsGetDistinctClientTokens()
    {
        var first = MinecraftAccount.CreateOffline("Steve").Data.YggdrasilToken.Extra["clientToken"];
        var second = MinecraftAccount.CreateOffline("Steve").Data.YggdrasilToken.Extra["clientToken"];

        Assert.NotEqual(first, second);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void OfflineAccountsHaveNoDisplayName()
    {
        // The account list shows the profile name for these; the display string names the account type.
        Assert.Equal("<Offline>", MinecraftAccount.CreateOffline("Steve").AccountDisplayString);
    }

    [Fact]
    public void AnMsaAccountShowsItsGamertag()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        Assert.Equal("Xbox profile missing", account.AccountDisplayString);

        account.Data.XboxApiToken.Extra["gtg"] = "Some Gamertag";
        Assert.Equal("Some Gamertag", account.AccountDisplayString);
    }

    [Fact]
    public void AnAccountWithoutAProfileIsNamedAfterItself()
        => Assert.Equal("No profile (Xbox profile missing)", MinecraftAccount.CreateBlankMsa().ProfileName);

    [Fact]
    public void EveryAccountGetsItsOwnInternalId()
        => Assert.NotEqual(MinecraftAccount.CreateBlankMsa().InternalId, MinecraftAccount.CreateBlankMsa().InternalId);

    // ================================================================== refreshing

    private static MinecraftAccount CertainAccount(TimeSpan? expiresIn = null, TimeSpan? issuedAgo = null)
    {
        var account = MinecraftAccount.CreateBlankMsa();
        account.Data.Validity = Validity.Certain;

        account.Data.YggdrasilToken.IssueInstant = DateTimeOffset.UtcNow - (issuedAgo ?? TimeSpan.Zero);

        if (expiresIn is { } lifetime)
        {
            account.Data.YggdrasilToken.NotAfter = DateTimeOffset.UtcNow + lifetime;
        }

        return account;
    }

    [Fact]
    public void AnAccountInUseIsNeverRefreshed()
    {
        // Refreshing would invalidate the token the running game is holding and drop the player from
        // whatever server they are on.
        var account = CertainAccount(expiresIn: TimeSpan.FromMinutes(1));
        account.IncrementUses();

        Assert.False(account.ShouldRefresh());
    }

    [Fact]
    public void AnAssumedAccountIsAlwaysRefreshed()
    {
        var account = MinecraftAccount.CreateBlankMsa();
        account.Data.Validity = Validity.Assumed;

        // Read from disk and not yet checked this session.
        Assert.True(account.ShouldRefresh());
    }

    [Fact]
    public void ABrokenAccountIsNeverRefreshed()
    {
        var account = MinecraftAccount.CreateBlankMsa();
        account.Data.Validity = Validity.None;

        Assert.False(account.ShouldRefresh());
    }

    [Fact]
    public void ATokenExpiringWithinTwelveHoursIsRefreshed()
        => Assert.True(CertainAccount(expiresIn: TimeSpan.FromHours(11)).ShouldRefresh());

    [Fact]
    public void ATokenWithLongerLeftIsLeftAlone()
        => Assert.False(CertainAccount(expiresIn: TimeSpan.FromHours(13)).ShouldRefresh());

    [Fact]
    public void AnExpiryIsAssumedTwentyFourHoursAfterIssueWhenNoneIsStored()
    {
        // Fresh token validity is 24 hours, so one issued 13 hours ago is inside the 12-hour window.
        Assert.True(CertainAccount(issuedAgo: TimeSpan.FromHours(13)).ShouldRefresh());
        Assert.False(CertainAccount(issuedAgo: TimeSpan.FromHours(11)).ShouldRefresh());
    }

    [Fact]
    public void AnAccountWithNoTimestampsAtAllIsLeftAlone()
    {
        var account = MinecraftAccount.CreateBlankMsa();
        account.Data.Validity = Validity.Certain;

        // Nothing says it has expired, so nothing justifies invalidating it.
        Assert.False(account.ShouldRefresh());
    }

    [Fact]
    public void ReleasingAnAccountThatIsNotInUseIsARefusal()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        Assert.Throws<InvalidOperationException>(account.DecrementUses);
    }

    [Fact]
    public void UseCountsNest()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        var changes = 0;
        account.Changed += (_, _) => changes++;

        account.IncrementUses();
        account.IncrementUses();
        account.DecrementUses();

        // Two games were running: still in use after one exits.
        Assert.True(account.IsInUse);
        Assert.Equal(1, account.Uses);

        account.DecrementUses();
        Assert.False(account.IsInUse);

        // Only the transitions are announced, not every increment.
        Assert.Equal(2, changes);
    }

    // ================================================================== sessions

    [Fact]
    public void AnOfflineAccountFillsAPlayableSession()
    {
        var session = MinecraftAccount.CreateOffline("Steve").CreateSession(wantsOnline: false);

        Assert.Equal(SessionStatus.PlayableOffline, session.Status);
        Assert.Equal("Steve", session.PlayerName);
        Assert.Equal("5627dd98e6be3c21b8a8e92344183641", session.Uuid);
        Assert.Equal("offline", session.UserType);
        Assert.Equal("token:0:5627dd98e6be3c21b8a8e92344183641", session.Session);
    }

    [Fact]
    public void AnAccountThatOwnsTheGameWithoutAProfileNeedsSetup()
    {
        var account = MinecraftAccount.CreateBlankMsa();
        account.Data.Entitlement.OwnsMinecraft = true;

        // Bought the game but never picked a name; the game cannot start until they do.
        Assert.Equal(SessionStatus.RequiresProfileSetup, account.CreateSession().Status);
    }

    [Fact]
    public void ASessionWithoutAProfileIdFallsBackToADerivedUuid()
    {
        var account = MinecraftAccount.CreateBlankMsa();
        account.Data.Profile.Name = "Steve";

        var session = account.CreateSession();

        Assert.Equal("5627dd98e6be3c21b8a8e92344183641", session.Uuid);
    }

    [Fact]
    public void ASessionWithNoTokenGetsTheDashPlaceholder()
    {
        // Not an empty string: the game splits this field and an empty one is a parse error.
        Assert.Equal("-", MinecraftAccount.CreateBlankMsa().CreateSession().Session);
    }

    [Fact]
    public void MakingASessionOfflineRequiresItToBePlayable()
    {
        var undetermined = new AuthSession();
        Assert.False(undetermined.MakeOffline("Steve"));

        var playable = MinecraftAccount.CreateOffline("Alex").CreateSession();
        Assert.True(playable.MakeOffline("Steve"));

        Assert.Equal("Steve", playable.PlayerName);
        Assert.Equal("0", playable.AccessToken);
        Assert.Equal("-", playable.Session);
        Assert.Equal(SessionStatus.PlayableOffline, playable.Status);
    }

    [Fact]
    public void ADemoSessionStillWantsTheNetwork()
    {
        var session = MinecraftAccount.CreateOffline("Steve").CreateSession();
        session.MakeDemo("Steve", "abc");

        Assert.True(session.Demo);
        Assert.False(session.WantsOnline);

        // PlayableOnline despite WantsOnline being false: the demo still downloads its assets.
        Assert.Equal(SessionStatus.PlayableOnline, session.Status);
    }

    [Fact]
    public void UserPropertiesAreAnEmptyObject()
    {
        // Empty, but not an empty string — old versions hand this straight to a JSON parser.
        Assert.Equal("{}", AuthSession.SerializeUserProperties());
    }

    // ================================================================== persistence

    private static JsonObject RoundTrip(MinecraftAccount account)
    {
        var saved = account.SaveToJson();

        // Through text, so nothing survives as a live object reference.
        return (JsonObject)JsonNode.Parse(saved.ToJsonString())!;
    }

    [Fact]
    public void AnOfflineAccountSurvivesARoundTrip()
    {
        var original = MinecraftAccount.CreateOffline("Steve");
        var restored = MinecraftAccount.LoadFromJsonV3(RoundTrip(original));

        Assert.NotNull(restored);
        Assert.Equal(AccountType.Offline, restored.AccountType);
        Assert.Equal("Steve", restored.ProfileName);
        Assert.Equal(original.ProfileId, restored.ProfileId);
        Assert.Equal("0", restored.AccessToken);
        Assert.Equal("Steve", restored.Data.YggdrasilToken.Extra["userName"]);
    }

    [Fact]
    public void ARestoredAccountIsAssumedRatherThanCertain()
    {
        var restored = MinecraftAccount.LoadFromJsonV3(RoundTrip(MinecraftAccount.CreateOffline("Steve")))!;

        // Nothing has been checked with a server this session, whatever was true when it was written.
        Assert.Equal(Validity.Assumed, restored.Data.Validity);
        Assert.Equal(Validity.Assumed, restored.Data.YggdrasilToken.Validity);
    }

    [Fact]
    public void AnUnrecognizedTypeIsRefusedRatherThanGuessedAt()
    {
        // Refusing leaves the entry untouched on disk instead of rewriting it as something it is not.
        Assert.Null(MinecraftAccount.LoadFromJsonV3(new JsonObject { ["type"] = "Mojang" }));
        Assert.Null(MinecraftAccount.LoadFromJsonV3(new JsonObject()));
        Assert.Null(MinecraftAccount.LoadFromJsonV3(new JsonObject { ["type"] = 3 }));
    }

    [Fact]
    public void ThePreSevenTwoOfflineTokenIsMigrated()
    {
        var account = MinecraftAccount.LoadFromJsonV3(new JsonObject
        {
            ["type"] = "Offline",
            ["ygg"] = new JsonObject { ["token"] = "offline" },
        })!;

        // Accounts written before 7.2 used "offline"; the game wants "0".
        Assert.Equal("0", account.AccessToken);
    }

    [Fact]
    public void AProfileIsRejectedWholeWhenAMandatoryFieldIsMissing()
    {
        var account = MinecraftAccount.LoadFromJsonV3(new JsonObject
        {
            ["type"] = "MSA",
            ["profile"] = new JsonObject
            {
                ["id"] = "abc",
                ["name"] = "Steve",

                // No "variant": the skin block is incomplete.
                ["skin"] = new JsonObject { ["id"] = "s", ["url"] = "http://example.com" },
                ["capes"] = new JsonArray(),
            },
        })!;

        // Half a profile would be written straight back out, losing the half that failed to parse.
        Assert.Equal(string.Empty, account.ProfileId);
        Assert.Equal(Validity.None, account.Data.Profile.Validity);
    }

    [Fact]
    public void CapesThatAreNotAnArrayRejectTheProfile()
    {
        var account = MinecraftAccount.LoadFromJsonV3(new JsonObject
        {
            ["type"] = "MSA",
            ["profile"] = new JsonObject
            {
                ["id"] = "abc",
                ["name"] = "Steve",
                ["skin"] = new JsonObject { ["id"] = "s", ["url"] = "u", ["variant"] = "classic" },
                ["capes"] = new JsonObject(),
            },
        })!;

        Assert.Equal(string.Empty, account.ProfileId);
    }

    [Fact]
    public void AnEquippedCapeMustBeOneTheProfileHas()
    {
        var account = MinecraftAccount.LoadFromJsonV3(new JsonObject
        {
            ["type"] = "MSA",
            ["profile"] = new JsonObject
            {
                ["id"] = "abc",
                ["name"] = "Steve",
                ["skin"] = new JsonObject { ["id"] = "s", ["url"] = "u", ["variant"] = "classic" },
                ["capes"] = new JsonArray(
                    new JsonObject { ["id"] = "migrator", ["url"] = "u", ["alias"] = "Migrator" }),
                ["cape"] = "some-cape-that-is-not-there",
            },
        })!;

        // The profile loads; only the impossible claim is dropped.
        Assert.Equal("abc", account.ProfileId);
        Assert.Equal(string.Empty, account.Data.Profile.CurrentCape);
        Assert.Single(account.Data.Profile.Capes);
    }

    [Fact]
    public void AnEquippedCapeThatIsPresentIsKept()
    {
        var account = MinecraftAccount.LoadFromJsonV3(new JsonObject
        {
            ["type"] = "MSA",
            ["profile"] = new JsonObject
            {
                ["id"] = "abc",
                ["name"] = "Steve",
                ["skin"] = new JsonObject { ["id"] = "s", ["url"] = "u", ["variant"] = "classic" },
                ["capes"] = new JsonArray(
                    new JsonObject { ["id"] = "migrator", ["url"] = "u", ["alias"] = "Migrator" }),
                ["cape"] = "migrator",
            },
        })!;

        Assert.Equal("migrator", account.Data.Profile.CurrentCape);
    }

    [Fact]
    public void AProfileWithoutAnEntitlementBlockIsAssumedToOwnTheGame()
    {
        var account = MinecraftAccount.LoadFromJsonV3(new JsonObject
        {
            ["type"] = "MSA",
            ["profile"] = new JsonObject
            {
                ["id"] = "abc",
                ["name"] = "Steve",
                ["skin"] = new JsonObject { ["id"] = "s", ["url"] = "u", ["variant"] = "classic" },
                ["capes"] = new JsonArray(),
            },
        })!;

        // The servers only hand out a profile to accounts that own the game.
        Assert.True(account.OwnsMinecraft);
        Assert.Equal(Validity.Assumed, account.Data.Entitlement.Validity);
    }

    [Fact]
    public void AnEntitlementOfTheWrongTypeIsNotBelieved()
    {
        var account = MinecraftAccount.LoadFromJsonV3(new JsonObject
        {
            ["type"] = "MSA",
            ["entitlement"] = new JsonObject
            {
                // Strings, not booleans. Coercing would turn a corrupt file into a confident claim.
                ["ownsMinecraft"] = "true",
                ["canPlayMinecraft"] = "true",
            },
        })!;

        Assert.False(account.OwnsMinecraft);
        Assert.Equal(Validity.None, account.Data.Entitlement.Validity);
    }

    [Fact]
    public void AnEmptyTokenIsNotWrittenAtAll()
    {
        var account = MinecraftAccount.CreateBlankMsa();
        account.Data.MsaToken.IssueInstant = DateTimeOffset.UtcNow;

        // Timestamps alone are not worth a block: it would reload as an empty stub either way.
        Assert.False(account.SaveToJson().ContainsKey("msa"));
    }

    [Fact]
    public void ANonPersistentTokenNeverReachesDisk()
    {
        var account = MinecraftAccount.CreateOffline("Steve");
        account.Data.YggdrasilToken.Persistent = false;

        Assert.False(account.SaveToJson().ContainsKey("ygg"));

        // The profile still is, so the account survives — just without its token.
        Assert.True(account.SaveToJson().ContainsKey("profile"));
    }

    [Fact]
    public void SkinAndCapeImagesRoundTripThroughBase64()
    {
        var account = MinecraftAccount.CreateOffline("Steve");
        account.Data.Profile.Skin.Data = [0x89, 0x50, 0x4e, 0x47];
        account.Data.Profile.Capes["migrator"] = new Cape
        {
            Id = "migrator",
            Url = "u",
            Alias = "Migrator",
            Data = [1, 2, 3],
        };

        var restored = MinecraftAccount.LoadFromJsonV3(RoundTrip(account))!;

        Assert.Equal([0x89, 0x50, 0x4e, 0x47], restored.Data.Profile.Skin.Data);
        Assert.Equal([1, 2, 3], restored.Data.Profile.Capes["migrator"].Data);
    }

    [Fact]
    public void AnMsaAccountKeepsItsTokenChain()
    {
        var account = MinecraftAccount.CreateBlankMsa();
        account.Data.MsaClientId = "some-client-id";
        account.Data.MsaToken.Value = "msa-token";
        account.Data.MsaToken.RefreshToken = "msa-refresh";
        account.Data.MsaToken.NotAfter = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        account.Data.XboxApiToken.Extra["gtg"] = "Some Gamertag";

        var restored = MinecraftAccount.LoadFromJsonV3(RoundTrip(account))!;

        Assert.Equal("some-client-id", restored.Data.MsaClientId);
        Assert.Equal("msa-token", restored.Data.MsaToken.Value);
        Assert.Equal("msa-refresh", restored.Data.MsaToken.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), restored.Data.MsaToken.NotAfter);
        Assert.Equal("Some Gamertag", restored.AccountDisplayString);
    }

    [Fact]
    public void OfflineAccountsDoNotWriteTheMsaTokenChain()
    {
        var account = MinecraftAccount.CreateOffline("Steve");
        account.Data.MsaToken.Value = "should-not-be-here";

        var saved = account.SaveToJson();

        Assert.False(saved.ContainsKey("msa"));
        Assert.False(saved.ContainsKey("msa-client-id"));
    }
}
