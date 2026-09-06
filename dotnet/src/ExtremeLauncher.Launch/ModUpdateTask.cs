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
 * Installing an update that ModUpdateCheck found.
 *
 * THE NEW FILE IS FETCHED AND VERIFIED BEFORE THE OLD ONE GOES ANYWHERE. Replacing a working mod with
 * a failed download is how an instance stops starting, and the user's next move is to launch it.
 *
 * THE OLD FILE IS NOT DELETED, IT IS RENAMED. A mod that turns out to be worse -- a new build with a
 * new crash -- is one somebody wants back, and going and finding the old version on a website is a
 * miserable way to spend an evening. It becomes "<name>.old", which the game ignores because it is not
 * a .jar, and which is obvious enough to delete by hand.
 */

using System.Security.Cryptography;
using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class ModUpdateTask : LauncherTask
{
    /// <summary>What a replaced file is renamed to.</summary>
    public const string ReplacedSuffix = ".old";

    private readonly IReadOnlyList<ModUpdate> _updates;

    private readonly string _gameRoot;

    private readonly HttpClient _client;

    public ModUpdateTask(IReadOnlyList<ModUpdate> updates, string gameRoot, HttpClient client)
        : base("Updating mods")
    {
        ArgumentNullException.ThrowIfNull(updates);

        _updates = updates;
        _gameRoot = gameRoot;
        _client = client;
    }

    public int UpdatedCount { get; private set; }

    /// <summary>What went wrong, one per mod that failed.</summary>
    public IReadOnlyList<string> Failures => _failures;

    private readonly List<string> _failures = [];

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var modsFolder = FileSystem.PathCombine(_gameRoot, "mods");

        FileSystem.EnsureFolderPathExists(modsFolder);

        for (var i = 0; i < _updates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var update = _updates[i];

            SetStatus($"Updating {update.Name}");
            SetProgress(i, _updates.Count);

            try
            {
                await ApplyAsync(update, modsFolder, cancellationToken).ConfigureAwait(false);

                UpdatedCount++;
            }
            catch (Exception e) when (e is LauncherException or IOException or HttpRequestException)
            {
                // One mod failing must not abandon the rest. Collected and reported together, so the
                // user sees which ones did not take rather than a single vague failure.
                _failures.Add($"{update.Name}: {e.Message}");
            }
        }

        SetProgress(_updates.Count, _updates.Count);
    }

    private async Task ApplyAsync(ModUpdate update, string modsFolder, CancellationToken cancellationToken)
    {
        /*
         * Downloaded to a temporary name first. The old file is still in place and still working at
         * this point, so a failure here costs nothing at all.
         */
        var temporary = FileSystem.PathCombine(modsFolder, update.NewFileName + ".part");

        var download = Download.MakeFile(_client, new Uri(update.DownloadUrl), temporary, update.NewFileName);

        if (update.Sha512.Length != 0)
        {
            // The service's own hash. A mod jar arriving corrupt and being installed over a working
            // one is precisely the failure this whole ordering exists to prevent.
            download.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA512, update.Sha512));
        }

        var job = new NetJob($"Updating {update.Name}", _client);

        job.AddNetAction(download);

        if (!await job.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw new LauncherException(job.FailReason.Length != 0 ? job.FailReason : "the download failed");
        }

        // The old file, under whichever name it actually has on disk.
        var oldName = update.WasDisabled ? update.CurrentFileName + PackExport.DisabledSuffix : update.CurrentFileName;
        var oldPath = FileSystem.PathCombine(modsFolder, oldName);

        if (File.Exists(oldPath))
        {
            var kept = oldPath + ReplacedSuffix;

            // A second update of the same mod would otherwise fail on the existing .old file.
            if (File.Exists(kept))
            {
                File.Delete(kept);
            }

            File.Move(oldPath, kept);
        }

        /*
         * STAYS DISABLED IF IT WAS DISABLED. A mod somebody turned off is one they will turn back on
         * some day; updating it must not silently put it back into the game.
         */
        var newName = update.WasDisabled ? update.NewFileName + PackExport.DisabledSuffix : update.NewFileName;

        File.Move(temporary, FileSystem.PathCombine(modsFolder, newName), overwrite: true);

        UpdatePackwizIndex(update, newName);
    }

    /// <summary>
    /// Points the packwiz entry at the new file, url and hash.
    /// </summary>
    /// <remarks>
    /// ALL THREE, not just the filename. PackExportTask reads these back to decide what to link, so an
    /// entry left with the old url under the new name would export a pack that hands everybody who
    /// installs it the version this instance just moved OFF -- silently, and only discoverable by
    /// installing the pack.
    ///
    /// Line-based rather than a TOML round trip, because this launcher wrote the file and its shape is
    /// known. Best effort and deliberately not fatal: the mod IS updated by the time this runs, and
    /// refusing an update that already happened would be the worse outcome.
    /// </remarks>
    private void UpdatePackwizIndex(ModUpdate update, string newFileName)
    {
        var indexDirectory = FileSystem.PathCombine(_gameRoot, ".index");

        if (!Directory.Exists(indexDirectory))
        {
            return;
        }

        // The name on disk may carry .disabled; what the index records is the bare jar name.
        var bareName = newFileName.EndsWith(PackExport.DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? newFileName[..^PackExport.DisabledSuffix.Length]
            : newFileName;

        try
        {
            foreach (var path in Directory.EnumerateFiles(indexDirectory, "*.pw.toml"))
            {
                var lines = File.ReadAllLines(path);

                if (!lines.Any(l => l.Contains(update.CurrentFileName, StringComparison.Ordinal)))
                {
                    continue;
                }

                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();

                    if (trimmed.StartsWith("filename", StringComparison.Ordinal))
                    {
                        lines[i] = $"filename = \"{bareName}\"";
                    }
                    else if (trimmed.StartsWith("url", StringComparison.Ordinal))
                    {
                        lines[i] = $"url = \"{update.DownloadUrl}\"";
                    }
                    else if (trimmed.StartsWith("hash ", StringComparison.Ordinal)
                             || trimmed.StartsWith("hash=", StringComparison.Ordinal))
                    {
                        // "hash " with the space, NOT StartsWith("hash") -- "hash-format" is a
                        // different key and rewriting it would corrupt the entry.
                        if (update.Sha512.Length != 0)
                        {
                            lines[i] = $"hash = \"{update.Sha512}\"";
                        }
                    }
                    else if (trimmed.StartsWith("version ", StringComparison.Ordinal)
                             || trimmed.StartsWith("version=", StringComparison.Ordinal))
                    {
                        /*
                         * The version id under [update.modrinth]. Missed in my first pass, and found
                         * by reading the file a real update had produced: the filename, url and hash
                         * had all moved on and this still named the version being replaced.
                         *
                         * It is what packwiz itself reads to decide what is installed, so leaving it
                         * stale would have any packwiz tool conclude the old version is still here.
                         */
                        if (update.VersionId.Length != 0)
                        {
                            lines[i] = $"version = \"{update.VersionId}\"";
                        }
                    }
                }

                File.WriteAllLines(path, lines);

                break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // See the remarks: the update itself succeeded.
        }
    }
}
