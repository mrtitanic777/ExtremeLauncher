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
 * The top-level ATLauncher install, ported from ATLPackInstallTask. It fetches the version manifest,
 * lays down the config archive, then works out which mods to fetch and does with each what its type
 * asks: a plain mod dropped in its folder, a jar mod installed as a component, an archive extracted, or
 * one file decompiled out. Everything the earlier waves built -- the manifest parser (AtlPackManifest),
 * the config installer (AtlConfigInstaller), the mod planner (AtlModPlanner), the extractor
 * (AtlModExtractor) and the instance staging (AtlPackBuilder) -- meets here.
 *
 * A FRESH INSTALL, not an update, so the on-update cleaner (AtlUpdateCleaner) is not run: a new
 * instance has nothing to clear. Optional mods take their default selection (the recommended set);
 * choosing them, and the share-code flow, are UI concerns above this task. A mod the author blocked
 * from third-party download fails the install with a message naming it, as the Flame import does.
 */

using System.Security.Cryptography;

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>Installs an ATLauncher pack from its safe name and version into the staging instance.</summary>
public sealed class AtlInstallTask : LauncherTask, IInstanceTask
{
    private readonly string _packName;

    private readonly string _packSafeName;

    private readonly string _versionName;

    private readonly HttpClient _client;

    private readonly RuntimeContext _runtimeContext;

    private readonly string _server;

    private readonly string _iconKey;

    private readonly string _instanceName;

    public AtlInstallTask(
        string packName,
        string packSafeName,
        string versionName,
        HttpClient client,
        RuntimeContext runtimeContext,
        string instanceName = "",
        string iconKey = "default",
        string group = "",
        string? serverBaseUrl = null)
        : base($"Installing modpack {packName}")
    {
        _packName = packName ?? throw new ArgumentNullException(nameof(packName));
        _packSafeName = packSafeName ?? throw new ArgumentNullException(nameof(packSafeName));
        _versionName = versionName ?? throw new ArgumentNullException(nameof(versionName));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _runtimeContext = runtimeContext;
        _instanceName = instanceName;
        _iconKey = iconKey;
        _server = serverBaseUrl ?? BuildConfig.Instance.AtlDownloadServerUrl;
        Group = group;
    }

    public string StagingPath { get; set; } = string.Empty;

    string IInstanceTask.Name => _instanceName.Length != 0 ? _instanceName : _packName;

    public string Group { get; }

    public bool ShouldOverride => false;

    public string OriginalInstanceId => string.Empty;

    /// <summary>How many files were fetched, once the install has run.</summary>
    public int DownloadedCount { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (StagingPath.Length == 0)
        {
            throw new LauncherException("No staging path was set.");
        }

        FileSystem.EnsureFolderPathExists(StagingPath);

        SetStatus("Resolving modpack files");

        var manifestBytes = await _client
            .GetByteArrayAsync(AtlUrls.VersionManifest(_server, _packSafeName, _versionName), cancellationToken)
            .ConfigureAwait(false);

        AtlPackVersion version;

        try
        {
            version = AtlPackManifest.LoadVersion(
                Json.RequireObject(Json.RequireDocument(manifestBytes, "Configs.json")));
        }
        catch (JsonException e)
        {
            throw new LauncherException($"Could not understand pack manifest:\n{e.Message}");
        }

        var paths = new InstancePaths(StagingPath);

        // The config archive, when the version has one, is laid straight over the game folder.
        if (version.Configs.Sha1.Length != 0 || version.Configs.FileSize > 0)
        {
            SetStatus("Downloading configs");

            await AtlConfigInstaller.InstallAsync(
                _client, AtlUrls.ConfigArchive(_server, _packSafeName, _versionName),
                paths.GameRoot, version.Configs.Sha1, cancellationToken).ConfigureAwait(false);
        }

        var selectedOptional = new HashSet<string>(
            AtlPackManifest.GetDefaultSelection(version.Mods).Where(m => m.Optional).Select(m => m.Name),
            StringComparer.Ordinal);

        var plan = AtlModPlanner.Build(version.Mods, selectedOptional, version.Minecraft, _server);

        if (plan.Blocked.Count != 0)
        {
            throw new LauncherException(BlockedMessage(plan.Blocked));
        }

        SetStatus($"Downloading {plan.Downloads.Count} mods");

        var jarMods = new List<string>();
        var temps = new List<string>();

        try
        {
            foreach (var download in plan.Downloads)
            {
                if (download is { Action: AtlModAction.Place, IsJarMod: false })
                {
                    var target = FileSystem.PathCombine(StagingPath, download.TargetPath!);
                    FileSystem.EnsureFilePathExists(target);

                    await DownloadAsync(download.Url, target, download.Md5, cancellationToken).ConfigureAwait(false);
                    DownloadedCount++;

                    continue;
                }

                // Jar mods, extracts and decomps are fetched to a temp file first — a jar mod is then
                // installed as a component, the others unpacked into place.
                var temp = FileSystem.PathCombine(Path.GetTempPath(), $"el-atlmod-{Guid.NewGuid():N}");
                temps.Add(temp);

                await DownloadAsync(download.Url, temp, download.Md5, cancellationToken).ConfigureAwait(false);
                DownloadedCount++;

                switch (download.Action)
                {
                    case AtlModAction.Place:
                        jarMods.Add(temp);
                        break;

                    case AtlModAction.Extract:
                        AtlModExtractor.Extract(paths, download.Mod, temp, version.Minecraft);
                        break;

                    case AtlModAction.Decompile:
                        AtlModExtractor.Decompile(paths, download.Mod, temp, version.Minecraft);
                        break;
                }
            }

            SetStatus($"Installing {_packName}");

            AtlPackBuilder.BuildInstance(
                paths, version, _runtimeContext, _packName, _packSafeName, _versionName, jarMods, _iconKey, _instanceName);
        }
        finally
        {
            foreach (var temp in temps)
            {
                FileSystem.DeletePath(temp);
            }
        }
    }

    private async Task DownloadAsync(string url, string destination, string md5, CancellationToken cancellationToken)
    {
        using (var response = await _client
                   .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            await using var output = File.Create(destination);
            await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        if (md5 is { Length: > 0 })
        {
            string actual;

            await using (var input = File.OpenRead(destination))
            {
                actual = Convert.ToHexString(await MD5.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            }

            if (!string.Equals(actual, md5, StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException($"Checksum mismatch for {url}: expected {md5}, got {actual.ToLowerInvariant()}.");
            }
        }
    }

    private static string BlockedMessage(IReadOnlyList<AtlVersionMod> blocked)
        => "These mods are not available for download in third-party launchers and must be added by hand:\n"
           + string.Join('\n', blocked.Select(m => $"  {m.Name} ({m.Url})"));
}
