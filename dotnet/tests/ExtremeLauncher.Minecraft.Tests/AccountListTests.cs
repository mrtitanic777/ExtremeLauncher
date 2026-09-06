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
 * The contract from launcher/minecraft/auth/AccountList.cpp.
 *
 * ASSERTED ON THE FILE, not read back through AccountList, wherever the question is "what got written".
 * accounts.json is a format other things read -- upstream's own launcher among them -- so reading it
 * back through the writer would only prove the pair agree with each other.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class AccountListTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "el-accounts-" + Guid.NewGuid().ToString("N"));

    public AccountListTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ListPath => Path.Combine(_folder, "accounts.json");

    private static MinecraftAccount Signed(string profileId, string name, bool owns = true)
    {
        var account = MinecraftAccount.CreateBlankMsa();

        account.Data.Profile.Id = profileId;
        account.Data.Profile.Name = name;
        account.Data.Entitlement.OwnsMinecraft = owns;

        return account;
    }

    /// <summary>
    /// One account as it really appears in accounts.json.
    /// </summary>
    /// <remarks>
    /// The skin block is NOT optional: the parser drops a profile that has no complete skin, which is
    /// upstream's rule. My first draft of these fixtures left it out, and both accounts loaded with an
    /// empty profile id -- which looked exactly like the dedupe failing.
    /// </remarks>
    private static string AccountJson(string profileId, string name)
        => "{ \"type\": \"MSA\", \"profile\": { \"id\": \"" + profileId + "\", \"name\": \"" + name
            + "\", \"skin\": { \"id\": \"s\", \"url\": \"http://example.invalid/s.png\", "
            + "\"variant\": \"CLASSIC\" }, \"capes\": [] } }";

    [Fact]
    public void AnEmptyListSavesAFileThatLoadsBackEmpty()
    {
        var list = new AccountList(ListPath);

        Assert.True(list.Save());

        var root = JsonNode.Parse(File.ReadAllText(ListPath))!.AsObject();

        // formatVersion 3 is MojangMSA. A different number makes upstream rename the file.
        Assert.Equal(3, (int)root["formatVersion"]!);
        Assert.Empty(root["accounts"]!.AsArray());
    }

    [Fact]
    public void TheDefaultAccountIsTheOneMarkedActiveInTheFile()
    {
        var list = new AccountList(ListPath);

        var first = Signed("aaa", "Alex");
        var second = Signed("bbb", "Steve");

        list.Add(first);
        list.Add(second);
        list.DefaultAccount = second;

        Assert.True(list.Save());

        var accounts = JsonNode.Parse(File.ReadAllText(ListPath))!["accounts"]!.AsArray();

        Assert.Equal(2, accounts.Count);

        // "active" is written ONLY on the default -- upstream omits it entirely on the others
        // rather than writing false, and something reading the file may well test for presence.
        Assert.Null(accounts[0]!["active"]);
        Assert.True((bool)accounts[1]!["active"]!);
    }

    [Fact]
    public void ALoadedListRestoresTheDefault()
    {
        var saved = new AccountList(ListPath);

        saved.Add(Signed("aaa", "Alex"));
        saved.Add(Signed("bbb", "Steve"));
        saved.DefaultAccount = saved.Accounts[1];
        saved.Save();

        var loaded = new AccountList(ListPath);

        Assert.True(loaded.Load());
        Assert.Equal(2, loaded.Accounts.Count);
        Assert.Equal("Steve", loaded.DefaultAccount?.ProfileName);
    }

    [Fact]
    public void AnAccountWithTheSameProfileIdReplacesTheOldOne()
    {
        var list = new AccountList(ListPath);

        var old = Signed("aaa", "Alex");

        list.Add(old);
        list.DefaultAccount = old;

        var renewed = Signed("aaa", "AlexRenamed");

        list.Add(renewed);

        // Replaced in place, not appended: signing in again to an account you already have is the
        // normal way a token gets refreshed, and it must not double the row.
        Assert.Single(list.Accounts);
        Assert.Equal("AlexRenamed", list.Accounts[0].ProfileName);

        // ... and it inherits being the default, or signing in again would silently deselect you.
        Assert.Same(renewed, list.DefaultAccount);
    }

    [Fact]
    public void TwoAccountsWithNoProfileIdBothStay()
    {
        /*
         * An account mid-sign-in has no profile id yet. Upstream only dedupes on a NON-EMPTY id, so
         * these must not collapse into one -- otherwise starting a second sign-in would eat the first.
         */
        var list = new AccountList(ListPath);

        list.Add(MinecraftAccount.CreateBlankMsa());
        list.Add(MinecraftAccount.CreateBlankMsa());

        Assert.Equal(2, list.Accounts.Count);
    }

    [Fact]
    public void AddingTheSameAccountObjectTwiceIsRefused()
    {
        var list = new AccountList(ListPath);

        var account = Signed("aaa", "Alex");

        list.Add(account);
        list.Add(account);

        Assert.Single(list.Accounts);
    }

    [Fact]
    public void RemovingTheDefaultAccountLeavesNoDefault()
    {
        var list = new AccountList(ListPath);

        var account = Signed("aaa", "Alex");

        list.Add(account);
        list.DefaultAccount = account;

        list.Remove(account);

        Assert.Empty(list.Accounts);
        Assert.Null(list.DefaultAccount);
    }

    [Fact]
    public void RemovingANonDefaultAccountKeepsTheDefault()
    {
        var list = new AccountList(ListPath);

        var keep = Signed("aaa", "Alex");
        var drop = Signed("bbb", "Steve");

        list.Add(keep);
        list.Add(drop);
        list.DefaultAccount = keep;

        list.Remove(drop);

        Assert.Same(keep, list.DefaultAccount);
    }

    [Fact]
    public void ADuplicateProfileIdInTheFileIsIgnored()
    {
        // Hand-written rather than produced by Save, because Add already refuses to make one --
        // this is about a file that arrived corrupt, which is the only way it can happen.
        var json =
            "{ \"formatVersion\": 3, \"accounts\": ["
            + AccountJson("aaa", "Alex") + "," + AccountJson("aaa", "Impostor")
            + "] }";

        File.WriteAllText(ListPath, json);

        var list = new AccountList(ListPath);

        Assert.True(list.Load());
        Assert.Single(list.Accounts);
        Assert.Equal("Alex", list.Accounts[0].ProfileName);
    }

    [Fact]
    public void AnUnknownFormatVersionRenamesTheFileAndLoadsNothing()
    {
        File.WriteAllText(ListPath, "{ \"formatVersion\": 2, \"accounts\": [] }");

        var list = new AccountList(ListPath);

        Assert.False(list.Load());

        // Renamed, NOT deleted. It holds refresh tokens somebody may want back.
        Assert.False(File.Exists(ListPath));
        Assert.True(File.Exists(Path.Combine(_folder, "accounts-old.json")));
    }

    [Fact]
    public void AMissingFileIsNotAnError()
    {
        var list = new AccountList(ListPath);

        // Load returns false, but the list is usable and empty: this is every first run.
        Assert.False(list.Load());
        Assert.Empty(list.Accounts);
    }

    [Fact]
    public void GarbageInTheFileDoesNotThrow()
    {
        File.WriteAllText(ListPath, "this is not json at all {{{");

        var list = new AccountList(ListPath);

        Assert.False(list.Load());
        Assert.Empty(list.Accounts);
    }

    [Fact]
    public void OneBrokenAccountDoesNotLoseTheOthers()
    {
        var json =
            "{ \"formatVersion\": 3, \"accounts\": ["
            + "\"not an object\","
            + AccountJson("bbb", "Steve")
            + "] }";

        File.WriteAllText(ListPath, json);

        var list = new AccountList(ListPath);

        Assert.True(list.Load());
        Assert.Single(list.Accounts);
        Assert.Equal("Steve", list.Accounts[0].ProfileName);
    }

    [Fact]
    public void AnyAccountIsValidOnlyCountsOnesThatOwnTheGame()
    {
        var list = new AccountList(ListPath);

        list.Add(Signed("aaa", "Alex", owns: false));

        Assert.False(list.AnyAccountIsValid);

        list.Add(Signed("bbb", "Steve", owns: true));

        Assert.True(list.AnyAccountIsValid);
    }

    [Fact]
    public void AnOfflineAccountNeverOwnsTheGame()
    {
        var list = new AccountList(ListPath);

        list.Add(MinecraftAccount.CreateOffline("Player"));

        // Offline accounts are for single player and LAN. Counting one as valid would make the
        // launcher think you can join an online server.
        Assert.False(list.AnyAccountIsValid);
    }

    [Fact]
    public void ChangingTheListRaisesAnEvent()
    {
        var list = new AccountList(ListPath);
        var changes = 0;

        list.ListChanged += (_, _) => changes++;

        list.Add(Signed("aaa", "Alex"));

        Assert.Equal(1, changes);

        list.Remove(list.Accounts[0]);

        Assert.Equal(2, changes);
    }

    [Fact]
    public void ChangingTheDefaultRaisesItsOwnEvent()
    {
        var list = new AccountList(ListPath);
        var changes = 0;

        var account = Signed("aaa", "Alex");

        list.Add(account);
        list.DefaultAccountChanged += (_, _) => changes++;

        list.DefaultAccount = account;

        Assert.Equal(1, changes);

        // Setting the same one again is not a change.
        list.DefaultAccount = account;

        Assert.Equal(1, changes);

        list.DefaultAccount = null;

        Assert.Equal(2, changes);
    }

    [Fact]
    public void AutosaveWritesTheFileWithoutBeingAsked()
    {
        var list = new AccountList(ListPath) { Autosave = true };

        list.Add(Signed("aaa", "Alex"));

        Assert.True(File.Exists(ListPath));

        var accounts = JsonNode.Parse(File.ReadAllText(ListPath))!["accounts"]!.AsArray();

        Assert.Single(accounts);
    }

    [Fact]
    public void FindingAnAccountByProfileName()
    {
        var list = new AccountList(ListPath);

        list.Add(Signed("aaa", "Alex"));

        Assert.NotNull(list.FindByProfileName("Alex"));
        Assert.Null(list.FindByProfileName("Nobody"));
    }

    [Fact]
    public void TheSavedFileRoundTripsATokenSoSigningInSurvivesARestart()
    {
        /*
         * THE POINT OF THE WHOLE FILE. If the refresh token does not come back, every restart is a
         * fresh sign-in, and the account list may as well not exist.
         */
        var list = new AccountList(ListPath);

        var account = Signed("aaa", "Alex");

        account.Data.MsaToken.Value = "access-token-value";
        account.Data.MsaToken.RefreshToken = "refresh-token-value";
        account.Data.MsaToken.Validity = Validity.Certain;

        list.Add(account);
        list.Save();

        var loaded = new AccountList(ListPath);

        loaded.Load();

        Assert.Equal("refresh-token-value", loaded.Accounts[0].Data.MsaToken.RefreshToken);
    }

    [Fact]
    public void ARewriteLeavesNoTemporaryFileBehind()
    {
        /*
         * The save goes through a temp file so a crash mid-write cannot leave a half-written
         * accounts.json -- which would cost somebody every account they have signed into. This checks
         * the cleanup half: the folder holds accounts.json and nothing else.
         */
        var list = new AccountList(ListPath);

        list.Add(Signed("aaa", "Alex"));
        list.Save();

        list.Add(Signed("bbb", "Steve"));
        list.Save();

        var files = Directory.GetFiles(_folder).Select(Path.GetFileName).ToArray();

        Assert.Equal(new[] { "accounts.json" }, files);
    }

    [SkippableFact]
    public void TheFileIsReadableOnlyByItsOwner()
    {
        // Refresh tokens. On a shared machine every other user can read this otherwise.
        Skip.If(OperatingSystem.IsWindows(), "Unix permission bits; Windows inherits directory ACLs.");

        var list = new AccountList(ListPath);

        list.Add(Signed("aaa", "Alex"));
        list.Save();

        // Skip.If threw already if this is Windows; the analyser cannot see through it.
#pragma warning disable CA1416
        var mode = File.GetUnixFileMode(ListPath);
#pragma warning restore CA1416

        Assert.Equal(UnixFileMode.None, mode & UnixFileMode.GroupRead);
        Assert.Equal(UnixFileMode.None, mode & UnixFileMode.OtherRead);
    }
}
