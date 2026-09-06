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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from launcher/minecraft/update/{Folders,Libraries,AssetUpdate,FMLLibraries}Task.{h,cpp}.
 *
 * Everything the game needs on disk before it can start. A resolved LaunchProfile says WHICH jars and
 * assets are required; these fetch them. Together they are the difference between "the launcher knows
 * what 1.20.1 is" and "the launcher can run 1.20.1".
 *
 * They take an InstancePaths and a LaunchProfile rather than a MinecraftInstance, for the same reason
 * LaunchCommandBuilder does: everything the work needs, and nothing else.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>Creates the game folder before anything tries to write into it.</summary>
public sealed class FoldersTask : LauncherTask
{
    private readonly InstancePaths _paths;

    public FoldersTask(InstancePaths paths) : base("Create folders") => _paths = paths;

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!FileSystem.EnsureFolderPathExists(_paths.GameRoot))
        {
            throw new TaskFailedException("Failed to create folder for Minecraft binaries.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>Downloads every jar the profile's classpath names.</summary>
public sealed class LibrariesTask : LauncherTask
{
    private readonly InstancePaths _paths;
    private readonly LaunchProfile _profile;
    private readonly RuntimeContext _runtimeContext;
    private readonly HttpClient _client;
    private readonly HttpMetaCache _cache;
    private readonly string _instanceName;

    public LibrariesTask(
        InstancePaths paths,
        LaunchProfile profile,
        RuntimeContext runtimeContext,
        HttpClient client,
        HttpMetaCache cache,
        string instanceName = "")
        : base($"Libraries for instance {instanceName}")
    {
        _paths = paths;
        _profile = profile;
        _runtimeContext = runtimeContext;
        _client = client;
        _cache = cache;
        _instanceName = instanceName;
    }

    public override bool CanAbort => true;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var job = new NetJob($"Libraries for instance {_instanceName}", _client);

        var failedLocalLibraries = new List<string>();
        var failedLocalJarMods = new List<string>();

        /*
         * EVERY ARTIFACT POOL, and the composition matters: classpath libraries, natives, maven files
         * that are downloaded but never put on the classpath, each agent's own jar, and the main jar
         * itself. Miss one and the game starts with a NoClassDefFoundError rather than a launcher
         * error, which is far harder for a user to report usefully.
         */
        var pool = new List<Library>();

        pool.AddRange(_profile.Libraries);
        pool.AddRange(_profile.NativeLibraries);
        pool.AddRange(_profile.MavenFiles);
        pool.AddRange(_profile.Agents.Select(agent => agent.Library));

        if (_profile.MainJar is { } mainJar)
        {
            pool.Add(mainJar);
        }

        AddPool(job, pool, failedLocalLibraries, _paths.LocalLibraryPath);
        AddPool(job, _profile.JarMods, failedLocalJarMods, _paths.JarModsDir);

        /*
         * A "local" artifact is one the metadata says the user must supply — a proprietary jar the
         * launcher is not allowed to distribute. There is nothing to download, so a missing one is
         * reported BEFORE any request goes out rather than as a failed download, and the message says
         * what to do about it.
         */
        if (failedLocalLibraries.Count != 0 || failedLocalJarMods.Count != 0)
        {
            var missing = string.Join('\n', failedLocalLibraries.Concat(failedLocalJarMods));

            throw new TaskFailedException(
                $"Some artifacts marked as 'local' are missing their files:\n{missing}\n\n"
                + "You need to either add the files, or removed the packages that require them.\n"
                + "You'll have to correct this problem manually.");
        }

        job.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);
        job.StatusChanged += (_, status) => SetStatus(status);

        if (!await job.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            if (job.State == TaskState.AbortedByUser)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TaskFailedException(
                $"Game update failed: it was impossible to fetch the required libraries.\nReason:\n{job.FailReason}");
        }
    }

    private void AddPool(NetJob job, IEnumerable<Library> pool, List<string> errors, string localPath)
    {
        foreach (var library in pool)
        {
            if (library is null)
            {
                throw new TaskFailedException("Null jar is specified in the metadata, aborting.");
            }

            foreach (var download in library.GetDownloads(_runtimeContext, _client, _cache, errors, localPath))
            {
                job.AddTask(download);
            }
        }
    }
}

/// <summary>
/// Downloads the asset index, then every asset it names.
/// </summary>
/// <remarks>
/// TWO ROUNDS, and they cannot be merged: the index is a document listing thousands of hashed objects,
/// so what to fetch in the second round is not known until the first has finished and parsed.
/// </remarks>
public sealed class AssetUpdateTask : LauncherTask
{
    private readonly LaunchProfile _profile;
    private readonly HttpClient _client;
    private readonly HttpMetaCache _cache;
    private readonly string _assetsDirectory;
    private readonly string _resourceBase;
    private readonly string _instanceName;

    public AssetUpdateTask(
        LaunchProfile profile,
        HttpClient client,
        HttpMetaCache cache,
        string assetsDirectory,
        string resourceBase = "https://resources.download.minecraft.net/",
        string instanceName = "")
        : base($"Assets for instance {instanceName}")
    {
        _profile = profile;
        _client = client;
        _cache = cache;
        _assetsDirectory = assetsDirectory;
        _resourceBase = resourceBase;
        _instanceName = instanceName;
    }

    public override bool CanAbort => true;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus("Updating assets index...");

        if (_profile.MinecraftAssets is not { Id.Length: > 0 } assets)
        {
            // A profile with no asset index is a valid state — some patches carry no assets at all.
            return;
        }

        var indexPath = FileSystem.PathCombine(_assetsDirectory, "indexes", $"{assets.Id}.json");
        FileSystem.EnsureFilePathExists(indexPath);

        var indexJob = new NetJob($"Asset index for {_instanceName}", _client);
        var download = Download.MakeFile(_client, new Uri(assets.Url), indexPath, $"{assets.Id}.json");

        if (assets.Sha1.Length != 0)
        {
            download.AddValidator(new ChecksumValidator(System.Security.Cryptography.HashAlgorithmName.SHA1, assets.Sha1));
        }

        indexJob.AddTask(download);
        indexJob.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);

        if (!await indexJob.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TaskFailedException($"Failed to download the assets index:\n{indexJob.FailReason}");
        }

        var index = AssetsIndex.Load(_assetsDirectory, assets.Id);

        if (index is null)
        {
            // EVICTED on a parse failure, inherited: a corrupt index that stays cached would be
            // re-read and re-rejected on every launch, with no way for the user to break the loop.
            FileSystem.DeletePath(indexPath);
            throw new TaskFailedException("Failed to read the assets index!");
        }

        var assetsJob = index.CreateDownloadJob(_client, _assetsDirectory, _resourceBase);

        if (assetsJob is null)
        {
            // Everything is already on disk.
            return;
        }

        SetStatus("Getting the assets files from Mojang...");

        assetsJob.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);

        if (!await assetsJob.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            if (assetsJob.State == TaskState.AbortedByUser)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TaskFailedException($"Failed to download assets:\n{assetsJob.FailReason}");
        }
    }
}

/// <summary>
/// Fetches the loose jars old Forge versions expect to find in the instance's lib folder.
/// </summary>
/// <remarks>
/// A 1.3–1.5 era arrangement: FML looked for these by filename in <c>&lt;instance&gt;/lib</c> rather
/// than taking them from the classpath. They are downloaded to the shared cache and then COPIED in,
/// because each instance needs its own physical copy at that path.
///
/// The table below is data, not logic — filenames and SHA-1s frozen when those versions shipped.
/// </remarks>
public sealed class FMLLibrariesTask : LauncherTask
{
    /// <summary>One loose FML library: the filename Forge looks for, and its hash.</summary>
    public readonly record struct FMLLib(string Filename, string Sha1);

    private static readonly FMLLib[] Libs13 =
    [
        new("argo-2.25.jar", "bb672829fde76cb163004752b86b0484bd0a7f4b"),
        new("guava-12.0.1.jar", "b8e78b9af7bf45900e14c6f958486b6ca682195f"),
        new("asm-all-4.0.jar", "98308890597acb64047f7e896638e0d98753ae82"),
    ];

    private static readonly FMLLib[] Libs14 =
    [
        new("argo-2.25.jar", "bb672829fde76cb163004752b86b0484bd0a7f4b"),
        new("guava-12.0.1.jar", "b8e78b9af7bf45900e14c6f958486b6ca682195f"),
        new("asm-all-4.0.jar", "98308890597acb64047f7e896638e0d98753ae82"),
        new("bcprov-jdk15on-147.jar", "b6f5d9926b0afbde9f4dbe3db88c5247be7794bb"),
    ];

    private static FMLLib[] Libs15(string deobfuscationData, string deobfuscationSha1) =>
    [
        new("argo-small-3.2.jar", "58912ea2858d168c50781f956fa5b59f0f7c6b51"),
        new("guava-14.0-rc3.jar", "931ae21fa8014c3ce686aaa621eae565fefb1a6a"),
        new("asm-all-4.1.jar", "054986e962b88d8660ae4566475658469595ef58"),
        new("bcprov-jdk15on-148.jar", "960dea7c9181ba0b17e8bab0c06a43f0a5f04e65"),
        new(deobfuscationData, deobfuscationSha1),
        new("scala-library.jar", "458d046151ad179c85429ed7420ffb1eaf6ddf85"),
    ];

    /// <summary>Which loose libraries each Minecraft version needs. Absent means none.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<FMLLib>> Mapping =
        new Dictionary<string, IReadOnlyList<FMLLib>>(StringComparer.Ordinal)
        {
            ["1.3.2"] = Libs13,

            ["1.4"] = Libs14,
            ["1.4.1"] = Libs14,
            ["1.4.2"] = Libs14,
            ["1.4.3"] = Libs14,
            ["1.4.4"] = Libs14,
            ["1.4.5"] = Libs14,
            ["1.4.6"] = Libs14,
            ["1.4.7"] = Libs14,

            // The deobfuscation data is version-specific; everything else in the 1.5 set is shared.
            ["1.5"] = Libs15("deobfuscation_data_1.5.zip", "5f7c142d53776f16304c0bbe10542014abad6af8"),
            ["1.5.1"] = Libs15("deobfuscation_data_1.5.1.zip", "22e221a0d89516c1f721d6cab056a7e37471d0a6"),
            ["1.5.2"] = Libs15("deobfuscation_data_1.5.2.zip", "446e55cd986582c70fcf12cb27bc00114c5adfd9"),
        };

    private readonly InstancePaths _paths;
    private readonly string _minecraftVersion;
    private readonly bool _hasForge;
    private readonly HttpClient _client;
    private readonly HttpMetaCache _cache;
    private readonly string _baseUrl;

    public FMLLibrariesTask(
        InstancePaths paths,
        string minecraftVersion,
        bool hasForge,
        HttpClient client,
        HttpMetaCache cache,
        string baseUrl = "https://extremelauncher.net/_api/fmllibs/")
        : base("FML libraries")
    {
        _paths = paths;
        _minecraftVersion = minecraftVersion;
        _hasForge = hasForge;
        _client = client;
        _cache = cache;
        _baseUrl = baseUrl;
    }

    public override bool CanAbort => true;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus("Checking for FML libraries...");

        // Nothing to do for any modern version, or for a version with no Forge on it.
        if (!Mapping.TryGetValue(_minecraftVersion, out var required) || !_hasForge)
        {
            return;
        }

        // Only what is not already in place: these never change, so a present file is a correct one.
        var missing = required
            .Where(lib => !File.Exists(FileSystem.PathCombine(_paths.LibDir, lib.Filename)))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        SetStatus("Downloading FML libraries...");

        var job = new NetJob("FML libraries", _client);
        var cachePaths = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var lib in missing)
        {
            var entry = _cache.ResolveEntry("fmllibs", lib.Filename);
            cachePaths[lib.Filename] = entry.GetFullPath();

            // Eternal: these files are frozen, so re-validating them against the server on every
            // launch would be pure latency. Upstream passes Net::Download::Option::MakeEternal.
            job.AddTask(Download.Make(
                _client,
                new Uri(_baseUrl + lib.Filename),
                new MetaCacheSink(entry, _cache, isEternal: true),
                lib.Filename));
        }

        job.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);

        if (!await job.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            if (job.State == TaskState.AbortedByUser)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TaskFailedException(
                $"Failed to download the following files:\n{string.Join('\n', missing.Select(l => l.Filename))}\n\n"
                + $"Reason:{job.FailReason}\nPlease try again.");
        }

        SetStatus("Copying FML libraries into the instance...");

        for (var i = 0; i < missing.Count; i++)
        {
            SetProgress(i, missing.Count);

            var path = FileSystem.PathCombine(_paths.LibDir, missing[i].Filename);

            if (!FileSystem.EnsureFilePathExists(path))
            {
                throw new TaskFailedException("Failed creating FML library folder inside the instance.");
            }

            try
            {
                // COPIED rather than linked: FML resolves these by path inside the instance, and a
                // hard link into the shared cache would let one instance's changes reach every other.
                File.Copy(cachePaths[missing[i].Filename], path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new TaskFailedException($"Failed copying Forge/FML library: {missing[i].Filename}.", e);
            }
        }

        SetProgress(missing.Count, missing.Count);
    }
}
