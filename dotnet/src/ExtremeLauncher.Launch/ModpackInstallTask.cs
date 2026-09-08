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
 * Ported in behaviour from the download half of
 * launcher/modplatform/modrinth/ModrinthInstanceCreationTask.cpp.
 *
 * INSTALLING A MODPACK FROM THE BROWSER rather than from a file somebody downloaded themselves. The
 * difference is not convenience -- it is that a pack installed this way KNOWS ITS OWN PROJECT ID, and
 * a pack imported from a file cannot (a .mrpack carries no id of its own). That id is the only thing
 * that could ever let the launcher offer a newer version.
 *
 * TWO STEPS, and the split matters: fetch the .mrpack to a temporary file, then hand it to the same
 * ModrinthImportTask a file import uses. One install path rather than two means the awkward parts --
 * component resolution, overrides, optional files, the BOM -- are solved once and cannot drift apart.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class ModpackInstallTask : LauncherTask, IInstanceTask
{
    private readonly HttpClient _client;

    private readonly IndexedPack _pack;

    private readonly IndexedVersion _version;

    private readonly string _requestedName;

    private readonly LauncherPaths? _paths;

    private readonly string _metaUrl;

    private ModrinthImportTask? _import;

    private FlameImportTask? _flameImport;

    /// <param name="name">What to call the instance, or empty to use the pack's own name.</param>
    public ModpackInstallTask(
        HttpClient client,
        IndexedPack pack,
        IndexedVersion version,
        string name = "",
        LauncherPaths? paths = null,
        string metaUrl = "")
        : base("Installing modpack")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(version);

        _client = client;
        _pack = pack;
        _version = version;
        _requestedName = name;
        _paths = paths;
        _metaUrl = metaUrl;
    }

    /// <summary>Where the instance is being built. Set by the staging task.</summary>
    public string StagingPath { get; set; } = string.Empty;

    /// <remarks>Explicit: LauncherTask.Name is the task's display name. See InstanceCopyTask.</remarks>
    string IInstanceTask.Name => _requestedName.Length != 0 ? _requestedName : _pack.Name;

    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Always false: installing a pack from the browser always makes a new instance.
    /// </summary>
    /// <remarks>
    /// UPDATING AN EXISTING ONE IN PLACE is what this flag is for, and it is the harder feature --
    /// the new pack's files have to be reconciled against what the player has since added, changed
    /// and deleted. Nothing does that yet, so claiming to would be worse than not offering it.
    /// </remarks>
    public bool ShouldOverride => false;

    public string OriginalInstanceId => string.Empty;

    /// <summary>How many of the pack's files were fetched, once the install has run.</summary>
    public int DownloadedCount => _import?.DownloadedCount ?? _flameImport?.DownloadedCount ?? 0;

    public string PackName => _import?.PackName ?? _pack.Name;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        /*
         * TWO PROVIDERS, TWO PACK FORMATS. A Modrinth file is a .mrpack; a CurseForge file is a zip of
         * manifest.json + overrides. Each has its own importer, so the download below is routed by the
         * pack's provider. Any other provider is refused rather than fed to the wrong reader.
         */
        if (_pack.Provider is not (ResourceProvider.Modrinth or ResourceProvider.Flame))
        {
            throw new TaskFailedException(
                $"Installing a {_pack.Provider} modpack from the browser is not supported yet.");
        }

        if (_version.DownloadUrl.Length == 0)
        {
            /*
             * A version with no file. Modrinth does not normally serve one, but a pack whose author
             * withdrew a file can leave the version record behind -- and "install" doing nothing at
             * all is worse than saying why.
             */
            throw new TaskFailedException($"{_pack.Name} {_version.Version} has no file to download.");
        }

        var temp = FileSystem.PathCombine(
            Path.GetTempPath(),
            $"el-modpack-{Guid.NewGuid():N}{Path.GetExtension(_version.FileName)}");

        try
        {
            SetStatus($"Downloading {_pack.Name} {_version.Version}");

            await DownloadAsync(temp, cancellationToken).ConfigureAwait(false);

            SetStatus($"Installing {_pack.Name}");

            if (_pack.Provider == ResourceProvider.Flame)
            {
                await InstallFlameAsync(temp, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await InstallModrinthAsync(temp, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            /*
             * The download is a cache, not a keepsake. Left behind it would be a 170 MB file in the
             * temp folder that nothing ever looks at again -- and on a failed install, one the user
             * did not ask for and cannot find.
             */
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // A temp file that will not delete is not worth failing an otherwise good install.
            }
        }
    }

    /// <summary>
    /// Imports the downloaded .mrpack. The project and version ids go in so the instance remembers where
    /// it came from and can later be offered an update — the whole reason the browser is a path of its
    /// own rather than a plain file import.
    /// </summary>
    private async Task InstallModrinthAsync(string archive, CancellationToken cancellationToken)
    {
        _import = new ModrinthImportTask(
            archive,
            _client,
            _requestedName,
            Group,
            _paths,
            _metaUrl,
            managedId: _pack.AddonId,
            managedVersionId: _version.FileId)
        {
            StagingPath = StagingPath,
        };

        // Progress passes straight through, so the window shows the pack's own mods downloading
        // rather than sitting at "installing" for two minutes.
        _import.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);
        _import.StatusChanged += (_, status) => SetStatus(status);

        if (!await _import.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TaskFailedException(_import.FailReason);
        }
    }

    /// <summary>
    /// Imports the downloaded CurseForge zip through <see cref="FlameImportTask"/>, building the file
    /// resolver from the CurseForge and Modrinth APIs the same way the browser's search does.
    /// </summary>
    private async Task InstallFlameAsync(string archive, CancellationToken cancellationToken)
    {
        var resolver = new FlameResolverApi(
            new FlameApi(_client, BuildConfig.Instance.FlameApiKey), new ModrinthApi(_client));

        _flameImport = new FlameImportTask(
            archive, resolver, _client, LauncherService.CurrentRuntimeContext(), _requestedName, group: Group)
        {
            StagingPath = StagingPath,
        };

        _flameImport.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);
        _flameImport.StatusChanged += (_, status) => SetStatus(status);

        if (!await _flameImport.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TaskFailedException(_flameImport.FailReason);
        }
    }

    private async Task DownloadAsync(string destination, CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(_version.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new TaskFailedException(
                $"Could not download {_version.FileName}: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(destination);

        var buffer = new byte[81920];
        long copied = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            copied += read;

            // Reported even when the server sent no length, because a pack download is the longest
            // silent stretch in the whole flow and a moving number beats a frozen one.
            SetProgress(copied, total > 0 ? total : copied);
        }
    }
}
