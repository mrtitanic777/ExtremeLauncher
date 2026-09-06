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
 * The fork's news-feed servers, merged into an instance at launch.
 *
 * A LAUNCHER EDITING A PLAYER'S GAME FILE, with addresses that came off the network, every time they
 * press Launch. Upstream does this (its own source marks the block "uncommited"), so it is ported --
 * but every test here is about the ways it must not go wrong, because the file it touches holds the
 * one thing in an instance nobody can reconstruct.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Tasks;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class FeedServerInjectionTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-feedsrv-" + Guid.NewGuid().ToString("N"));

    public FeedServerInjectionTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string GameRoot => Path.Combine(_temp, ".minecraft");

    private void GiveItServers(params (string Name, string Address)[] servers)
    {
        Directory.CreateDirectory(GameRoot);

        ServerList.Save(GameRoot, servers.Select(s => new MinecraftServer { Name = s.Name, Address = s.Address }));
    }

    private async Task<List<string>> RunAsync(params string[] feedServers)
    {
        var step = new CreateGameFolders(GameRoot, feedServers);

        var lines = new List<string>();

        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        await step.RunAsync();

        return lines;
    }

    private List<MinecraftServer> OnDisk() => ServerList.Load(GameRoot);

    [Fact]
    public async Task TheFeedsServersAreAddedToAnInstanceThatHasNone()
    {
        await RunAsync("first.example.invalid", "second.example.invalid");

        Assert.Equal(
            ["first.example.invalid", "second.example.invalid"],
            OnDisk().Select(s => s.Address));
    }

    [Fact]
    public async Task ThePlayersOwnServersKeepTheirPlaceAtTheTop()
    {
        /*
         * Upstream appends the new ones FIRST and re-adds the player's afterwards, which silently
         * reorders a multiplayer list somebody has arranged. This does not.
         */
        GiveItServers(("Home", "home.example.invalid"), ("Friends", "friends.example.invalid"));

        await RunAsync("advert.example.invalid");

        Assert.Equal(
            ["home.example.invalid", "friends.example.invalid", "advert.example.invalid"],
            OnDisk().Select(s => s.Address));
    }

    [Fact]
    public async Task LaunchingTwiceDoesNotAddThemTwice()
    {
        await RunAsync("advert.example.invalid");
        await RunAsync("advert.example.invalid");

        Assert.Single(OnDisk());
    }

    [Fact]
    public async Task AServerThePlayerRenamedIsNotAddedAgain()
    {
        // Matched on address, which is what upstream compares. Somebody who renamed the entry still
        // has it, and a duplicate every launch would be its own kind of broken.
        GiveItServers(("My name for it", "advert.example.invalid"));

        await RunAsync("advert.example.invalid");

        var only = Assert.Single(OnDisk());

        Assert.Equal("My name for it", only.Name);
    }

    [Fact]
    public async Task AListThatWillNotParseIsLeftEXACTLYAsItIs()
    {
        /*
         * THE BUG THIS PORT FIXES. Upstream's parseServersDat returns null for any file it cannot
         * read, and the code then writes a fresh list holding only the fork's servers -- replacing
         * everything the player had, silently, on launch.
         *
         * Adding an advertisement is not worth losing somebody's server list over.
         */
        Directory.CreateDirectory(GameRoot);

        var path = Path.Combine(GameRoot, "servers.dat");

        File.WriteAllText(path, "definitely not NBT");

        var lines = await RunAsync("advert.example.invalid");

        Assert.Equal("definitely not NBT", File.ReadAllText(path));
        Assert.Contains(lines, l => l.Contains("left exactly as it is", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatWasAddedIsSaidOutLoud()
    {
        /*
         * Upstream's version is invisible: it edits the file and logs nothing. A launcher that adds
         * servers to somebody's game should at minimum leave a record in the launch log they already
         * paste into bug reports.
         */
        var lines = await RunAsync("advert.example.invalid");

        Assert.Contains(lines, l => l.Contains("advert.example.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingIsWrittenWhenTheFeedHasNoServers()
    {
        /*
         * The normal case in a build with no feed, and in every launch before the news has loaded.
         * Writing servers.dat anyway would rewrite the file on every launch for no reason -- and this
         * port has just been careful about a file that must not be rewritten for no reason.
         */
        await RunAsync();

        Assert.False(File.Exists(Path.Combine(GameRoot, "servers.dat")));
    }

    [Fact]
    public async Task AnUnchangedListIsNotRewritten()
    {
        GiveItServers(("Home", "advert.example.invalid"));

        var before = File.GetLastWriteTimeUtc(Path.Combine(GameRoot, "servers.dat"));

        await RunAsync("advert.example.invalid");

        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(GameRoot, "servers.dat")));
    }

    [Fact]
    public async Task TheFoldersAreStillMadeWhateverHappensToTheServers()
    {
        // The step's actual job. A failure in the fork's patch must not cost the launch its folders.
        Directory.CreateDirectory(GameRoot);

        File.WriteAllText(Path.Combine(GameRoot, "servers.dat"), "not NBT");

        await RunAsync("advert.example.invalid");

        Assert.True(Directory.Exists(GameRoot));
        Assert.True(Directory.Exists(Path.Combine(GameRoot, "server-resource-packs")));
    }

    [Fact]
    public async Task AnEmptyAddressInTheFeedIsIgnored()
    {
        // A feed with a stray blank <serverlistentry> should not put a nameless, addressless row in
        // somebody's multiplayer list.
        await RunAsync(string.Empty, "real.example.invalid");

        Assert.Equal(["real.example.invalid"], OnDisk().Select(s => s.Address));
    }
}
