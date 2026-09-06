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
 * Ported in behaviour from ModrinthCreationTask::updateInstance in
 * launcher/modplatform/modrinth/ModrinthInstanceCreationTask.cpp.
 *
 * CARRYING OUT A PLAN. PackUpdatePlanner works out what an update would do; this is the part that
 * does it, in an existing instance folder that somebody has been playing in.
 *
 * THE ORDER IS THE SAFETY PROPERTY, and it is the whole design:
 *
 *     1. fetch everything the new version needs, into a temporary folder
 *     2. only then remove what the old version left
 *     3. move the fetched files into place
 *     4. lay down the new overrides
 *     5. write the new ledger and version
 *
 * DOWNLOAD BEFORE DELETE. A network that dies half way through step 1 leaves the instance exactly as
 * it was and still playable; the reverse order leaves somebody with an instance missing half its mods
 * and no way back. This is the single most important decision in the file, and it costs disk space --
 * the new files exist twice for the length of step 3.
 *
 * EVERY PATH IS VALIDATED, including the ones being DELETED. The removal list comes out of a ledger
 * file, which is text on disk that another launcher wrote, or that somebody edited. See upstream bug
 * #12, which was a real path-traversal hole in .mrpack handling: the same rule has to cover the
 * delete side, where getting it wrong is worse than getting it wrong on the write side.
 */

using System.Security.Cryptography;
using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Net;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class PackUpdateTask : LauncherTask
{
    private readonly HttpClient _client;

    private readonly string _instanceRoot;

    private readonly string _gameRoot;

    private readonly IndexedPack _pack;

    private readonly IndexedVersion _version;

    private readonly SettingsObject _instanceConfig;

    public PackUpdateTask(
        HttpClient client,
        string instanceRoot,
        string gameRoot,
        IndexedPack pack,
        IndexedVersion version,
        SettingsObject instanceConfig)
        : base("Updating modpack")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(instanceConfig);

        _client = client;
        _instanceRoot = instanceRoot;
        _gameRoot = gameRoot;
        _pack = pack;
        _version = version;
        _instanceConfig = instanceConfig;
    }

    /// <summary>The plan that was carried out, once it has been.</summary>
    public PackUpdatePlan? Plan { get; private set; }

    /// <summary>How many files were actually deleted, which can be fewer than the plan listed.</summary>
    public int Removed { get; private set; }

    public int Downloaded { get; private set; }

    /// <summary>
    /// Works out what the update would do, without doing any of it.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM RUNNING IT so the plan can be put in front of somebody first. "This would remove
    /// 71 files" is a sentence a person should get to read before it happens, not after.
    /// </remarks>
    public static async Task<(PackUpdatePlan Plan, string PackFile)> PrepareAsync(
        HttpClient client,
        string instanceRoot,
        IndexedVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(version);

        var temp = FileSystem.PathCombine(
            Path.GetTempPath(),
            $"el-update-{Guid.NewGuid():N}{Path.GetExtension(version.FileName)}");

        await DownloadToAsync(client, version.DownloadUrl, temp, cancellationToken).ConfigureAwait(false);

        var manifest = ReadManifest(temp);

        /*
         * The new pack's own override paths, so the plan can tell a config that is being REFRESHED
         * from one that is really being lost. Without this every old override counts as a refresh,
         * which is the reassuring answer -- so it is worth the few milliseconds of listing the zip.
         */
        var plan = PackUpdatePlanner.Create(PackLedgerStore.Read(instanceRoot), manifest, OverridePathsIn(temp));

        return (plan, temp);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus($"Fetching {_pack.Name} {_version.Version}");

        var (plan, packFile) = await PrepareAsync(_client, _instanceRoot, _version, cancellationToken)
            .ConfigureAwait(false);

        Plan = plan;

        try
        {
            if (!plan.Possible)
            {
                throw new TaskFailedException(plan.Blocker);
            }

            var manifest = ReadManifest(packFile);

            // ---- 1. Everything the new version needs, into a temporary folder ----------------
            //
            // Nothing in the instance has been touched at this point, and nothing will be until
            // this has finished. An interrupted download leaves a playable instance.

            var incoming = FileSystem.PathCombine(
                Path.GetTempPath(),
                $"el-update-files-{Guid.NewGuid():N}");

            Directory.CreateDirectory(incoming);

            try
            {
                await FetchAsync(plan, incoming, cancellationToken).ConfigureAwait(false);

                // ---- 2. Only now, remove what the old version left --------------------------

                SetStatus($"Removing {plan.ToRemove.Count} file(s) and refreshing {plan.ToReplace.Count}");

                Removed = RemoveOld(plan);

                // ---- 3. Move the fetched files into place -----------------------------------

                SetStatus("Putting the new files in place");

                MoveIn(incoming);

                // ---- 4. The new version's overrides ------------------------------------------

                SetStatus("Applying the pack's own files");

                var overrides = ApplyOverrides(packFile);

                // ---- 5. The new ledger, and the version this instance now is ------------------

                PackLedgerStore.WriteManifest(_instanceRoot, File.ReadAllBytes(ManifestPathIn(packFile)));

                foreach (var (name, paths) in overrides)
                {
                    PackLedgerStore.WriteOverrides(_instanceRoot, name, paths);
                }

                RecordNewVersion(manifest);
            }
            finally
            {
                TryDeleteFolder(incoming);
            }
        }
        finally
        {
            TryDelete(packFile);
        }
    }

    /// <summary>Downloads everything the plan calls for, into a folder outside the instance.</summary>
    private async Task FetchAsync(PackUpdatePlan plan, string incoming, CancellationToken cancellationToken)
    {
        if (plan.ToDownload.Count == 0)
        {
            return;
        }

        SetStatus($"Downloading {plan.ToDownload.Count} file(s)");

        var job = new NetJob("Pack update", _client);

        foreach (var file in plan.ToDownload)
        {
            // The same guard the install uses, on the same kind of data. See upstream bug #12.
            ModrinthPack.ValidateRelativePath(file.Path);

            if (file.Downloads.Count == 0)
            {
                if (file.Required)
                {
                    throw new TaskFailedException($"The pack lists “{file.Path}” with nowhere to fetch it from.");
                }

                continue;
            }

            var target = FileSystem.PathCombine(incoming, file.Path);

            FileSystem.EnsureFilePathExists(target);

            var download = Download.MakeFile(_client, file.Downloads[0], target, file.Path);

            /*
             * The manifest's own sha512, the same as on install. A file served wrongly fails the job
             * BEFORE anything has been removed, which is the ordering doing its work: a bad mirror
             * cannot leave a half-updated instance.
             */
            if (file.Hash.Length != 0)
            {
                download.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA512, file.Hash));
            }

            job.AddNetAction(download);
        }

        job.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);

        if (!await job.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            /*
             * TOLD APART FROM A FAILURE. NetJob reports a cancellation the same way it reports a dead
             * mirror -- by returning false -- so without this check somebody who pressed Cancel is
             * told their download failed, as though something had gone wrong. It had not; they asked.
             *
             * Found by a test, because the two paths look identical from here and only differ in what
             * the person on the other end believes happened.
             */
            cancellationToken.ThrowIfCancellationRequested();

            /*
             * NOTHING HAS BEEN REMOVED YET. This is the failure the ordering exists for: the
             * instance is exactly as it was, and saying so is worth a sentence, because "update
             * failed" leaves somebody wondering whether their instance is now broken.
             */
            throw new TaskFailedException(
                $"Could not download the new version's files ({job.FailReason}). "
                + "Nothing has been changed -- the instance is exactly as it was.");
        }

        Downloaded = plan.ToDownload.Count;
    }

    /// <summary>Deletes what the old version left, skipping anything already gone.</summary>
    private int RemoveOld(PackUpdatePlan plan)
    {
        var removed = 0;

        // Both groups: a refreshed override is deleted here and written again by the overrides step
        // a moment later. They are counted apart for the message, not treated apart on disk.
        foreach (var relative in plan.AllRemovals)
        {
            /*
             * VALIDATED EVEN THOUGH IT CAME FROM OUR OWN LEDGER. That ledger is a text file another
             * launcher may have written, or that somebody edited. Getting this wrong on the delete
             * side is worse than on the write side.
             */
            try
            {
                ModrinthPack.ValidateRelativePath(relative);
            }
            catch (LauncherException)
            {
                LogSkipped(relative, "it is not a path inside the instance");

                continue;
            }

            var path = FileSystem.PathCombine(_gameRoot, relative);

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);

                    removed++;
                }

                // A file the player already deleted is not an error -- it is the outcome anyway.
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A locked file, usually because the game is running. Reported and stepped over: an
                // update that stops half way through deleting is worse than one that leaves a jar.
                LogSkipped(relative, e.Message);
            }
        }

        return removed;
    }

    /// <summary>Moves the fetched files over the game folder.</summary>
    private void MoveIn(string incoming)
    {
        foreach (var source in Directory.EnumerateFiles(incoming, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(incoming, source);
            var target = FileSystem.PathCombine(_gameRoot, relative);

            FileSystem.EnsureFilePathExists(target);

            File.Move(source, target, overwrite: true);
        }
    }

    /// <summary>Extracts the new version's overrides and reports what each folder wrote.</summary>
    private List<(string Name, List<string> Paths)> ApplyOverrides(string packFile)
    {
        var root = PackTypeDetector.Detect(packFile).Root;

        var written = new List<(string, List<string>)>();

        foreach (var folder in new[] { "overrides", "client-overrides" })
        {
            var extracted = MMCZip.ExtractDir(packFile, root + folder, _gameRoot) ?? [];

            written.Add((folder, extracted.Select(p => Path.GetRelativePath(_gameRoot, p)).ToList()));
        }

        return written;
    }

    /// <summary>Points the instance's settings at the version it now holds.</summary>
    /// <remarks>
    /// LAST, deliberately. If anything above failed the instance still claims to be the old version,
    /// which is both true of most of its files and the state a retry needs to work from.
    /// </remarks>
    private void RecordNewVersion(ModrinthPackManifest manifest)
    {
        _instanceConfig.Set("ManagedPackVersionID", _version.FileId);
        _instanceConfig.Set("ManagedPackVersionName", manifest.VersionId);
        _instanceConfig.Set("ManagedPackName", manifest.Name);
    }

    private void LogSkipped(string path, string why) => SetStatus($"Left {path} alone: {why}");

    /// <summary>Every path the pack's override folders would write, without extracting them.</summary>
    private static List<string> OverridePathsIn(string packFile)
    {
        var root = PackTypeDetector.Detect(packFile).Root;

        using var archive = System.IO.Compression.ZipFile.OpenRead(packFile);

        var paths = new List<string>();

        foreach (var folder in new[] { "overrides/", "client-overrides/" })
        {
            var prefix = root + folder;

            paths.AddRange(archive.Entries
                .Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length != 0)
                .Select(e => e.FullName[prefix.Length..]));
        }

        return paths;
    }

    private static string ManifestPathIn(string packFile)
    {
        // Extracted beside the pack, because the ledger keeps the bytes as published.
        var folder = FileSystem.PathCombine(Path.GetTempPath(), $"el-update-manifest-{Guid.NewGuid():N}");

        MMCZip.ExtractDir(packFile, PackTypeDetector.Detect(packFile).Root, folder);

        return FileSystem.PathCombine(folder, PackLedgerStore.ManifestName);
    }

    private static ModrinthPackManifest ReadManifest(string packFile)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(packFile);

        var root = PackTypeDetector.Detect(packFile).Root;

        var entry = archive.GetEntry(root + PackLedgerStore.ManifestName)
                    ?? throw new TaskFailedException("The downloaded pack has no manifest in it.");

        using var stream = entry.Open();
        using var memory = new MemoryStream();

        stream.CopyTo(memory);

        return ModrinthPack.Parse(memory.ToArray());
    }

    private static async Task DownloadToAsync(
        HttpClient client,
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new TaskFailedException(
                $"Could not download the pack: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(destination);

        await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
