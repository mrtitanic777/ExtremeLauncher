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
 * Ported in behaviour from launcher/ui/dialogs/ModUpdateDialog.cpp.
 *
 * KEEPING AN INSTANCE UP TO DATE, which is the other half of being able to install mods at all. A
 * modded instance goes stale the week after it is made: mods get performance fixes and crash fixes,
 * and without this the only way to find out is to notice a version number on a website.
 *
 * ONE REQUEST FOR THE WHOLE FOLDER. Modrinth's /version_files/update takes every hash at once and
 * answers with what each should become, so checking a hundred mods costs one round trip rather than a
 * hundred. The loaders and game versions go with it as a FILTER -- without them the service will
 * happily offer a 1.21 build as the update for a mod in a 1.20.1 instance, which installs cleanly and
 * then refuses to load.
 *
 * IT HASHES THE FILES RATHER THAN TRUSTING THE INDEX. The packwiz entry records a hash, but the file
 * beside it may have been replaced by hand since -- and an update check that reports on a file that is
 * no longer there is worse than one that reports nothing.
 */

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.Launch;

/// <summary>One mod that could be updated.</summary>
public sealed record ModUpdate(
    string Name,
    string CurrentFileName,
    string NewFileName,
    string NewVersionName,
    string DownloadUrl,
    string Sha512,
    string ProjectId,
    string VersionId)
{
    /// <summary>Whether the installed file is disabled, so the replacement should be too.</summary>
    public bool WasDisabled { get; init; }
}

/// <summary>What a check found, including what it could not answer for.</summary>
/// <param name="Unrecognised">
/// How many jars the service had never seen. A mod from CurseForge, or built by hand, has no hash
/// Modrinth knows -- so it is simply absent from the answer, which is INDISTINGUISHABLE from
/// "already current" unless it is counted. Reported so the user is told the check did not cover
/// everything, rather than being left to assume it did.
/// </param>
public sealed record ModUpdateReport(
    IReadOnlyList<ModUpdate> Updates,
    int Checked,
    int Unrecognised)
{
    public static readonly ModUpdateReport Empty = new([], 0, 0);
}

public sealed class ModUpdateCheck(HttpClient client)
{
    /// <summary>
    /// Finds what could be updated in an instance's mods folder.
    /// </summary>
    /// <param name="loader">The instance's loader name, e.g. "Fabric". Empty checks nothing.</param>
    /// <param name="minecraftVersion">The instance's Minecraft version.</param>
    public async Task<ModUpdateReport> FindAsync(
        string gameRoot,
        string loader,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var modsFolder = FileSystem.PathCombine(gameRoot, "mods");

        if (!Directory.Exists(modsFolder) || minecraftVersion.Length == 0)
        {
            return ModUpdateReport.Empty;
        }

        /*
         * A vanilla instance has no loader, and Modrinth's filter requires one. Rather than sending an
         * empty list -- which matches nothing and quietly reports "no updates" -- this says so by
         * returning nothing, and the caller explains why.
         */
        var loaderFacet = LoaderFacet(loader);

        if (loaderFacet.Length == 0)
        {
            return ModUpdateReport.Empty;
        }

        var installed = HashInstalledMods(modsFolder);

        if (installed.Count == 0)
        {
            return ModUpdateReport.Empty;
        }

        var api = new ModrinthApi(client);

        JsonObject answer;

        try
        {
            answer = await api.LatestVersionsAsync(
                [.. installed.Keys],
                "sha512",
                [loaderFacet],
                [minecraftVersion],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or Core.JsonException or System.Text.Json.JsonException)
        {
            throw new LauncherException($"Could not check for updates: {e.Message}");
        }

        var updates = new List<ModUpdate>();

        foreach (var (hash, node) in answer)
        {
            if (node is not JsonObject version || !installed.TryGetValue(hash, out var current))
            {
                continue;
            }

            var files = version["files"] as JsonArray;

            // The primary file is the jar; a version can also carry sources and javadoc jars, and
            // installing one of those in place of the mod would break the instance silently.
            var primary = files?.OfType<JsonObject>().FirstOrDefault(f => f["primary"]?.GetValue<bool>() == true)
                ?? files?.OfType<JsonObject>().FirstOrDefault();

            if (primary is null)
            {
                continue;
            }

            var newFileName = Json.EnsureString(primary, "filename");
            var url = Json.EnsureString(primary, "url");

            if (newFileName.Length == 0 || url.Length == 0)
            {
                continue;
            }

            /*
             * THE SAME FILE NAME MEANS NO UPDATE. Modrinth answers for every hash it recognises,
             * including ones already current -- the endpoint's contract is "what this should be", not
             * "what is newer". Without this check the dialog would offer to reinstall every mod in the
             * folder as an update, which is worse than useless: it looks like everything is stale.
             */
            if (string.Equals(newFileName, current.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            updates.Add(new ModUpdate(
                Name: Path.GetFileNameWithoutExtension(current.FileName),
                CurrentFileName: current.FileName,
                NewFileName: newFileName,
                NewVersionName: Json.EnsureString(version, "name"),
                DownloadUrl: url,
                Sha512: HashFrom(primary),
                ProjectId: Json.EnsureString(version, "project_id"),
                VersionId: Json.EnsureString(version, "id"))
            {
                WasDisabled = current.Disabled,
            });
        }

        /*
         * COUNTED, not inferred. The service returns a key for every hash it recognises -- including
         * ones already current, which was established by probing it -- so anything missing from the
         * answer is a jar it has never seen. That is the CurseForge and hand-built case, and without
         * counting it a folder full of CurseForge mods reports "everything is up to date".
         */
        var unrecognised = installed.Keys.Count(hash => !answer.ContainsKey(hash));

        return new ModUpdateReport(
            updates.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            installed.Count,
            unrecognised);
    }

    private static string HashFrom(JsonObject file)
        => file["hashes"] is JsonObject hashes ? Json.EnsureString(hashes, "sha512") : string.Empty;

    /// <summary>Every jar in the folder, keyed by its sha512.</summary>
    private static Dictionary<string, (string FileName, bool Disabled)> HashInstalledMods(string modsFolder)
    {
        var installed = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(modsFolder))
        {
            var name = Path.GetFileName(path);

            var disabled = name.EndsWith(PackExport.DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            var bare = disabled ? name[..^PackExport.DisabledSuffix.Length] : name;

            if (!bare.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);

                installed[Convert.ToHexStringLower(SHA512.HashData(stream))] = (bare, disabled);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One unreadable file -- the game may have it open -- is not a reason to check none
                // of the others.
                continue;
            }
        }

        return installed;
    }

    /// <summary>The name Modrinth knows a loader by.</summary>
    private static string LoaderFacet(string loader) => loader switch
    {
        "Fabric" => "fabric",
        "Quilt" => "quilt",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        _ => string.Empty,
    };
}
