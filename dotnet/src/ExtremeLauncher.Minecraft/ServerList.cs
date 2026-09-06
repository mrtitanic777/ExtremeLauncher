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
 * Ported from the file half of ui/pages/instance/ServersPage.cpp.
 *
 * The multiplayer list the game writes: an UNCOMPRESSED NBT file holding one list, "servers", of
 * compounds with "name", "ip", and optionally "icon" (a base64 PNG) and "acceptTextures" (a byte).
 *
 * UNCOMPRESSED, unlike level.dat, which is gzipped. Reading it with the wrong one gives an empty list
 * rather than an error, which is the sort of thing that looks like "this instance has no servers".
 *
 * A FILE THAT WILL NOT PARSE READS AS EMPTY, not as a failure. That is upstream's behaviour and it is
 * the right one here: a corrupt servers.dat should not stop the page opening, because the page is
 * where someone would go to fix it.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

/// <summary>Whether a server's resource pack is accepted without asking.</summary>
public enum AcceptsTextures
{
    /// <summary>Not recorded, so the game asks each time.</summary>
    Prompt,

    Always,

    Never,
}

/// <summary>One entry in the game's multiplayer list.</summary>
public sealed record MinecraftServer
{
    public string Name { get; init; } = "Minecraft Server";

    public string Address { get; init; } = string.Empty;

    /// <summary>The server's icon as raw PNG bytes, decoded from base64, or empty.</summary>
    public byte[] Icon { get; init; } = [];

    public AcceptsTextures AcceptsTextures { get; init; } = AcceptsTextures.Prompt;
}

public static class ServerList
{
    public const string FileName = "servers.dat";

    /// <summary>Reads the multiplayer list, saying whether it could.</summary>
    /// <remarks>
    /// THE DISTINCTION Load THROWS AWAY, and it only started to matter when this file became
    /// writable. "No servers" and "I could not read your servers" are the same empty list to a page
    /// that only displays them -- and are the difference between saving nothing and DESTROYING a
    /// player's whole multiplayer list to a page that writes them back.
    ///
    /// Upstream has the same guard, as a bool on the model: scheduleSave() refuses when !m_loaded and
    /// logs "Server list should never save if it didn't successfully load".
    ///
    /// A missing file counts as loaded: an instance that has never been played has no servers.dat,
    /// and that is not a failure to read anything -- it is an empty list, and adding the first server
    /// to it must work.
    /// </remarks>
    public static bool TryLoad(string gameRoot, out List<MinecraftServer> servers)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);

        var path = FileSystem.PathCombine(gameRoot, FileName);

        if (!File.Exists(path))
        {
            servers = [];

            return true;
        }

        try
        {
            servers = Read(File.ReadAllBytes(path));

            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or JsonException)
        {
            servers = [];

            return false;
        }
    }

    /// <summary>Reads the multiplayer list out of a game directory.</summary>
    /// <remarks>
    /// Returns an empty list for a missing or unreadable file, as upstream does. Callers that are
    /// going to WRITE the list back must use <see cref="TryLoad"/> instead -- see its remarks.
    /// </remarks>
    public static List<MinecraftServer> Load(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);

        var path = FileSystem.PathCombine(gameRoot, FileName);

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return Read(File.ReadAllBytes(path));
        }
        /*
         * JsonException is what the NBT reader throws -- an odd name, but it is this port's general
         * parse failure and it derives from LauncherException. Named explicitly because I first wrote
         * this catch from memory (IOException / InvalidDataException / FormatException) and a file of
         * plain text sailed straight through it.
         */
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or JsonException)
        {
            /*
             * A corrupt file reads as no servers rather than stopping the page from opening -- which is
             * where someone would go to fix it. The alternative is a launcher that refuses to show a
             * page because of the very problem the page exists to address.
             */
            return [];
        }
    }

    /// <summary>Writes the multiplayer list into a game directory.</summary>
    /// <remarks>
    /// Ported from Server::serialize and ServersModel::save_internal in ServersPage.cpp.
    ///
    /// EXACTLY UPSTREAM'S FIELD SET, which is smaller than what the reader accepts: name and ip
    /// always, icon only when there is one, acceptTextures only when it is not "ask". Writing an
    /// empty icon or a byte meaning "ask" would both be legal NBT and both change the game's
    /// behaviour -- an "acceptTextures: 0" is "never", not "ask", so writing one for every server
    /// would silently answer a question the player has not been asked.
    ///
    /// WRITTEN THROUGH A TEMPORARY FILE AND MOVED INTO PLACE. This is the one file in an instance
    /// whose loss is unrecoverable -- a world can be re-downloaded, a mod re-installed, but nobody
    /// remembers the address of the server they joined once in 2019. A half-written servers.dat from
    /// a crash mid-write reads as an empty list, which is exactly the silent total loss worth the
    /// extra syscall to avoid.
    /// </remarks>
    public static void Save(string gameRoot, IEnumerable<MinecraftServer> servers)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(servers);

        var list = new NbtTag
        {
            Type = NbtTagType.List,
            Name = "servers",
            ListType = NbtTagType.Compound,
        };

        foreach (var server in servers)
        {
            list.Children.Add(Serialize(server));
        }

        /*
         * The root compound is UNNAMED, which is what the game writes and what the reader here
         * expects. A named root parses fine and is still wrong -- other tools read servers.dat too.
         */
        var root = new NbtTag { Type = NbtTagType.Compound, Name = string.Empty };

        root.Children.Add(list);

        var path = FileSystem.PathCombine(gameRoot, FileName);

        Directory.CreateDirectory(gameRoot);

        // Uncompressed: this is not level.dat.
        var bytes = Nbt.Write(root);

        var temporary = path + ".tmp";

        File.WriteAllBytes(temporary, bytes);

        File.Move(temporary, path, overwrite: true);
    }

    private static NbtTag Serialize(MinecraftServer server)
    {
        var tag = new NbtTag { Type = NbtTagType.Compound };

        // Trimmed, as upstream does: an address with a stray space does not resolve, and a user who
        // pasted one has no way of seeing why.
        tag.Put("name", server.Name.Trim());
        tag.Put("ip", server.Address.Trim());

        if (server.Icon.Length != 0)
        {
            tag.Put("icon", Convert.ToBase64String(server.Icon));
        }

        if (server.AcceptsTextures != AcceptsTextures.Prompt)
        {
            tag.Children.Add(new NbtTag
            {
                Type = NbtTagType.Byte,
                Name = "acceptTextures",
                Value = (byte)(server.AcceptsTextures == AcceptsTextures.Always ? 1 : 0),
            });
        }

        return tag;
    }

    private static List<MinecraftServer> Read(byte[] bytes)
    {
        // Uncompressed: this is not level.dat.
        var root = Nbt.Read(bytes);

        var list = root.Children.FirstOrDefault(c => c.Name == "servers");

        /*
         * A file with no "servers" list at all counts as READ, not as broken: the game writes exactly
         * that for a player who has removed their last server, and refusing to save over it would
         * leave them unable to add one back.
         */
        return list is null ? [] : [.. list.Children.Select(Parse)];
    }

    private static MinecraftServer Parse(NbtTag server)
    {
        var name = server.Children.FirstOrDefault(c => c.Name == "name")?.Value as string;
        var address = server.Children.FirstOrDefault(c => c.Name == "ip")?.Value as string;
        var icon = server.Children.FirstOrDefault(c => c.Name == "icon")?.Value as string;
        var textures = server.Children.FirstOrDefault(c => c.Name == "acceptTextures");

        return new MinecraftServer
        {
            // Upstream's default for an entry with no name. The game shows the same.
            Name = name is { Length: > 0 } ? name : "Minecraft Server",
            Address = address ?? string.Empty,
            Icon = DecodeIcon(icon),

            /*
             * THREE STATES, not two. Absent means "ask me", which is what the game does for a server
             * whose textures the user has never answered about -- collapsing it to false would silently
             * turn "ask" into "never".
             */
            AcceptsTextures = textures is null
                ? AcceptsTextures.Prompt
                : Convert.ToInt32(textures.Value ?? 0, System.Globalization.CultureInfo.InvariantCulture) != 0
                    ? AcceptsTextures.Always
                    : AcceptsTextures.Never,
        };
    }

    /// <summary>Decodes the base64 icon, treating anything unreadable as no icon.</summary>
    private static byte[] DecodeIcon(string? base64)
    {
        if (base64 is not { Length: > 0 })
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            // A bad icon must not lose the server it belongs to.
            return [];
        }
    }
}
