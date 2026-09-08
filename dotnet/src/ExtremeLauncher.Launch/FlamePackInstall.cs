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
 * The core of flame/FlameInstanceCreationTask.cpp's createInstance: turning an already-extracted
 * CurseForge pack into a staged instance. A CurseForge pack zip holds a manifest.json (the loader, the
 * Minecraft version, and a list of file ids to fetch) and an "overrides" folder of loose files that go
 * straight into the game directory. This builds the instance from those two things -- the pack profile
 * and instance.cfg, with the overrides moved into place -- which is everything the import does that
 * does NOT need the network. Resolving the file ids to download URLs (FlameFileResolver) and fetching
 * them, with the blocked-mod handling that entails, is the other half and a later wave.
 *
 * The components come from the already-ported PackComponents.FromFlame (Minecraft version normalised,
 * loader mapped, the NeoForge 1.20.1 quirk handled); this adds the instance I/O around it.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>Builds an instance from an extracted CurseForge pack.</summary>
public static class FlamePackBuilder
{
    /// <summary>
    /// Turns an extracted CurseForge pack — its manifest already parsed, its <c>overrides</c> folder
    /// sitting under the instance root — into a staged instance: the pack profile (Minecraft plus the
    /// loader the manifest names) and instance.cfg, with the overrides moved in to become the game
    /// folder. The manifest.json, when present on disk, is filed under <c>flame/</c> for later update
    /// checks, as upstream does.
    /// </summary>
    public static void BuildFromExtracted(
        InstancePaths paths,
        FlamePackManifest manifest,
        RuntimeContext runtimeContext,
        string iconKey = "default",
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);

        // The overrides folder becomes the game directory. Missing is a warning upstream, not an error:
        // a pack that was already imported once has had its overrides moved away.
        var overrides = FileSystem.PathCombine(paths.InstanceRoot, manifest.Overrides);

        if (Directory.Exists(overrides) && !FileSystem.Move(overrides, paths.GameRoot))
        {
            throw new LauncherException($"Could not rename the overrides folder: {manifest.Overrides}");
        }

        // The manifest is kept under flame/ so an update can tell what the pack shipped.
        var manifestPath = FileSystem.PathCombine(paths.InstanceRoot, "manifest.json");

        if (File.Exists(manifestPath))
        {
            var filed = FileSystem.PathCombine(paths.InstanceRoot, "flame", "manifest.json");
            FileSystem.EnsureFilePathExists(filed);
            FileSystem.Move(manifestPath, filed);
        }

        var profile = new PackProfile(runtimeContext);

        foreach (var component in PackComponents.FromFlame(manifest))
        {
            profile.SetComponentVersion(component.Uid, component.Version, component.Important);
        }

        profile.Save(paths.PackProfilePath);

        var name = displayName is { Length: > 0 } ? displayName : manifest.Name;

        var settings = new IniSettingsObject(paths.ConfigPath);
        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);
        settings.Set("InstanceType", "OneSix");
        settings.Set("name", name);
        settings.Set("iconKey", ResolveIconKey(iconKey, manifest.Name));
    }

    /// <summary>
    /// The instance icon: a caller-chosen one wins; otherwise a couple of well-known pack names get a
    /// fitting default (Direwolf20 packs an old Steve icon, FTB packs the FTB logo), matching upstream.
    /// A pack matching neither keeps the plain default.
    /// </summary>
    public static string ResolveIconKey(string iconKey, string packName)
    {
        if (iconKey != "default")
        {
            return iconKey;
        }

        if (packName.Contains("Direwolf20", StringComparison.Ordinal))
        {
            return "steve";
        }

        if (packName.Contains("FTB", StringComparison.Ordinal)
            || packName.Contains("Feed The Beast", StringComparison.Ordinal))
        {
            return "ftb_logo";
        }

        return "default";
    }
}

/// <summary>One file to fetch: where from, and where it lands under the instance.</summary>
public sealed record FlameDownload(string Url, string RelativePath);

/// <summary>The plan for turning resolved pack files into downloads and manual steps.</summary>
public sealed class FlameDownloadPlan
{
    /// <summary>Files with a usable URL, each with its target path relative to the instance root.</summary>
    public List<FlameDownload> Downloads { get; } = [];

    /// <summary>Files with no URL — the user must fetch these by hand.</summary>
    public List<FlameResolvedFile> Blocked { get; } = [];

    /// <summary>The <c>.zip</c> files, which are extracted into place after download rather than dropped in.</summary>
    public List<(string FileName, string TargetFolder)> ZipResources { get; } = [];
}

/// <summary>
/// Builds the download plan from a pack's resolved files, ported from FlameInstanceCreationTask's
/// idResolverSucceeded and setupDownloadJob. The resolution itself (FlameFileResolver) and the actual
/// fetching are elsewhere; this is the pure step in between — deciding, for each resolved file, whether
/// it is downloaded and to what path, which are the manual downloads, and which are zip resources.
/// </summary>
public static class FlameDownloadPlanner
{
    private const string GameFolder = "minecraft";

    /// <summary>
    /// The optional files a pack offers, as instance-relative paths (under the game folder), for the
    /// caller to present a chooser. Required files are never listed — they are always installed.
    /// </summary>
    /// <remarks>
    /// The path is built from the SANITISED file name, the same form <see cref="Build"/> compares the
    /// selection against. Upstream builds this list from the raw name but checks the sanitised one, so a
    /// file with an invalid character in its name never matches its own selection and is always
    /// disabled; using the sanitised name on both sides fixes that quietly.
    /// </remarks>
    public static List<string> OptionalFiles(IEnumerable<FlameResolvedFile> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return [.. resolved.Where(f => !f.Entry.Required).Select(RelativePath)];
    }

    /// <summary>
    /// The download plan. Every resolved file with a URL becomes a download; a file whose URL is
    /// missing is blocked (a manual step). An optional file the caller did not select is still
    /// downloaded but lands disabled (a <c>.disabled</c> suffix), matching upstream. Every <c>.zip</c>
    /// file is also recorded as a zip resource to be extracted after download.
    /// </summary>
    /// <param name="selectedOptional">
    /// The optional files (by the paths <see cref="OptionalFiles"/> returned) the user chose to enable.
    /// </param>
    public static FlameDownloadPlan Build(
        IEnumerable<FlameResolvedFile> resolved, IReadOnlySet<string> selectedOptional)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(selectedOptional);

        var plan = new FlameDownloadPlan();

        foreach (var file in resolved)
        {
            if (file.Version.FileName.EndsWith(".zip", StringComparison.Ordinal))
            {
                plan.ZipResources.Add((file.Version.FileName, file.Entry.TargetFolder));
            }

            if (file.IsBlocked)
            {
                plan.Blocked.Add(file);
                continue;
            }

            var relativeToGame = RelativePath(file);

            // An unselected optional file is installed disabled rather than left out, so the user can
            // turn it on later without re-downloading.
            if (!file.Entry.Required && !selectedOptional.Contains(relativeToGame))
            {
                relativeToGame += ".disabled";
            }

            plan.Downloads.Add(new FlameDownload(file.Version.DownloadUrl, $"{GameFolder}/{relativeToGame}"));
        }

        return plan;
    }

    /// <summary>A file's path under the game folder: its target folder plus its sanitised name.</summary>
    private static string RelativePath(FlameResolvedFile file)
        => $"{file.Entry.TargetFolder}/{FileSystem.RemoveInvalidPathChars(file.Version.FileName)}";
}

/// <summary>
/// Installs a CurseForge pack from a downloaded archive: extract, resolve, stage and download. Ported
/// from flame/FlameInstanceCreationTask, tying together the pieces the earlier waves ported —
/// FlamePackManifest (parse), FlameFileResolver (resolve), FlamePackBuilder (stage) and
/// FlameDownloadPlanner (plan). The resolver and HTTP client are injected so the whole task runs
/// without hidden globals, and can be driven in a test with a stub resolver and local files.
/// </summary>
/// <remarks>
/// TWO OF UPSTREAM'S INTERACTIONS ARE NOT HERE, on purpose. Blocked mods — files CurseForge will not
/// serve to third parties — make the install fail with a message naming them and where to get them,
/// rather than opening the "add these by hand" dialog; and optional mods are installed disabled rather
/// than through a chooser. Both are UI flows that belong above this task; the download plan already
/// supports a selection, so a chooser can be added without changing the task.
/// </remarks>
public sealed class FlameImportTask : LauncherTask, IInstanceTask
{
    private readonly string _archivePath;

    private readonly IFlameResolverApi _resolver;

    private readonly HttpClient _client;

    private readonly RuntimeContext _runtimeContext;

    private readonly string _iconKey;

    private string _instanceName;

    public FlameImportTask(
        string archivePath,
        IFlameResolverApi resolver,
        HttpClient client,
        RuntimeContext runtimeContext,
        string instanceName = "",
        string iconKey = "default",
        string group = "")
        : base("Installing modpack")
    {
        _archivePath = archivePath ?? throw new ArgumentNullException(nameof(archivePath));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _runtimeContext = runtimeContext;
        _instanceName = instanceName;
        _iconKey = iconKey;
        Group = group;
    }

    public string StagingPath { get; set; } = string.Empty;

    string IInstanceTask.Name => _instanceName.Length != 0 ? _instanceName : "Modpack";

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

        SetStatus("Extracting modpack");

        if (MMCZip.ExtractDir(_archivePath, StagingPath) is null)
        {
            throw new LauncherException($"Failed to extract the modpack archive {_archivePath}.");
        }

        var manifestPath = FileSystem.PathCombine(StagingPath, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            throw new LauncherException("The pack has no manifest.json.");
        }

        var manifest = FlamePack.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));

        if (_instanceName.Length == 0)
        {
            _instanceName = manifest.Name;
        }

        SetStatus("Resolving modpack files");

        var resolved = await FlameFileResolver.ResolveAsync(manifest, _resolver, cancellationToken).ConfigureAwait(false);

        // No optional chooser here: an optional file is installed disabled. See the class remarks.
        var plan = FlameDownloadPlanner.Build(resolved, new HashSet<string>());

        if (plan.Blocked.Count != 0)
        {
            throw new LauncherException(BlockedMessage(plan.Blocked));
        }

        var paths = new InstancePaths(StagingPath);

        // Stage first (this moves the overrides folder in to become the game directory), then download
        // the resolved files into it — the order upstream uses.
        FlamePackBuilder.BuildFromExtracted(paths, manifest, _runtimeContext, _iconKey, _instanceName);

        SetStatus($"Downloading {plan.Downloads.Count} files");

        foreach (var download in plan.Downloads)
        {
            var target = FileSystem.PathCombine(StagingPath, download.RelativePath);
            FileSystem.EnsureFilePathExists(target);

            await DownloadAsync(download.Url, target, cancellationToken).ConfigureAwait(false);
            DownloadedCount++;
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var output = File.Create(destination);
        await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static string BlockedMessage(IReadOnlyList<FlameResolvedFile> blocked)
    {
        var names = blocked.Select(f =>
        {
            var where = f.ManualDownloadUrl.Length != 0 ? $" ({f.ManualDownloadUrl})" : string.Empty;
            return $"  {f.Version.FileName}{where}";
        });

        return "These files are not available for download in third-party launchers and must be added by "
               + $"hand:\n{string.Join('\n', names)}";
    }
}
