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
 * The decision logic of ATLPackInstallTask, ported as pure functions. The install task itself is
 * ~1,075 lines of download/extract/staging orchestration bound to Qt; two of its private helpers are
 * pure and carry the real rules, so they are worth porting and testing on their own ahead of the rest:
 *
 *   - getDirForModType: where a mod of a given type is placed inside the instance. A delivery
 *     instruction, not a category -- "mods" is the mods folder, "jar" is a jar mod, "dependency" is a
 *     Minecraft-version-specific mods subfolder, and several types are placed elsewhere (extracted,
 *     decompiled) so they resolve to no plain destination here.
 *   - detectLibrary: turning a library's server path or filename into a Gradle coordinate, so a
 *     library ATLauncher ships can line up with the same library from the metadata index.
 */

using System.Security.Cryptography;

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>Pure decision helpers ported from ATLauncher's PackInstallTask.</summary>
public static class AtlInstall
{
    /// <summary>
    /// The instance-relative folder a mod of the given type is dropped into, or <c>null</c> when the
    /// type is not placed as a plain file here — either handled elsewhere (extracted, decompiled, the
    /// pack root) or unsupported. Forward slashes, matching upstream's normalised paths.
    /// </summary>
    /// <param name="minecraftVersion">Used only by <see cref="AtlModType.Dependency"/>, which lands in a
    /// per-version mods subfolder.</param>
    /// <exception cref="LauncherException">
    /// The type is <see cref="AtlModType.Unknown"/>, which upstream treats as a fatal install error.
    /// </exception>
    public static string? GetDirForModType(AtlModType type, string raw, string minecraftVersion)
    {
        return type switch
        {
            // Handled at another stage (extracted, decompiled) or ignored entirely.
            AtlModType.Root
                or AtlModType.Extract
                or AtlModType.Decomp
                or AtlModType.TexturePackExtract
                or AtlModType.ResourcePackExtract
                or AtlModType.Mcpc => null,

            // Forge is detected later; if it cannot be, it installs as a jar mod — so both land here.
            AtlModType.Forge or AtlModType.Jar => "jarmods",

            AtlModType.Mods => "mods",
            AtlModType.Flan => "Flan",
            AtlModType.Dependency => "mods/" + minecraftVersion,
            AtlModType.Ic2Lib => "mods/ic2",
            AtlModType.DenLib => "mods/denlib",
            AtlModType.Coremods => "coremods",
            AtlModType.Plugins => "plugins",
            AtlModType.TexturePack => "texturepacks",
            AtlModType.ResourcePack => "resourcepacks",
            AtlModType.ShaderPack => "shaderpacks",

            // Recognised but not supported; upstream warns and skips it.
            AtlModType.Millenaire => null,

            _ => throw new LauncherException($"Unknown mod type: {raw}"),
        };
    }

    /// <summary>
    /// A Gradle-style coordinate for a pack library, so it can be matched against the metadata index.
    /// The server path is preferred (it carries the real group/artefact/version); failing that, a
    /// couple of well-known filenames are recognised; failing that, a synthetic ATLauncher coordinate
    /// keyed by the library's md5 keeps it unique.
    /// </summary>
    public static string DetectLibrary(AtlVersionLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        // A server path like ".../group/parts/artefact/version/file.jar" spells out the coordinate.
        if (!string.IsNullOrEmpty(library.Server) && library.Server.Split('/').Length >= 3)
        {
            var lastSlash = library.Server.LastIndexOf('/');
            var locationAndVersion = library.Server[..lastSlash];

            lastSlash = locationAndVersion.LastIndexOf('/');
            var location = locationAndVersion[..lastSlash];
            var version = locationAndVersion[(lastSlash + 1)..];

            lastSlash = location.LastIndexOf('/');
            var group = location[..lastSlash].Replace('/', '.');
            var artefact = location[(lastSlash + 1)..];

            return $"{group}:{artefact}:{version}";
        }

        // A "name-version.jar" filename is enough for the two libraries ATLauncher ships under names
        // the metadata index also knows.
        if (library.File.Contains('-', StringComparison.Ordinal))
        {
            var lastDash = library.File.LastIndexOf('-');
            var name = library.File[..lastDash];
            var version = library.File[(lastDash + 1)..].Replace(".jar", string.Empty, StringComparison.Ordinal);

            if (name == "guava")
            {
                return "com.google.guava:guava:" + version;
            }

            if (name == "commons-lang3")
            {
                return "org.apache.commons:commons-lang3:" + version;
            }
        }

        // Nothing recognisable: a synthetic coordinate, unique by md5, so it still installs.
        return "org.multimc.atlauncher:" + library.Md5 + ":1";
    }
}

/// <summary>
/// Stages an ATLauncher instance from a resolved pack version, ported from the tail of
/// ATLPackInstallTask's install(): the pack profile (Minecraft plus the loader, from
/// <see cref="PackComponents.FromAtl"/>, plus any jar mods) and instance.cfg, including the managed-pack
/// fields that let the instance be updated later. The library and pack VersionFile components upstream
/// also writes (createLibrariesComponent / createPackComponent) are a further wave; this is the part
/// that turns the version metadata and downloaded jar mods into an instance.
/// </summary>
public static class AtlPackBuilder
{
    private const string ManagedPackType = "atlauncher";

    public static void BuildInstance(
        InstancePaths paths,
        AtlPackVersion version,
        RuntimeContext runtimeContext,
        string packName,
        string packSafeName,
        string versionName,
        IEnumerable<string>? jarMods = null,
        string iconKey = "default",
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(version);

        var profile = new PackProfile(runtimeContext);

        foreach (var component in PackComponents.FromAtl(version))
        {
            profile.SetComponentVersion(component.Uid, component.Version, component.Important);
        }

        var jars = jarMods?.ToList() ?? [];

        if (jars.Count != 0)
        {
            JarModInstaller.Install(paths, profile, jars);
        }

        profile.Save(paths.PackProfilePath);

        var name = displayName is { Length: > 0 } ? displayName : packName;

        var settings = new IniSettingsObject(paths.ConfigPath);
        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);
        settings.Set("InstanceType", "OneSix");
        settings.Set("name", name);

        if (iconKey != "default")
        {
            settings.Set("iconKey", iconKey);
        }

        // The managed-pack fields, so the instance knows it came from ATLauncher and can be offered an
        // update — the same keys InstanceSettings.SetManagedPack writes. The id is the pack's safe name,
        // and both version fields carry the version name (ATLauncher has no separate numeric version id).
        settings.RegisterSetting("ManagedPack", false);
        settings.RegisterSetting("ManagedPackType", string.Empty);
        settings.RegisterSetting("ManagedPackID", string.Empty);
        settings.RegisterSetting("ManagedPackName", string.Empty);
        settings.RegisterSetting("ManagedPackVersionID", string.Empty);
        settings.RegisterSetting("ManagedPackVersionName", string.Empty);
        settings.Set("ManagedPack", true);
        settings.Set("ManagedPackType", ManagedPackType);
        settings.Set("ManagedPackID", packSafeName);
        settings.Set("ManagedPackName", packName);
        settings.Set("ManagedPackVersionID", versionName);
        settings.Set("ManagedPackVersionName", versionName);
    }
}

/// <summary>
/// Works out which files an ATLauncher update removes, ported from ATLPackInstallTask's
/// deleteExistingFiles. On update the pack's old mods/config/bin are cleared out — a fixed set of
/// built-in delete and keep rules, plus the version's own — before the new files go in. Given the game
/// folder and the version's keeps/deletes, this returns the files to remove; the caller deletes them.
/// </summary>
/// <remarks>
/// FILE GRANULARITY, a deliberate divergence. Upstream lists directories as well as files, and because
/// a keep-folder rule is matched with a bare "starts with", a kept file inside a directory that is
/// itself scheduled for deletion is removed anyway when the directory goes. Planning at file
/// granularity — never returning a directory, so a kept file is never collateral — closes that
/// data-loss hole. Nothing tested it, so nothing depends on the old behaviour.
/// </remarks>
public static class AtlUpdateCleaner
{
    // ATLauncher's built-in rules, applied to every update on top of the pack's own.
    private static readonly (string Base, string Target)[] BuiltinDeleteFolders =
        [("root", "mods/"), ("root", "configs/"), ("root", "bin/")];

    private static readonly (string Base, string Target)[] BuiltinKeepFiles =
    [
        ("root", "mods/PortalGunSounds.pak"),
        ("root", "config/NEI.cfg"),
        ("root", "options.txt"),
        ("root", "servers.dat"),
    ];

    private static readonly (string Base, string Target)[] BuiltinKeepFolders =
        [("root", "mods/rei_minimap/"), ("root", "mods/VoxelMods/")];

    public static List<string> PlanDeletions(string gameRoot, AtlVersionKeeps packKeeps, AtlVersionDeletes packDeletes)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(packKeeps);
        ArgumentNullException.ThrowIfNull(packDeletes);

        var root = Normalize(gameRoot);

        var keepFiles = new HashSet<string>(StringComparer.Ordinal);
        keepFiles.UnionWith(BuiltinKeepFiles.Select(k => FullPath(root, k.Base, k.Target)));
        keepFiles.UnionWith(packKeeps.Files.Select(k => FullPath(root, k.Base, k.Target)));

        // Keep-folder prefixes, each ending in "/" so a name is matched as a whole segment.
        var keepFolders = BuiltinKeepFolders.Concat(packKeeps.Folders.Select(k => (k.Base, k.Target)))
            .Select(k => EnsureTrailingSlash(FullPath(root, k.Base, k.Target)))
            .ToList();

        bool ShouldKeep(string path)
            => keepFiles.Contains(path) || keepFolders.Any(f => path.StartsWith(f, StringComparison.Ordinal));

        var toDelete = new List<string>();

        // Individual file deletes: built-ins have none, the pack may.
        foreach (var item in packDeletes.Files)
        {
            var path = FullPath(root, item.Base, item.Target);

            if (File.Exists(path) && !ShouldKeep(path))
            {
                toDelete.Add(path);
            }
        }

        // Folder deletes: every unkept file under the folder, never the folder itself.
        foreach (var (baseName, target) in BuiltinDeleteFolders.Concat(packDeletes.Folders.Select(f => (f.Base, f.Target))))
        {
            var folder = FullPath(root, baseName, target).TrimEnd('/');

            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var path = Normalize(file);

                if (!ShouldKeep(path))
                {
                    toDelete.Add(path);
                }
            }
        }

        return [.. toDelete.Distinct(StringComparer.Ordinal)];
    }

    private static string FullPath(string root, string baseName, string target)
    {
        var basePath = baseName == "config" ? root + "/config" : root;

        return Normalize($"{basePath}/{ConvertTarget(target)}");
    }

    /// <summary>ATLauncher's path separator placeholder is "%s%".</summary>
    private static string ConvertTarget(string target) => target.Replace("%s%", "/", StringComparison.Ordinal);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static string EnsureTrailingSlash(string path) => path.EndsWith('/') ? path : path + "/";
}

/// <summary>What the installer does with a downloaded ATLauncher mod file.</summary>
public enum AtlModAction
{
    /// <summary>Dropped whole into a folder (mods, jarmods, coremods, …).</summary>
    Place,

    /// <summary>Unpacked into a folder.</summary>
    Extract,

    /// <summary>Unpacked, with one named file taken out.</summary>
    Decompile,
}

/// <summary>One ATLauncher mod to fetch, and what becomes of it.</summary>
public sealed record AtlModDownload(
    string Url, string Md5, AtlModAction Action, string? TargetPath, bool IsJarMod, AtlVersionMod Mod);

/// <summary>The downloads and manual steps for an ATLauncher pack's mods.</summary>
public sealed class AtlModPlan
{
    public List<AtlModDownload> Downloads { get; } = [];

    /// <summary>Mods whose download type is "browser" — the user must fetch these by hand.</summary>
    public List<AtlVersionMod> Blocked { get; } = [];
}

/// <summary>
/// Builds the mod download plan for an ATLauncher pack version, ported from ATLPackInstallTask's
/// downloadMods. The choosing of optional mods is a UI step above this; given the selection, the plan
/// is pure: which mods are installed, where each comes from, and what is done with it.
/// </summary>
public static class AtlModPlanner
{
    private const string GameFolder = "minecraft";

    /// <summary>The optional mods a pack offers, by name, for the caller to present a chooser.</summary>
    public static List<string> OptionalMods(IEnumerable<AtlVersionMod> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        return [.. mods.Where(m => m.Optional).Select(m => m.Name)];
    }

    /// <summary>
    /// The URL a mod is fetched from, or <c>null</c> when it must be downloaded by hand (a "browser"
    /// mod). A server mod hangs off ATLauncher's CDN; a direct mod names its own URL.
    /// </summary>
    /// <exception cref="LauncherException">The download type is unknown, as upstream fails.</exception>
    public static string? DownloadUrl(AtlVersionMod mod, string serverBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(mod);

        return mod.Download switch
        {
            AtlDownloadType.Server => serverBaseUrl + mod.Url,
            AtlDownloadType.Direct => mod.Url,
            AtlDownloadType.Browser => null,
            _ => throw new LauncherException($"Unknown download type: {mod.DownloadRaw}"),
        };
    }

    /// <summary>
    /// The plan for a pack version's mods, given the optional ones the user chose (by name). Only
    /// client-side mods are considered, and an unchosen optional mod is left out entirely — unlike
    /// CurseForge, ATLauncher does not install it disabled.
    /// </summary>
    public static AtlModPlan Build(
        IEnumerable<AtlVersionMod> mods,
        IReadOnlySet<string> selectedOptional,
        string minecraftVersion,
        string? serverBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(selectedOptional);

        var server = serverBaseUrl ?? BuildConfig.Instance.AtlDownloadServerUrl;
        var plan = new AtlModPlan();

        foreach (var mod in mods)
        {
            // Server-side-only mods are not part of a client install; an unchosen optional mod is skipped.
            if (!mod.Client || (mod.Optional && !selectedOptional.Contains(mod.Name)))
            {
                continue;
            }

            if (DownloadUrl(mod, server) is not { } url)
            {
                plan.Blocked.Add(mod);
                continue;
            }

            if (mod.Type is AtlModType.Extract or AtlModType.TexturePackExtract or AtlModType.ResourcePackExtract)
            {
                plan.Downloads.Add(new AtlModDownload(url, mod.Md5, AtlModAction.Extract, TargetPath: null, IsJarMod: false, mod));
                continue;
            }

            if (mod.Type == AtlModType.Decomp)
            {
                plan.Downloads.Add(new AtlModDownload(url, mod.Md5, AtlModAction.Decompile, TargetPath: null, IsJarMod: false, mod));
                continue;
            }

            // A plain mod goes into a folder decided by its type; a type with no folder is skipped.
            if (AtlInstall.GetDirForModType(mod.Type, mod.TypeRaw, minecraftVersion) is not { } relativeFolder)
            {
                continue;
            }

            var target = $"{GameFolder}/{relativeFolder}/{mod.File}";
            var isJarMod = mod.Type is AtlModType.Forge or AtlModType.Jar;

            plan.Downloads.Add(new AtlModDownload(url, mod.Md5, AtlModAction.Place, target, isJarMod, mod));
        }

        return plan;
    }
}

/// <summary>
/// Unpacks the ATLauncher mod types that are not simply dropped in — extract and decompile — ported
/// from ATLPackInstallTask's extractMods. An "extract" mod is an archive whose contents (or one folder
/// of them) are unpacked into a target folder; a "decomp" mod is an archive from which one named file
/// is taken. Both target folders come from the mod's own extractTo/decompType, mapped through
/// <see cref="AtlInstall.GetDirForModType"/>.
/// </summary>
public static class AtlModExtractor
{
    /// <summary>The game-relative folder an extract mod's contents land in.</summary>
    public static string ExtractTargetFolder(AtlVersionMod mod, string minecraftVersion)
    {
        ArgumentNullException.ThrowIfNull(mod);

        return mod.Type switch
        {
            AtlModType.TexturePackExtract => "texturepacks/extracted",
            AtlModType.ResourcePackExtract => "resourcepacks/extracted",

            // A plain "extract" names where it goes; a type with no folder unpacks at the game root.
            _ => AtlInstall.GetDirForModType(mod.ExtractTo, mod.ExtractToRaw, minecraftVersion) ?? string.Empty,
        };
    }

    /// <summary>Unpacks an extract mod: the whole archive, or the one folder of it the mod names.</summary>
    public static void Extract(InstancePaths paths, AtlVersionMod mod, string archivePath, string minecraftVersion)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mod);

        var folder = ExtractTargetFolder(mod, minecraftVersion);
        var target = folder.Length == 0 ? paths.GameRoot : FileSystem.PathCombine(paths.GameRoot, folder);

        // Only a plain "extract" restricts itself to a sub-folder of the archive; the leading slash the
        // manifest sometimes writes is dropped, matching upstream.
        var subFolder = mod.Type == AtlModType.Extract ? mod.ExtractFolder.TrimStart('/') : string.Empty;

        if (MMCZip.ExtractDir(archivePath, subFolder, target) is null)
        {
            throw new LauncherException($"Failed to extract mod archive {archivePath}.");
        }
    }

    /// <summary>Takes the one named file out of a decomp mod's archive.</summary>
    public static void Decompile(InstancePaths paths, AtlVersionMod mod, string archivePath, string minecraftVersion)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mod);

        var folder = AtlInstall.GetDirForModType(mod.DecompType, mod.DecompTypeRaw, minecraftVersion) ?? string.Empty;

        var target = folder.Length == 0
            ? FileSystem.PathCombine(paths.GameRoot, mod.DecompFile)
            : FileSystem.PathCombine(paths.GameRoot, folder, mod.DecompFile);

        FileSystem.EnsureFilePathExists(target);

        if (!MMCZip.ExtractFile(archivePath, mod.DecompFile, target))
        {
            throw new LauncherException($"Failed to take {mod.DecompFile} out of {archivePath}.");
        }
    }
}

/// <summary>The ATLauncher CDN URLs for one version of one pack. Ported from ATLPackInstallTask.</summary>
public static class AtlUrls
{
    /// <summary>The version manifest (Configs.json) — the loader, mods and file rules for a version.</summary>
    public static string VersionManifest(string serverBaseUrl, string packSafeName, string versionName)
        => $"{serverBaseUrl}packs/{packSafeName}/versions/{versionName}/Configs.json";

    /// <summary>The config archive (Configs.zip) — the loose files laid over the instance.</summary>
    public static string ConfigArchive(string serverBaseUrl, string packSafeName, string versionName)
        => $"{serverBaseUrl}packs/{packSafeName}/versions/{versionName}/Configs.zip";
}

/// <summary>
/// Downloads and unpacks a pack version's config archive, ported from ATLPackInstallTask's
/// installConfigs / extractConfigs. The archive's contents are the loose files (config, scripts,
/// resources) that go straight into the game folder, checked against the version's sha1 when it gives
/// one.
/// </summary>
public static class AtlConfigInstaller
{
    public static async Task InstallAsync(
        HttpClient client,
        string url,
        string gameRoot,
        string sha1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var temp = FileSystem.PathCombine(Path.GetTempPath(), $"el-atl-configs-{Guid.NewGuid():N}.zip");

        try
        {
            using (var response = await client
                       .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using var output = File.Create(temp);
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (sha1 is { Length: > 0 })
            {
                string actual;

                await using (var input = File.OpenRead(temp))
                {
                    actual = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                }

                if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new LauncherException(
                        $"Config archive checksum mismatch: expected {sha1}, got {actual.ToLowerInvariant()}.");
                }
            }

            FileSystem.EnsureFolderPathExists(gameRoot);

            if (MMCZip.ExtractDir(temp, gameRoot) is null)
            {
                throw new LauncherException("Failed to extract the pack config archive.");
            }
        }
        finally
        {
            FileSystem.DeletePath(temp);
        }
    }
}
