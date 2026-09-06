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
 * Ported from launcher/modplatform/helpers/OverrideUtils.cpp and the mrpack/ half of
 * launcher/modplatform/modrinth/ModrinthInstanceCreationTask.cpp.
 *
 * THE RECORD OF WHAT THE PACK PUT THERE. An instance folder after a modpack install is a mixture of
 * two things: files the pack supplied, and files the player has added since. Nothing about a file on
 * disk says which it is -- and without knowing, an update can only choose between leaving stale mods
 * behind and deleting things that were never the pack's.
 *
 * So the install keeps a ledger, in <instance>/mrpack/, exactly as upstream does:
 *
 *     modrinth.index.json    the manifest, so a later update can diff against it
 *     overrides.txt          every path the pack's "overrides" folder contributed
 *     client-overrides.txt   the same for "client-overrides"
 *
 * IT IS UPSTREAM'S FORMAT, NOT AN INVENTION, and the file names and layout matter: an instance made
 * by Prism can be updated by this launcher and the other way round.
 *
 * THIS PORT NEVER WROTE IT until now -- the same shape of gap as the ManagedPack settings in wave 38.
 * Every pack installed by this launcher before this wave has no ledger, and PackUpdatePlan says so
 * plainly rather than guessing.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.Launch;

/// <summary>What an install recorded about itself.</summary>
public sealed record PackLedger
{
    /// <summary>The pack's manifest as installed, or null when there is no ledger.</summary>
    public ModrinthPackManifest? Manifest { get; init; }

    /// <summary>Paths, relative to the game folder, that the pack's overrides wrote.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];

    /// <summary>Whether a ledger was found at all.</summary>
    public bool Exists => Manifest is not null;
}

public static class PackLedgerStore
{
    /// <summary>The folder inside an instance where the ledger lives. Upstream's name.</summary>
    public const string FolderName = "mrpack";

    public const string ManifestName = "modrinth.index.json";

    public static string FolderIn(string instanceRoot) => FileSystem.PathCombine(instanceRoot, FolderName);

    /// <summary>Writes the manifest half of the ledger.</summary>
    /// <remarks>
    /// The manifest is COPIED out of the pack rather than re-serialised, so what is kept is byte for
    /// byte what the author published. Re-writing it through this port's own model would quietly drop
    /// any field the model does not know about, and the whole value of the ledger is being able to
    /// compare against it later.
    /// </remarks>
    public static void WriteManifest(string instanceRoot, byte[] manifestBytes)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);

        var path = FileSystem.PathCombine(FolderIn(instanceRoot), ManifestName);

        FileSystem.EnsureFilePathExists(path);
        File.WriteAllBytes(path, manifestBytes);
    }

    /// <summary>Records the paths one overrides folder contributed.</summary>
    /// <param name="name">"overrides" or "client-overrides", which is also the file's name.</param>
    /// <remarks>
    /// ONE PATH PER LINE, which is upstream's format -- its readOverrides trims each line and treats
    /// it as a relative path. Nothing else may go on a line, or an instance this port writes becomes
    /// one Prism cannot update.
    /// </remarks>
    public static void WriteOverrides(string instanceRoot, string name, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var path = FileSystem.PathCombine(FolderIn(instanceRoot), name + ".txt");

        FileSystem.EnsureFilePathExists(path);

        // Forward slashes, because the ledger is read on whichever platform opens the instance next
        // and a backslash is a legal filename character on Linux.
        File.WriteAllLines(path, paths.Select(p => p.Replace('\\', '/')));
    }

    /// <summary>Reads whatever ledger an instance has, or an empty one.</summary>
    public static PackLedger Read(string instanceRoot)
    {
        var folder = FolderIn(instanceRoot);
        var manifestPath = FileSystem.PathCombine(folder, ManifestName);

        if (!File.Exists(manifestPath))
        {
            return new PackLedger();
        }

        ModrinthPackManifest manifest;

        try
        {
            manifest = ModrinthPack.Parse(File.ReadAllBytes(manifestPath));
        }
        catch (Exception e) when (e is Core.JsonException or IOException)
        {
            /*
             * A ledger that will not parse is worth exactly as much as no ledger, and treating it as
             * empty is the safe reading: PackUpdatePlan refuses to delete anything without one.
             */
            return new PackLedger();
        }

        var overrides = new List<string>();

        foreach (var name in new[] { "overrides", "client-overrides" })
        {
            var listPath = FileSystem.PathCombine(folder, name + ".txt");

            if (!File.Exists(listPath))
            {
                continue;
            }

            overrides.AddRange(
                File.ReadAllLines(listPath)
                    .Select(line => line.Trim())
                    .Where(line => line.Length != 0));
        }

        return new PackLedger { Manifest = manifest, Overrides = overrides };
    }
}
