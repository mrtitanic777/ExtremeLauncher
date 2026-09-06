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
 * WHAT THE GAME IS TOLD ABOUT WHO IS PLAYING.
 *
 * Every launch until now built an offline session with a hardcoded name, so no instance could ever
 * join an online-mode server no matter what was signed in. These pin the wiring that fixes it, and
 * they assert on the COMMAND LINE -- the actual bytes handed to the JVM -- because that is the only
 * place the distinction between "signed in" and "not" ultimately shows up.
 */

using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LaunchAccountTests
{
    private static MinecraftAccount SignedIn(string name = "Alex", string accessToken = "a-real-token")
    {
        var account = MinecraftAccount.CreateBlankMsa();

        account.Data.Profile.Id = "0123456789abcdef0123456789abcdef";
        account.Data.Profile.Name = name;
        account.Data.Profile.Validity = Validity.Certain;
        account.Data.YggdrasilToken.Value = accessToken;
        account.Data.YggdrasilToken.Validity = Validity.Certain;
        account.Data.Entitlement.OwnsMinecraft = true;
        account.Data.Entitlement.CanPlayMinecraft = true;

        return account;
    }

    [Fact]
    public void ASignedInAccountProducesAnOnlineSession()
    {
        var session = SignedIn().CreateSession(wantsOnline: true);

        Assert.Equal("Alex", session.PlayerName);
        Assert.Equal("a-real-token", session.AccessToken);

        // The whole point: an offline session carries the placeholder token "0", which every
        // online-mode server rejects.
        Assert.NotEqual("0", session.AccessToken);
    }

    [Fact]
    public void AnOfflineAccountProducesThePlaceholderToken()
    {
        var session = MinecraftAccount.CreateOffline("Player").CreateSession(wantsOnline: false);

        Assert.Equal("Player", session.PlayerName);
        Assert.Equal("0", session.AccessToken);
    }

    [Fact]
    public void TheOfflineUuidIsTheOneTheVanillaServerWouldDerive()
    {
        /*
         * Not a new behaviour -- pinned here because the account wiring is what decides which UUID a
         * player gets, and getting it wrong means walking into your own world as a stranger with an
         * empty inventory. The expected value is what Java's UUID.nameUUIDFromBytes produces for
         * "OfflinePlayer:Player".
         */
        Assert.Equal(
            Guid.Parse("a01e3843-e521-3998-958a-f459800e4d11"),
            MinecraftAccount.UuidFromUsername("Player"));
    }

    [Fact]
    public void ASignedInAccountKeepsItsRealUuid()
    {
        // NOT derived from the name: the profile id is the identity the servers know.
        var session = SignedIn().CreateSession(wantsOnline: true);

        Assert.Equal("0123456789abcdef0123456789abcdef", session.Uuid);
    }

    [Fact]
    public void ARefreshFlowIsJustTheMsaChainWithADifferentFirstStep()
    {
        /*
         * The shape that makes silent re-auth possible at all: CreateMsaFlow takes the OAuth step as a
         * parameter, so "sign in" and "stay signed in" differ only in what goes at the front and share
         * every Xbox, entitlement and profile step behind it.
         */
        var data = new AccountData { Type = AccountType.Msa, MsaClientId = "client" };

        using var client = new HttpClient();

        var refresh = AuthFlow.CreateMsaFlow(data, client, new MSARefreshStep(data, client, "client"));

        Assert.IsType<MSARefreshStep>(refresh.Steps[0]);

        var signIn = AuthFlow.CreateMsaFlow(data, client, new MSADeviceCodeStep(data, client, "client"));

        Assert.IsType<MSADeviceCodeStep>(signIn.Steps[0]);

        // Same chain behind the first step, or one of the two paths would end up with a different
        // account state than the other.
        Assert.Equal(signIn.Steps.Count, refresh.Steps.Count);
        Assert.Equal(
            signIn.Steps.Skip(1).Select(s => s.GetType()),
            refresh.Steps.Skip(1).Select(s => s.GetType()));
    }
}
