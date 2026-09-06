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
 * servers.dat is written by the GAME, not by this launcher, so these read files built the way the game
 * builds them: UNCOMPRESSED NBT, unlike level.dat. Reading it with the wrong one yields an empty list
 * rather than an error, which would look exactly like "this instance has no servers".
 */

using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class ServerListTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-srv-" + Guid.NewGuid().ToString("N"));

    public ServerListTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>Builds a servers.dat the way the game does.</summary>
    private void Write(params NbtTag[] servers)
    {
        var list = new NbtTag { Type = NbtTagType.List, Name = "servers", ListType = NbtTagType.Compound };

        foreach (var server in servers)
        {
            list.Children.Add(server);
        }

        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Children.Add(list);

        File.WriteAllBytes(Path.Combine(_temp, "servers.dat"), Nbt.Write(root));
    }

    private static NbtTag Server(string? name, string? ip, string? icon = null, bool? acceptTextures = null)
    {
        var server = new NbtTag { Type = NbtTagType.Compound };

        if (name is not null)
        {
            server.Put("name", name);
        }

        if (ip is not null)
        {
            server.Put("ip", ip);
        }

        if (icon is not null)
        {
            server.Put("icon", icon);
        }

        if (acceptTextures is { } accept)
        {
            server.Children.Add(new NbtTag
            {
                Type = NbtTagType.Byte,
                Name = "acceptTextures",
                Value = (byte)(accept ? 1 : 0),
            });
        }

        return server;
    }

    private string DatPath => Path.Combine(_temp, "servers.dat");

    /// <summary>The raw file, for assertions that must not go back through this port's own reader.</summary>
    private byte[] Raw => File.ReadAllBytes(DatPath);

    private static string Ascii(byte[] bytes) => System.Text.Encoding.ASCII.GetString(bytes);

    // ================================================================== writing

    [Fact]
    public void AWrittenListReadsBack()
    {
        ServerList.Save(_temp, [
            new MinecraftServer { Name = "Home", Address = "home.example.invalid" },
            new MinecraftServer { Name = "Other", Address = "other.example.invalid:25566" },
        ]);

        var read = ServerList.Load(_temp);

        Assert.Equal(["Home", "Other"], read.Select(s => s.Name));
        Assert.Equal(["home.example.invalid", "other.example.invalid:25566"], read.Select(s => s.Address));
    }

    [Fact]
    public void TheOrderIsKept()
    {
        // The multiplayer screen shows them in file order, and moving one up is an edit the page
        // offers -- so the order IS the data, not an incidental detail.
        ServerList.Save(_temp, [
            new MinecraftServer { Name = "Third", Address = "c" },
            new MinecraftServer { Name = "First", Address = "a" },
            new MinecraftServer { Name = "Second", Address = "b" },
        ]);

        Assert.Equal(["Third", "First", "Second"], ServerList.Load(_temp).Select(s => s.Name));
    }

    [Fact]
    public void TheFileIsUncompressedNbtLikeTheGameWrites()
    {
        /*
         * ASSERTED ON THE BYTES, not through this port's reader, which would happily round-trip a
         * gzipped file this port wrote and the game could not open. servers.dat is uncompressed --
         * unlike level.dat -- so the first byte is the root compound's tag id, 0x0A, and NOT gzip's
         * 0x1F 0x8B.
         */
        ServerList.Save(_temp, [new MinecraftServer { Name = "Home", Address = "home.example.invalid" }]);

        var raw = Raw;

        Assert.Equal(0x0A, raw[0]);
        Assert.Contains("servers", Ascii(raw), StringComparison.Ordinal);
        Assert.Contains("home.example.invalid", Ascii(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRootCompoundHasNoName()
    {
        // What the game writes. A named root parses fine here and is still wrong: other tools read
        // this file too. Bytes 1 and 2 are the root name's length, and it must be zero.
        ServerList.Save(_temp, [new MinecraftServer { Name = "Home", Address = "a" }]);

        var raw = Raw;

        Assert.Equal(0, raw[1]);
        Assert.Equal(0, raw[2]);
    }

    [Fact]
    public void AServerWithNoIconWritesNoIconField()
    {
        /*
         * Upstream's serialize() omits it. An empty "icon" string is legal NBT and the game would
         * try to decode it as a PNG.
         */
        ServerList.Save(_temp, [new MinecraftServer { Name = "Home", Address = "a" }]);

        Assert.DoesNotContain("icon", Ascii(Raw), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIconIsWrittenBackAsBase64()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 };

        ServerList.Save(_temp, [new MinecraftServer { Name = "Home", Address = "a", Icon = png }]);

        Assert.Contains(Convert.ToBase64String(png), Ascii(Raw), StringComparison.Ordinal);
        Assert.Equal(png, ServerList.Load(_temp).Single().Icon);
    }

    [Fact]
    public void AskingIsWrittenAsAbsenceRatherThanAsZero()
    {
        /*
         * THE ONE THAT WOULD SILENTLY CHANGE THE GAME. "acceptTextures: 0" does not mean "ask", it
         * means "never" -- so writing a byte for every server would answer, on the player's behalf,
         * a question they have never been asked, for every server in the list, the first time they
         * touched this page.
         */
        ServerList.Save(_temp, [
            new MinecraftServer { Name = "Asked", Address = "a", AcceptsTextures = AcceptsTextures.Prompt },
        ]);

        Assert.DoesNotContain("acceptTextures", Ascii(Raw), StringComparison.Ordinal);
        Assert.Equal(AcceptsTextures.Prompt, ServerList.Load(_temp).Single().AcceptsTextures);
    }

    [Theory]
    [InlineData(AcceptsTextures.Always)]
    [InlineData(AcceptsTextures.Never)]
    public void AnAnsweredTexturesQuestionSurvives(AcceptsTextures answer)
    {
        ServerList.Save(_temp, [new MinecraftServer { Name = "Home", Address = "a", AcceptsTextures = answer }]);

        Assert.Contains("acceptTextures", Ascii(Raw), StringComparison.Ordinal);
        Assert.Equal(answer, ServerList.Load(_temp).Single().AcceptsTextures);
    }

    [Fact]
    public void NamesAndAddressesAreTrimmed()
    {
        // Upstream trims both. An address with a stray space does not resolve, and somebody who
        // pasted one has no way of seeing why.
        ServerList.Save(_temp, [new MinecraftServer { Name = "  Home  ", Address = "  a.example.invalid  " }]);

        var server = ServerList.Load(_temp).Single();

        Assert.Equal("Home", server.Name);
        Assert.Equal("a.example.invalid", server.Address);
    }

    [Fact]
    public void SavingAnEmptyListLeavesAnEmptyListNotAMissingFile()
    {
        /*
         * Removing the last server has to write an empty list. Deleting the file instead would work
         * -- the game makes a new one -- right up until something restores it from a backup.
         */
        ServerList.Save(_temp, []);

        Assert.True(File.Exists(DatPath));
        Assert.Empty(ServerList.Load(_temp));
    }

    [Fact]
    public void SavingReplacesWhatWasThereRatherThanAppending()
    {
        ServerList.Save(_temp, [new MinecraftServer { Name = "Old", Address = "a" }]);
        ServerList.Save(_temp, [new MinecraftServer { Name = "New", Address = "b" }]);

        Assert.Equal(["New"], ServerList.Load(_temp).Select(s => s.Name));
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehind()
    {
        /*
         * The write goes through a temporary and is moved into place, because a half-written
         * servers.dat reads as an empty list -- a silent total loss of the one thing in an instance
         * nobody can reconstruct. The leftover would sit in the game directory forever.
         */
        ServerList.Save(_temp, [new MinecraftServer { Name = "Home", Address = "a" }]);

        Assert.Empty(Directory.GetFiles(_temp, "*.tmp"));
    }

    [Fact]
    public void UnicodeNamesSurviveTheRoundTrip()
    {
        // NBT strings are modified UTF-8, which this port implements by hand.
        ServerList.Save(_temp, [new MinecraftServer { Name = "Сервер Ünicode", Address = "a" }]);

        Assert.Equal("Сервер Ünicode", ServerList.Load(_temp).Single().Name);
    }

    [Fact]
    public void AFileTheGameWroteCanBeReadEditedAndWrittenBack()
    {
        /*
         * The whole path the page uses: a file built the way the game builds it, read, changed, and
         * saved. Everything else here writes files this port made.
         */
        Write(
            Server("Home", "home.example.invalid", acceptTextures: true),
            Server("Other", "other.example.invalid"));

        var servers = ServerList.Load(_temp);

        servers.RemoveAt(1);
        servers.Add(new MinecraftServer { Name = "Added", Address = "added.example.invalid" });

        ServerList.Save(_temp, servers);

        var read = ServerList.Load(_temp);

        Assert.Equal(["Home", "Added"], read.Select(s => s.Name));
        Assert.Equal(AcceptsTextures.Always, read[0].AcceptsTextures);
        Assert.Equal(AcceptsTextures.Prompt, read[1].AcceptsTextures);
    }

    // ================================================================== reading

    [Fact]
    public void ServersAreReadInOrder()
    {
        Write(
            Server("Hypixel", "mc.hypixel.net"),
            Server("A friend's server", "192.168.1.10:25566"));

        var servers = ServerList.Load(_temp);

        Assert.Equal(["Hypixel", "A friend's server"], servers.Select(s => s.Name));
        Assert.Equal(["mc.hypixel.net", "192.168.1.10:25566"], servers.Select(s => s.Address));
    }

    /// <summary>An entry with no name gets the game's own default rather than a blank row.</summary>
    [Fact]
    public void AnUnnamedServerGetsTheDefaultName()
    {
        Write(Server(name: null, ip: "mc.example.com"));

        Assert.Equal("Minecraft Server", Assert.Single(ServerList.Load(_temp)).Name);
    }

    /*
     * THREE STATES, NOT TWO. An absent acceptTextures means "ask me", which is what the game does for a
     * server the user has never answered about. Collapsing it to false would silently turn "ask" into
     * "never" -- and this file is written back by the game, so the lie would stick.
     */
    [Fact]
    public void AcceptTexturesHasThreeStates()
    {
        Write(
            Server("Asks", "a.example.com"),
            Server("Always", "b.example.com", acceptTextures: true),
            Server("Never", "c.example.com", acceptTextures: false));

        var servers = ServerList.Load(_temp);

        Assert.Equal(AcceptsTextures.Prompt, servers[0].AcceptsTextures);
        Assert.Equal(AcceptsTextures.Always, servers[1].AcceptsTextures);
        Assert.Equal(AcceptsTextures.Never, servers[2].AcceptsTextures);
    }

    [Fact]
    public void AnIconIsDecodedFromBase64()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 };

        Write(Server("With icon", "mc.example.com", icon: Convert.ToBase64String(png)));

        Assert.Equal(png, Assert.Single(ServerList.Load(_temp)).Icon);
    }

    /// <summary>A bad icon must not lose the server it belongs to.</summary>
    [Fact]
    public void AnUnreadableIconLeavesTheServerIntact()
    {
        Write(Server("With bad icon", "mc.example.com", icon: "this is not base64!!!"));

        var server = Assert.Single(ServerList.Load(_temp));

        Assert.Equal("With bad icon", server.Name);
        Assert.Empty(server.Icon);
    }

    // ================================================================== not reading

    [Fact]
    public void AnInstanceWithNoServersFileHasNoServers()
        => Assert.Empty(ServerList.Load(_temp));

    /*
     * A CORRUPT FILE READS AS EMPTY, not as a failure -- upstream's behaviour, and the right one: the
     * servers page is where someone would go to fix a broken list, so it must still open.
     */
    [Fact]
    public void ACorruptFileReadsAsEmptyRatherThanThrowing()
    {
        File.WriteAllText(Path.Combine(_temp, "servers.dat"), "definitely not NBT");

        Assert.Empty(ServerList.Load(_temp));
    }

    /*
     * THE FILE IS UNCOMPRESSED, unlike level.dat. Handed a gzipped one, the reader must not silently
     * succeed with nothing -- this pins which of the two readers is used.
     */
    [Fact]
    public void AGzippedFileIsNotMistakenForAValidList()
    {
        var list = new NbtTag { Type = NbtTagType.List, Name = "servers", ListType = NbtTagType.Compound };
        list.Children.Add(Server("Hypixel", "mc.hypixel.net"));

        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Children.Add(list);

        File.WriteAllBytes(Path.Combine(_temp, "servers.dat"), Nbt.WriteCompressed(root));

        Assert.Empty(ServerList.Load(_temp));
    }

    [Fact]
    public void AFileWithNoServersListReadsAsEmpty()
    {
        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Put("somethingElse", "value");

        File.WriteAllBytes(Path.Combine(_temp, "servers.dat"), Nbt.Write(root));

        Assert.Empty(ServerList.Load(_temp));
    }
}
