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
 * Ported from technic/TechnicPackProcessor.cpp -- the step that turns an extracted Technic pack (its
 * files already sitting under staging/minecraft) into an instance: work out the components from the
 * pack's version metadata, write the pack profile and instance.cfg. It lives in Launch, not
 * ModPlatform, because it drives instance I/O (the pack profile, jar mods, instance.cfg); the pure
 * piece it leans on -- reading a version.json into a component list -- is TechnicVersionJson over in
 * ModPlatform.
 *
 * A Technic pack declares its loader in one of three shapes, and the processor picks whichever it
 * finds:
 *   - bin/modpack.jar containing a version.json (the modern shape) -> TechnicVersionJson,
 *   - bin/modpack.jar with no version.json (the pre-Forge shape) -> the jar itself is a jar mod, over
 *     the Minecraft version the search gave us, plus a Forge component read from forgeversion.properties,
 *   - bin/version.json on disk (a Solder pack) -> TechnicVersionJson,
 *   - none of the above -> the "Vanilla" pack, just Minecraft.
 * The isSolder flag upstream is unused, so it is not part of this API.
 */

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>Builds an instance from an extracted Technic pack.</summary>
public static class TechnicPackBuilder
{
    /// <summary>
    /// Turns a Technic pack already extracted into <c>staging/minecraft</c> into an instance: the pack
    /// profile (Minecraft plus whatever loader the pack names) and instance.cfg. Throws
    /// <see cref="LauncherException"/> when a jar-mod modpack.jar carries no Minecraft version and none
    /// was supplied, matching upstream's "but Minecraft version is unknown".
    /// </summary>
    /// <param name="minecraftVersion">
    /// The Minecraft version the pack search reported, used only when the pack's own metadata does not
    /// name one (a jar-mod modpack.jar, or the Vanilla pack).
    /// </param>
    public static void BuildFromStaging(
        InstancePaths paths,
        RuntimeContext runtimeContext,
        string instanceName,
        string minecraftVersion = "",
        string iconKey = "default")
    {
        ArgumentNullException.ThrowIfNull(paths);

        var modpackJar = FileSystem.PathCombine(paths.BinRoot, "modpack.jar");
        var versionJsonPath = FileSystem.PathCombine(paths.BinRoot, "version.json");

        var profile = new PackProfile(runtimeContext);

        string? versionData;
        var fmlMinecraftVersion = string.Empty;

        if (File.Exists(modpackJar))
        {
            using var zip = ZipFile.OpenRead(modpackJar);

            var versionEntry = zip.GetEntry("version.json");

            if (versionEntry is null)
            {
                // No version.json inside: the jar itself is a jar mod, laid over a known Minecraft
                // version, and any Forge coordinates come from forgeversion.properties.
                if (minecraftVersion.Length == 0)
                {
                    throw new LauncherException(
                        "Could not find \"version.json\" inside \"bin/modpack.jar\", but Minecraft version is unknown.");
                }

                profile.SetComponentVersion(PackComponents.MinecraftUid, minecraftVersion, important: true);
                JarModInstaller.Install(paths, profile, [modpackJar]);

                if (zip.GetEntry("forgeversion.properties") is { } forgeEntry)
                {
                    profile.SetComponentVersion(PackComponents.ForgeUid, ForgeVersionFromProperties(forgeEntry));
                }

                Finish(paths, profile, instanceName, iconKey);
                return;
            }

            // fmlversion.properties, when present, names the Minecraft version an old FML version.json
            // omits from inheritsFrom.
            if (zip.GetEntry("fmlversion.properties") is { } fmlEntry)
            {
                var ini = new IniFile();
                ini.LoadFromBytes(ReadAll(fmlEntry));
                fmlMinecraftVersion = ini.GetString("fmlbuild.mcversion");
            }

            versionData = Encoding.UTF8.GetString(ReadAll(versionEntry));
        }
        else if (File.Exists(versionJsonPath))
        {
            versionData = File.ReadAllText(versionJsonPath);
        }
        else
        {
            // The "Vanilla" pack the search code excludes: no bin at all, just Minecraft.
            profile.SetComponentVersion(PackComponents.MinecraftUid, minecraftVersion, important: true);
            Finish(paths, profile, instanceName, iconKey);
            return;
        }

        // Json (and DetectComponents) throw JsonException, itself a LauncherException carrying a
        // "Could not understand version.json" message, so a bad file surfaces without extra wrapping.
        var root = Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(versionData), "version.json"));

        foreach (var component in TechnicVersionJson.DetectComponents(root, fmlMinecraftVersion))
        {
            profile.SetComponentVersion(component.Uid, component.Version, component.Important);
        }

        Finish(paths, profile, instanceName, iconKey);
    }

    /// <summary>Reads the four-part Forge version out of a forgeversion.properties entry.</summary>
    private static string ForgeVersionFromProperties(ZipArchiveEntry entry)
    {
        var ini = new IniFile();
        ini.LoadFromBytes(ReadAll(entry));

        var major = ini.GetString("forge.major.number");
        var minor = ini.GetString("forge.minor.number");
        var revision = ini.GetString("forge.revision.number");
        var build = ini.GetString("forge.build.number");

        if (major.Length == 0 || minor.Length == 0 || revision.Length == 0 || build.Length == 0)
        {
            throw new LauncherException("Invalid \"forgeversion.properties\".");
        }

        return $"{major}.{minor}.{revision}.{build}";
    }

    private static void Finish(InstancePaths paths, PackProfile profile, string instanceName, string iconKey)
    {
        profile.Save(paths.PackProfilePath);

        var settings = new IniSettingsObject(paths.ConfigPath);
        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);
        settings.Set("InstanceType", "OneSix");
        settings.Set("name", instanceName);

        if (iconKey != "default")
        {
            settings.Set("iconKey", iconKey);
        }
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }
}

/// <summary>
/// Extracts one or more Technic archives into the staging game folder and builds the instance. A
/// single-zip pack has one archive; a Solder pack has one per mod, layered in order (a later archive
/// overwriting an earlier one where they collide), exactly as upstream extracts them.
/// </summary>
/// <remarks>
/// Upstream chmods every extracted file +rw (dirs +rwx) after unzipping; that is a Unix-permissions
/// workaround for a QuaZip quirk. .NET's extractor writes files the current user can already read and
/// write, so the fix-up is dropped.
/// </remarks>
public static class TechnicPackStager
{
    public static void StageAndBuild(
        InstancePaths paths,
        RuntimeContext runtimeContext,
        IEnumerable<string> archivePaths,
        string instanceName,
        string minecraftVersion = "",
        string iconKey = "default")
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(archivePaths);

        var extractDir = paths.GameRoot;
        FileSystem.EnsureFolderPathExists(extractDir);

        foreach (var archive in archivePaths)
        {
            if (MMCZip.ExtractDir(archive, extractDir) is null)
            {
                throw new LauncherException($"Failed to extract the modpack archive {archive}.");
            }
        }

        TechnicPackBuilder.BuildFromStaging(paths, runtimeContext, instanceName, minecraftVersion, iconKey);
    }
}

/// <summary>Downloads a Technic single-zip pack and builds it into the staging instance.</summary>
public sealed class TechnicSingleZipInstallTask : LauncherTask, IInstanceTask
{
    private readonly string _sourceUrl;

    private readonly string _minecraftVersion;

    private readonly RuntimeContext _runtimeContext;

    private readonly HttpClient _client;

    private readonly string _iconKey;

    private readonly string _instanceName;

    public TechnicSingleZipInstallTask(
        string sourceUrl,
        string minecraftVersion,
        RuntimeContext runtimeContext,
        HttpClient client,
        string instanceName = "",
        string iconKey = "default",
        string group = "")
        : base($"Installing modpack {instanceName}")
    {
        _sourceUrl = sourceUrl ?? throw new ArgumentNullException(nameof(sourceUrl));
        _minecraftVersion = minecraftVersion ?? string.Empty;
        _runtimeContext = runtimeContext;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _instanceName = instanceName;
        _iconKey = iconKey;
        Group = group;
    }

    public string StagingPath { get; set; } = string.Empty;

    string IInstanceTask.Name => _instanceName;

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

        SetStatus("Downloading modpack");

        var archivePath = FileSystem.PathCombine(StagingPath, "pack.zip");
        await TechnicDownload.ToFileAsync(_client, _sourceUrl, archivePath, md5: null, cancellationToken)
            .ConfigureAwait(false);

        SetStatus("Extracting modpack");

        TechnicPackStager.StageAndBuild(
            new InstancePaths(StagingPath), _runtimeContext, [archivePath], _instanceName, _minecraftVersion, _iconKey);

        FileSystem.DeletePath(archivePath);
    }
}

/// <summary>
/// Resolves a Solder build's mod list, downloads every mod (each md5-checked), then layers them into
/// the staging instance and builds it. Ported from technic/SolderPackInstallTask.
/// </summary>
public sealed class TechnicSolderInstallTask : LauncherTask, IInstanceTask
{
    private readonly string _solderUrl;

    private readonly string _pack;

    private readonly string _version;

    private string _minecraftVersion;

    private readonly RuntimeContext _runtimeContext;

    private readonly HttpClient _client;

    private readonly string _iconKey;

    private readonly string _instanceName;

    public TechnicSolderInstallTask(
        string solderUrl,
        string pack,
        string version,
        string minecraftVersion,
        RuntimeContext runtimeContext,
        HttpClient client,
        string instanceName = "",
        string iconKey = "default",
        string group = "")
        : base($"Installing modpack {instanceName}")
    {
        _solderUrl = solderUrl ?? throw new ArgumentNullException(nameof(solderUrl));
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _minecraftVersion = minecraftVersion ?? string.Empty;
        _runtimeContext = runtimeContext;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _instanceName = instanceName;
        _iconKey = iconKey;
        Group = group;
    }

    public string StagingPath { get; set; } = string.Empty;

    string IInstanceTask.Name => _instanceName;

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

        SetStatus("Resolving modpack files");

        var manifest = await _client
            .GetByteArrayAsync(TechnicSolder.BuildUrl(_solderUrl, _pack, _version), cancellationToken)
            .ConfigureAwait(false);

        SolderPackBuild build;
        try
        {
            build = TechnicSolder.LoadPackBuild(Json.RequireObject(Json.RequireDocument(manifest, "Solder build")));
        }
        catch (JsonException e)
        {
            throw new LauncherException($"Could not understand pack manifest:\n{e.Message}");
        }

        // A build names the Minecraft version the search could only guess at.
        if (build.Minecraft.Length != 0)
        {
            _minecraftVersion = build.Minecraft;
        }

        SetStatus("Downloading modpack");

        var archives = new List<string>(build.Mods.Count);

        for (var i = 0; i < build.Mods.Count; i++)
        {
            var mod = build.Mods[i];
            var path = FileSystem.PathCombine(StagingPath, $"{i}.zip");

            await TechnicDownload.ToFileAsync(_client, mod.Url, path, mod.Md5, cancellationToken).ConfigureAwait(false);
            archives.Add(path);
        }

        SetStatus("Extracting modpack");

        TechnicPackStager.StageAndBuild(
            new InstancePaths(StagingPath), _runtimeContext, archives, _instanceName, _minecraftVersion, _iconKey);

        foreach (var archive in archives)
        {
            FileSystem.DeletePath(archive);
        }
    }
}

/// <summary>Downloads a file, and — when the source publishes one — checks its md5, all Solder offers.</summary>
internal static class TechnicDownload
{
    public static async Task ToFileAsync(
        HttpClient client, string url, string destination, string? md5, CancellationToken cancellationToken)
    {
        using (var response = await client
                   .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            await using var output = File.Create(destination);
            await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        if (md5 is { Length: > 0 })
        {
            var actual = Convert.ToHexString(MD5.HashData(await File.ReadAllBytesAsync(destination, cancellationToken)
                .ConfigureAwait(false)));

            if (!string.Equals(actual, md5, StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException($"Checksum mismatch for {url}: expected {md5}, got {actual.ToLowerInvariant()}.");
            }
        }
    }
}
