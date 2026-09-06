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
 * Ported from modplatform/modrinth/ModrinthInstanceCreationTask.cpp.
 *
 * IMPORTING A .mrpack, which is how most people get a modpack. Every piece of this was already ported
 * and tested in wave 8 -- the manifest parser, the component mapping, the path-traversal guard, the
 * archive detector -- and none of it was reachable, because nothing turned a file on disk into an
 * instance.
 *
 * A .mrpack is a zip holding:
 *
 *   modrinth.index.json    the manifest: name, version, dependencies, and a list of files to fetch
 *   overrides/             files copied into the instance as they are
 *   client-overrides/      the same, applied afterwards so they win, for client-only files
 *
 * THE MODS ARE NOT IN THE ZIP. The manifest lists them with a URL and a hash and the launcher fetches
 * them, which is what makes a .mrpack a few kilobytes rather than a few hundred megabytes -- and why
 * importing one needs a network although creating a vanilla instance does not.
 *
 * IT LIVES IN Launch, NOT ModPlatform, because writing an instance needs PackProfile and ModPlatform
 * sits below Meta. That layering is right: parsing a pack format should not require the component
 * system.
 */

using System.IO.Compression;
using System.Security.Cryptography;
using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Net;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class ModrinthImportTask : LauncherTask, IInstanceTask
{
    private readonly string _packPath;

    private readonly HttpClient? _client;

    private readonly string _requestedName;

    /// <summary>The platform's ids, when the pack came from the browser rather than a file.</summary>
    private readonly string _managedId;

    private readonly string _managedVersionId;

    /// <summary>The manifest exactly as published, kept for the ledger.</summary>
    private byte[] _manifestBytes = [];

    private string _instanceName = string.Empty;

    /// <param name="client">Null imports without downloading, which leaves the mods folder empty.</param>
    /// <param name="name">What to call the instance, or empty to use the pack's own name.</param>
    private readonly LauncherPaths? _paths;

    private readonly string _metaUrl;

    /// <param name="paths">
    /// With a client and a meta url, the imported instance RESOLVES its dependencies -- see
    /// ComponentResolution. A pack records the loader and the Minecraft version and nothing else, so
    /// without this an imported instance is missing the same org.lwjgl3 a created one was.
    /// </param>
    public ModrinthImportTask(
        string packPath,
        HttpClient? client = null,
        string name = "",
        string group = "",
        LauncherPaths? paths = null,
        string metaUrl = "",
        string managedId = "",
        string managedVersionId = "")
        : base("Importing pack")
    {
        _managedId = managedId;
        _managedVersionId = managedVersionId;
        _packPath = packPath;
        _client = client;
        _requestedName = name;
        _paths = paths;
        _metaUrl = metaUrl;

        Group = group;
    }

    public string StagingPath { get; set; } = string.Empty;

    /// <inheritdoc/>
    /// <remarks>Explicit: LauncherTask.Name is the task's display name. See InstanceCopyTask.</remarks>
    string IInstanceTask.Name => _instanceName;

    public string Group { get; }

    public bool ShouldOverride => false;

    public string OriginalInstanceId => string.Empty;

    /// <summary>The pack's own name, known once the manifest has been read.</summary>
    public string PackName { get; private set; } = string.Empty;

    /// <summary>How many files the manifest asked for.</summary>
    public int FileCount { get; private set; }

    /// <summary>How many were fetched. Zero without a client.</summary>
    public int DownloadedCount { get; private set; }

    private string GameRoot => FileSystem.PathCombine(StagingPath, "minecraft");

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (StagingPath.Length == 0)
        {
            throw new LauncherException("No staging path was set.");
        }

        if (!File.Exists(_packPath))
        {
            throw new LauncherException($"There is no file at {_packPath}.");
        }

        SetStatus("Reading the pack");

        var detection = PackTypeDetector.Detect(_packPath);

        if (detection.Type != ModpackType.Modrinth)
        {
            /*
             * Named rather than "unsupported". A user who picked a CurseForge zip has a real question --
             * "does this launcher do CurseForge?" -- and the answer is more useful than the refusal.
             */
            throw new LauncherException(
                detection.Type == ModpackType.Unknown
                    ? "That file is not a modpack this launcher recognises."
                    : $"That is a {detection.Type} pack. Only Modrinth packs can be imported so far.");
        }

        ModrinthPackManifest manifest;

        using (var archive = ZipFile.OpenRead(_packPath))
        {
            var entry = archive.GetEntry(detection.Root + PackTypeDetector.ModrinthMarker)
                ?? throw new LauncherException("The pack has no modrinth.index.json.");

            using var stream = entry.Open();
            using var memory = new MemoryStream();

            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);

            _manifestBytes = memory.ToArray();

            manifest = ModrinthPack.Parse(_manifestBytes);
        }

        PackName = manifest.Name;
        _instanceName = _requestedName.Length != 0 ? _requestedName : manifest.Name;

        if (_instanceName.Length == 0)
        {
            throw new LauncherException("The pack does not name itself, and no name was given.");
        }

        FileSystem.EnsureFolderPathExists(GameRoot);

        /*
         * The components before the files. An import that dies part way through downloads then leaves
         * an instance that at least describes itself: what is missing is mods, which a user can see.
         * The other order leaves one with no Minecraft version, which they cannot.
         */
        WriteInstanceFiles(manifest);

        SetStatus("Extracting the pack's own files");

        ExtractOverrides(detection.Root);

        // Kept byte for byte as published, so a later update diffs against what the author actually
        // wrote rather than against this port's idea of it.
        PackLedgerStore.WriteManifest(StagingPath, _manifestBytes);

        FileCount = manifest.Files.Count;

        if (_client is null || FileCount == 0)
        {
            // Still resolved: a pack with no files to fetch is still an instance that has to run.
            await ResolveComponentsAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        await DownloadFilesAsync(manifest, cancellationToken).ConfigureAwait(false);

        await ResolveComponentsAsync(cancellationToken).ConfigureAwait(false);
    }

    private void WriteInstanceFiles(ModrinthPackManifest manifest)
    {
        var settings = new IniSettingsObject(FileSystem.PathCombine(StagingPath, "instance.cfg"));

        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);

        settings.Set("InstanceType", "OneSix");
        settings.Set("name", _instanceName);
        settings.Set("iconKey", "default");

        /*
         * WHERE THIS INSTANCE CAME FROM. Ported from ModrinthInstanceCreationTask.cpp:237, and it was
         * the missing half of a feature: the six ManagedPack* settings had been REGISTERED since the
         * instance-settings wave and nothing ever wrote one, so an imported pack forgot it had ever
         * been a pack the moment it finished importing.
         *
         * THE ID AND VERSION ID COME FROM THE CALLER, and are EMPTY for a file import -- a .mrpack on
         * disk does not carry its own Modrinth project id. Only the pack browser knows it, so a pack
         * installed from there can be checked for updates and one imported from a file cannot. That
         * is the difference InstanceSettings.CanCheckForPackUpdates exists to express.
         */
        settings.RegisterSetting("ManagedPack", false);
        settings.RegisterSetting("ManagedPackType", string.Empty);
        settings.RegisterSetting("ManagedPackID", string.Empty);
        settings.RegisterSetting("ManagedPackName", string.Empty);
        settings.RegisterSetting("ManagedPackVersionID", string.Empty);
        settings.RegisterSetting("ManagedPackVersionName", string.Empty);

        settings.Set("ManagedPack", true);
        settings.Set("ManagedPackType", "modrinth");
        settings.Set("ManagedPackID", _managedId);

        // The PACK's name, not the instance's -- somebody who renamed the instance to "modded 1.20"
        // still wants to know it is Fabulously Optimized underneath.
        settings.Set("ManagedPackName", manifest.Name);
        settings.Set("ManagedPackVersionID", _managedVersionId);
        settings.Set("ManagedPackVersionName", manifest.VersionId);

        /*
         * PackComponents.FromModrinth was ported and tested in wave 8 and had never been used by
         * anything. It maps the manifest's dependency names onto metadata uids -- "fabric-loader" to
         * net.fabricmc.fabric-loader, and so on.
         */
        var components = PackComponents.FromModrinth(manifest);

        /*
         * CHECKED ON THE VERSION, not the count. FromModrinth ALWAYS returns at least the Minecraft
         * component -- with an empty version when the manifest named none -- so a count check is a
         * branch that can never be taken, and a pack with no dependencies imported happily into an
         * instance pinned to Minecraft "".
         *
         * Same shape as the other dead guards this port has found: the condition looked like the
         * question and was not it.
         */
        // PackComponent is a value type, so "not found" is a default with an empty uid.
        var minecraft = components.FirstOrDefault(
            c => string.Equals(c.Uid, PackComponents.MinecraftUid, StringComparison.Ordinal));

        if (minecraft.Version is not { Length: > 0 })
        {
            throw new LauncherException("The pack does not say which Minecraft version it is for.");
        }

        var profile = new PackProfile(LauncherService.CurrentRuntimeContext());

        foreach (var component in components)
        {
            profile.SetComponentVersion(
                component.Uid,
                component.Version,
                important: string.Equals(component.Uid, PackComponents.MinecraftUid, StringComparison.Ordinal));
        }

        var packProfilePath = FileSystem.PathCombine(StagingPath, "mmc-pack.json");

        if (!profile.Save(packProfilePath))
        {
            throw new LauncherException("Could not write the instance's component list.");
        }

        _resolveProfile = profile;
        _resolvePackPath = packProfilePath;
    }

    private PackProfile? _resolveProfile;

    private string _resolvePackPath = string.Empty;

    /// <summary>
    /// Adds whatever the pack's components require.
    /// </summary>
    /// <remarks>
    /// A .mrpack names its Minecraft version and its loader and nothing else, so an imported instance
    /// starts out exactly as incomplete as a created one -- missing org.lwjgl3 and everything else the
    /// game needs but nobody lists. See ComponentResolution.
    ///
    /// Best effort: an import without a client already writes an instance with no mods and says so,
    /// and refusing the whole import over this would be worse.
    /// </remarks>
    private async Task ResolveComponentsAsync(CancellationToken cancellationToken)
    {
        if (_resolveProfile is null || _paths is null || _client is null || _metaUrl.Length == 0)
        {
            return;
        }

        SetStatus("Working out what else this pack needs");

        await ComponentResolution.ApplyAsync(
            _resolveProfile,
            _resolvePackPath,
            FileSystem.PathCombine(StagingPath, "patches"),
            _paths,
            _client,
            _metaUrl,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the pack's own files into the instance.
    /// </summary>
    /// <remarks>
    /// "overrides" then "client-overrides", in that order, because the client ones are meant to win --
    /// which is upstream's order and the reason a pack can ship a server config and a different client
    /// one under the same name.
    /// </remarks>
    private void ExtractOverrides(string root)
    {
        foreach (var folder in new[] { "overrides", "client-overrides" })
        {
            // Null means the archive could not be opened at all, which the manifest read above has
            // already ruled out; an empty list means the pack simply has no such folder.
            var extracted = MMCZip.ExtractDir(_packPath, root + folder, GameRoot) ?? [];

            /*
             * THE LEDGER, which is the record that makes a later update possible at all. Nothing
             * about a file on disk says whether the pack put it there or the player did, so without
             * this an update can only choose between leaving stale mods behind and deleting things
             * that were never the pack's. See PackLedger.
             *
             * Written even when the folder was empty: an EMPTY list and a MISSING list mean different
             * things -- "the pack contributed nothing here" against "nobody wrote this down".
             */
            PackLedgerStore.WriteOverrides(
                StagingPath,
                folder,
                extracted.Select(path => Path.GetRelativePath(GameRoot, path)));
        }
    }

    private async Task DownloadFilesAsync(ModrinthPackManifest manifest, CancellationToken cancellationToken)
    {
        SetStatus($"Downloading {manifest.Files.Count} files");

        var job = new NetJob("Pack files", _client!);

        foreach (var file in manifest.Files)
        {
            /*
             * CHECKED AGAIN, although Parse already refused a traversing path -- see upstream bug #12,
             * which was a real .mrpack path-traversal hole. The guard is a string comparison, and this
             * is the one place in the launcher where a destination path comes out of a file somebody
             * downloaded. Two checks on that rule is the right number.
             */
            ModrinthPack.ValidateRelativePath(file.Path);

            if (file.Downloads.Count == 0)
            {
                // An optional file may legitimately have no source; a required one that does is broken.
                if (file.Required)
                {
                    throw new LauncherException($"The pack lists “{file.Path}” with nowhere to fetch it from.");
                }

                continue;
            }

            var target = FileSystem.PathCombine(GameRoot, file.Path);

            FileSystem.EnsureFilePathExists(target);

            var download = Download.MakeFile(_client!, file.Downloads[0], target, file.Path);

            /*
             * The manifest's own sha512. A file served wrongly fails the job rather than landing in
             * somebody's mods folder -- which matters more here than anywhere else in the launcher,
             * because these URLs came from a pack file, which came from the internet.
             */
            if (file.Hash.Length != 0)
            {
                download.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA512, file.Hash));
            }

            job.AddNetAction(download);

            DownloadedCount++;
        }

        job.ProgressChanged += (_, progress) => SetProgress(progress.Current, progress.Total);
        job.StatusChanged += (_, status) => SetStatus(status);

        if (!await job.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            // Told apart from a failure, as in PackUpdateTask: NetJob reports a cancellation and a
            // dead mirror the same way, and somebody who pressed Cancel has not had an error.
            cancellationToken.ThrowIfCancellationRequested();

            throw new LauncherException($"Could not download the pack's files: {job.FailReason}");
        }
    }
}
