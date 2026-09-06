// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2022 flowln <flowlnlnln@gmail.com>
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
 * Ported from the manifest half of launcher/modplatform/modrinth/ModrinthInstanceCreationTask.cpp
 * and launcher/modplatform/modrinth/ModrinthPackManifest.cpp.
 *
 * A .mrpack IS A ZIP CONTAINING modrinth.index.json PLUS OVERRIDES. The index does not carry the mods
 * themselves -- it lists where to fetch each one and what it should hash to, which is what keeps a
 * pack small and lets the launcher reuse files it already has.
 *
 * PARSING IS SEPARATED FROM INSTALLING, which upstream does not do: its parseManifest opens a modal
 * dialog partway through to ask which optional mods the user wants, so the format cannot be read
 * without a UI on screen. Here optional files come back in their own list and the caller decides.
 * Everything below is a pure function of the file's bytes.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>One file a pack expects to be downloaded.</summary>
public sealed class ModrinthPackFile
{
    /// <summary>Where it goes, relative to the game directory, with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The sha512 the download must match.</summary>
    public byte[] Hash { get; set; } = [];

    /// <summary>Mirrors, in the order the pack lists them.</summary>
    public List<Uri> Downloads { get; } = [];

    /// <summary>Whether the pack requires this file, as opposed to offering it.</summary>
    public bool Required { get; set; } = true;
}

/// <summary>What a pack needs installed before its files make sense.</summary>
public sealed class ModrinthPackDependencies
{
    public string MinecraftVersion { get; set; } = string.Empty;

    public string FabricVersion { get; set; } = string.Empty;

    public string QuiltVersion { get; set; } = string.Empty;

    public string ForgeVersion { get; set; } = string.Empty;

    public string NeoForgeVersion { get; set; } = string.Empty;
}

/// <summary>A parsed modrinth.index.json.</summary>
public sealed class ModrinthPackManifest
{
    public string Name { get; set; } = string.Empty;

    public string VersionId { get; set; } = string.Empty;

    /// <summary>Files the pack requires.</summary>
    public List<ModrinthPackFile> Files { get; } = [];

    /// <summary>
    /// Files the pack offers but does not require.
    /// </summary>
    /// <remarks>
    /// Kept apart rather than mixed in with a flag, because the caller has to make a choice about
    /// these and a list it must remember to filter is a list it will forget to filter.
    /// </remarks>
    public List<ModrinthPackFile> OptionalFiles { get; } = [];

    public ModrinthPackDependencies Dependencies { get; } = new();
}

public static class ModrinthPack
{
    /// <summary>The only format version this understands.</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>What a file's path becomes when the user declines an optional mod.</summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>Reads a modrinth.index.json.</summary>
    public static ModrinthPackManifest Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var obj = Json.RequireObject(Json.RequireDocument(data, "modrinth.index.json"));
        var manifest = new ModrinthPackManifest();

        var formatVersion = Json.RequireInteger(obj, "formatVersion");

        // Refused, not guessed at: a later format could mean anything, including different semantics
        // for fields that happen to still parse.
        if (formatVersion != SupportedFormatVersion)
        {
            throw new JsonException($"Unknown format version: {formatVersion}");
        }

        var game = Json.RequireString(obj, "game");

        if (game != "minecraft")
        {
            throw new JsonException("Unknown game: " + game);
        }

        manifest.VersionId = Json.EnsureString(obj, "versionId");
        manifest.Name = Json.EnsureString(obj, "name");

        foreach (var element in Json.RequireArray(obj, "files"))
        {
            if (ParseFile(Json.RequireObject(element)) is { } file)
            {
                (file.Required ? manifest.Files : manifest.OptionalFiles).Add(file);
            }
        }

        ParseDependencies(Json.RequireObject(obj, "dependencies"), manifest.Dependencies);

        return manifest;
    }

    /// <returns>Null when the pack says this file does not belong on a client at all.</returns>
    private static ModrinthPackFile? ParseFile(JsonObject obj)
    {
        var file = new ModrinthPackFile
        {
            // Backslashes normalised: packs built on Windows carry them, and the path is used as a
            // relative path on every platform.
            Path = Json.RequireString(obj, "path").Replace('\\', '/'),
        };

        /*
         * SECURITY: see the note on ValidateRelativePath. Checked here, at the point the path enters
         * the launcher, rather than wherever it is eventually joined to a directory.
         */
        ValidateRelativePath(file.Path);

        var env = Json.EnsureObject(obj["env"]);

        // The env block is optional; a file without one is simply required.
        if (env.Count != 0)
        {
            var support = Json.EnsureString(env, "client", "unsupported");

            // Server-only files are not skipped as an error -- they are just not ours to install.
            if (support == "unsupported")
            {
                return null;
            }

            if (support == "optional")
            {
                file.Required = false;
            }
        }

        // Required, and only sha512 is accepted: this is what every downloaded file is verified against.
        var sha512 = Json.RequireString(Json.RequireObject(obj, "hashes"), "sha512");

        /*
         * An empty hash decodes happily to an empty array, so it has to be refused explicitly. The
         * format requires a sha512, and a file with nothing to verify against is worse than a missing
         * one: it downloads, passes an empty check, and installs whatever the mirror served.
         */
        if (sha512.Length == 0)
        {
            throw new JsonException($"File {file.Path} has no sha512 hash.");
        }

        try
        {
            file.Hash = Convert.FromHexString(sha512);
        }
        catch (FormatException e)
        {
            /*
             * Convert.FromHexString throws a bare FormatException, which is not the exception type
             * anything here catches -- so a hand-edited or hostile pack would escape the parser as a
             * raw BCL error rather than "this pack is malformed". Every other parse failure in this
             * codebase is a LauncherException, and a caller that handles one handles all of them.
             */
            throw new JsonException($"File {file.Path} has a malformed sha512 hash.", e);
        }

        var downloads = Json.EnsureArray(obj["downloads"]);

        foreach (var download in downloads)
        {
            var url = Json.EnsureString(download);

            /*
             * A malformed mirror is skipped rather than fatal, because the rest may still work.
             * Upstream is deliberately tolerant about parsing here -- its comment says Modrinth
             * mishandles spaces -- so a URL that merely looks odd should not lose the file.
             */
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                file.Downloads.Add(parsed);
            }
        }

        // No usable mirror at all is fatal: the pack cannot be installed without this file.
        if (file.Downloads.Count == 0)
        {
            throw new JsonException($"Download URL for {file.Path} is not a correctly formatted URL");
        }

        return file;
    }

    /// <summary>
    /// Reads the dependency block, refusing anything it does not recognise.
    /// </summary>
    /// <remarks>
    /// Upstream throws on an unknown dependency name rather than ignoring it, and that is right:
    /// silently skipping one would build an instance missing the loader the pack needs, which fails
    /// much later and much less clearly.
    /// </remarks>
    private static void ParseDependencies(JsonObject obj, ModrinthPackDependencies dependencies)
    {
        foreach (var (name, value) in obj)
        {
            var version = Json.RequireString(value);

            switch (name)
            {
                case "minecraft":
                    dependencies.MinecraftVersion = version;
                    break;

                case "fabric-loader":
                    dependencies.FabricVersion = version;
                    break;

                case "quilt-loader":
                    dependencies.QuiltVersion = version;
                    break;

                case "forge":
                    dependencies.ForgeVersion = version;
                    break;

                case "neoforge":
                    dependencies.NeoForgeVersion = version;
                    break;

                default:
                    throw new JsonException("Unknown dependency type: " + name);
            }
        }
    }

    /// <summary>
    /// Rejects a path that would escape the directory it is relative to.
    /// </summary>
    /// <remarks>
    /// SECURITY, AND NOT PRESENT UPSTREAM. A .mrpack is an untrusted file from the internet, and its
    /// index chooses where each download lands. Upstream reads <c>path</c> straight out of the JSON
    /// and joins it to the game directory, so an entry of <c>../../../../.bashrc</c> writes there --
    /// no download is even needed to notice, the path is simply taken at face value. This is the same
    /// class of hole as zip-slip, which the archive layer here already guards; a manifest that names
    /// its own destinations needs the identical check.
    /// </remarks>
    /// <remarks>
    /// Rejects rather than sanitises. A pack whose paths escape is either malicious or broken, and
    /// quietly rewriting it to something safe would install a pack that is not the one published.
    /// </remarks>
    public static void ValidateRelativePath(string path)
    {
        if (path.Length == 0)
        {
            throw new JsonException("A pack file has an empty path.");
        }

        // Absolute in any spelling, including a Windows drive or a UNC share.
        if (System.IO.Path.IsPathRooted(path) || path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal))
        {
            throw new JsonException($"Pack file path '{path}' is absolute.");
        }

        /*
         * Resolved against a marker root rather than matched against "..", so that anything the
         * filesystem would treat as an escape is caught however it is spelled -- "a/../../b",
         * trailing dots, and the rest.
         */
        var root = System.IO.Path.GetFullPath("/el-pack-root/");
        var resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));

        if (!resolved.StartsWith(root, StringComparison.Ordinal))
        {
            throw new JsonException($"Pack file path '{path}' escapes the pack directory.");
        }
    }

    /// <summary>
    /// Folds the user's choices about optional files back into the install list.
    /// </summary>
    /// <remarks>
    /// A declined file is still installed -- with <c>.disabled</c> appended, which is upstream's
    /// behaviour and better than it looks. The mod is on disk, so turning it on later is a rename
    /// rather than a download, and the pack's own list stays complete.
    /// </remarks>
    public static void ApplyOptionalSelection(ModrinthPackManifest manifest, IReadOnlySet<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(selectedPaths);

        foreach (var file in manifest.OptionalFiles)
        {
            if (selectedPaths.Contains(file.Path))
            {
                file.Required = true;
            }
            else
            {
                file.Path += DisabledSuffix;
            }

            manifest.Files.Add(file);
        }

        manifest.OptionalFiles.Clear();
    }

    /// <summary>
    /// Drops the files an update already has on disk, and reports the ones it must delete.
    /// </summary>
    /// <remarks>
    /// MATCHED ON HASH, NOT PATH. A mod that moved folders between versions is the same file and does
    /// not need fetching again; a mod at the same path with different contents does. Path matching
    /// would get both backwards.
    /// </remarks>
    /// <remarks>
    /// What is left in the OLD manifest after the pairing is what this version dropped, and it has to
    /// be deleted or the instance accumulates every version's mods -- two conflicting copies of the
    /// same mod is a crash on startup, not a tidiness problem.
    /// </remarks>
    /// <returns>Paths to delete, relative to the game directory.</returns>
    public static List<string> RemoveUnchanged(ModrinthPackManifest current, ModrinthPackManifest previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        // Queued per hash so N copies of one file cancel exactly N old copies, not all of them.
        var byHash = new Dictionary<string, Queue<ModrinthPackFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in current.Files)
        {
            var key = Convert.ToHexString(file.Hash);

            if (!byHash.TryGetValue(key, out var queue))
            {
                queue = new Queue<ModrinthPackFile>();
                byHash[key] = queue;
            }

            queue.Enqueue(file);
        }

        var alreadyOnDisk = new HashSet<ModrinthPackFile>();
        var stale = new List<string>();

        foreach (var old in previous.Files)
        {
            var key = Convert.ToHexString(old.Hash);

            if (byHash.TryGetValue(key, out var queue) && queue.Count != 0)
            {
                // Unchanged: already correct on disk, so neither downloaded nor deleted.
                alreadyOnDisk.Add(queue.Dequeue());

                continue;
            }

            if (old.Path.Length != 0)
            {
                stale.Add(old.Path);
            }
        }

        // Rebuilt by filtering rather than by flattening the map, so the pack's own order survives.
        var remaining = current.Files.Where(f => !alreadyOnDisk.Contains(f)).ToList();

        current.Files.Clear();
        current.Files.AddRange(remaining);

        return stale;
    }
}
