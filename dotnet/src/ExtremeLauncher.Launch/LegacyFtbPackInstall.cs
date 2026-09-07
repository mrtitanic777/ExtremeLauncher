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
 * Ported from legacy_ftb/PackInstallTask.cpp -- the orchestration that turns a downloaded legacy FTB
 * archive into an instance. It lives in Launch, not ModPlatform, because it drives instance I/O
 * (staging, the pack profile, jar mods, instance.cfg); the pure pieces it leans on -- the archive URL
 * and the Forge-coordinate reader -- are LegacyFtbInstall over in ModPlatform.
 *
 * BuildFromArchive is split out from the download so it can be tested with a hand-built archive and no
 * network: extract, move the game folder up, then work out the install method the way upstream does --
 * a Forge pack.json, or an instMods jar-mod folder, or neither, which is a failure.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>Builds an instance from a legacy FTB pack archive.</summary>
public static class LegacyFtbPackBuilder
{
    /// <summary>
    /// Turns an extracted-into-staging FTB archive into an instance: the game folder, the pack profile
    /// (Minecraft plus either a Forge component or jar mods) and instance.cfg. Throws
    /// <see cref="LauncherException"/> when neither a Forge pack.json nor an instMods folder is present,
    /// matching upstream's "No installation method found".
    /// </summary>
    public static void BuildFromArchive(
        InstancePaths paths,
        string archivePath,
        LegacyFtbModpack pack,
        RuntimeContext runtimeContext,
        string iconKey = "default",
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(pack);

        var unzipDir = FileSystem.PathCombine(paths.InstanceRoot, "unzip");

        if (MMCZip.ExtractDir(archivePath, unzipDir) is null)
        {
            throw new LauncherException($"Failed to extract the modpack archive {archivePath}.");
        }

        // The archive's minecraft/ folder is the instance's game folder.
        var unpackedGame = FileSystem.PathCombine(unzipDir, "minecraft");
        var gameDir = FileSystem.PathCombine(paths.InstanceRoot, "minecraft");

        if (Directory.Exists(unpackedGame) && !FileSystem.Move(unpackedGame, gameDir))
        {
            throw new LauncherException("Failed to move the unpacked Minecraft folder.");
        }

        var profile = new PackProfile(runtimeContext);
        profile.SetComponentVersion("net.minecraft", pack.McVersion, important: true);

        var installed = false;

        // A Forge pack names its loader in minecraft/pack.json; once used, the file is thrown away.
        var packJsonPath = FileSystem.PathCombine(gameDir, "pack.json");

        if (File.Exists(packJsonPath)
            && LegacyFtbInstall.ForgeComponentVersion(File.ReadAllText(packJsonPath), pack.McVersion) is { } forgeVersion)
        {
            profile.SetComponentVersion("net.minecraftforge", forgeVersion);
            File.Delete(packJsonPath);
            installed = true;
        }

        // An older pack ships jar mods in instMods/ instead.
        var instMods = FileSystem.PathCombine(unzipDir, "instMods");

        if (Directory.Exists(instMods))
        {
            var jars = Directory.EnumerateFiles(instMods, "*.jar").ToList();

            if (jars.Count != 0)
            {
                JarModInstaller.Install(paths, profile, jars);
            }

            installed = true;
        }

        FileSystem.DeletePath(unzipDir);

        if (!installed)
        {
            throw new LauncherException("No installation method found for this modpack.");
        }

        profile.Save(paths.PackProfilePath);

        var settings = new IniSettingsObject(FileSystem.PathCombine(paths.InstanceRoot, "instance.cfg"));
        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);
        settings.Set("InstanceType", "OneSix");
        settings.Set("name", displayName is { Length: > 0 } ? displayName : pack.Name);

        // The FTB logo stands in for the default icon, as upstream substitutes.
        settings.Set("iconKey", iconKey == "default" ? "ftb_logo" : iconKey);
    }
}

/// <summary>Downloads a legacy FTB pack and builds it into the staging instance.</summary>
public sealed class LegacyFtbPackInstallTask : LauncherTask, IInstanceTask
{
    private readonly LegacyFtbModpack _pack;

    private readonly string _version;

    private readonly RuntimeContext _runtimeContext;

    private readonly HttpClient _client;

    private readonly string? _baseUrl;

    private readonly string _iconKey;

    private readonly string _instanceName;

    public LegacyFtbPackInstallTask(
        LegacyFtbModpack pack,
        string version,
        RuntimeContext runtimeContext,
        HttpClient client,
        string instanceName = "",
        string? baseUrl = null,
        string iconKey = "default",
        string group = "")
        : base($"Installing modpack {pack?.Name}")
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _runtimeContext = runtimeContext;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _instanceName = instanceName;
        _baseUrl = baseUrl;
        _iconKey = iconKey;
        Group = group;
    }

    public string StagingPath { get; set; } = string.Empty;

    string IInstanceTask.Name => _instanceName.Length != 0 ? _instanceName : _pack.Name;

    public string Group { get; }

    public bool ShouldOverride => false;

    public string OriginalInstanceId => string.Empty;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (StagingPath.Length == 0)
        {
            throw new LauncherException("No staging path was set.");
        }

        FileSystem.EnsureFolderPathExists(StagingPath);

        SetStatus($"Downloading {_pack.Name}");

        var url = LegacyFtbInstall.ArchiveUrl(_pack, _version, _baseUrl);
        var archivePath = FileSystem.PathCombine(StagingPath, "pack.zip");

        using (var response = await _client
                   .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            await using var output = File.Create(archivePath);
            await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        SetStatus($"Installing {_pack.Name}");

        LegacyFtbPackBuilder.BuildFromArchive(
            new InstancePaths(StagingPath), archivePath, _pack, _runtimeContext, _iconKey, _instanceName);

        FileSystem.DeletePath(archivePath);
    }
}
