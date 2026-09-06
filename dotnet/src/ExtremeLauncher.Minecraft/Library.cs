// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * Ported from launcher/minecraft/Library.{h,cpp}.
 *
 * A single entry from a version JSON's "libraries" array. Deciding whether it applies, where its jar
 * lives, and which native to extract is the most correctness-critical logic in the launcher: get it
 * wrong and a modded instance fails to start with no useful error.
 *
 * THE "${arch}" HACK is upstream's, and preserved. Old Mojang natives shipped separate 32- and 64-bit
 * jars distinguished by a literal "${arch}" in the classifier. When the storage path still contains
 * that token, the library expands into TWO files -- one per bitness -- which is why
 * GetApplicableFiles has separate native32/native64 outputs rather than one native list.
 *
 * Libraries reach the disk one of two ways. Modern version JSONs carry a "downloads" block with an
 * explicit URL and sha1 per artifact; older ones carry only a Maven coordinate, and the URL is
 * derived from a repository base. GetDownloads handles both, and routes everything through the HTTP
 * cache so a second launch re-validates rather than re-downloads.
 */

using System.Security.Cryptography;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;

namespace ExtremeLauncher.Minecraft;

public sealed class Library
{
    private const string ArchToken = "${arch}";

    private readonly List<Rule> _rules = [];

    public Library()
    {
    }

    public Library(string name) => Name = new GradleSpecifier(name);

    /// <summary>The Maven coordinate this library is published under.</summary>
    public GradleSpecifier Name { get; set; } = new(string.Empty);

    /// <summary>Maps a platform classifier ("linux", "windows-x86_64") to a native jar classifier.</summary>
    public Dictionary<string, string> NativeClassifiers { get; } = new(StringComparer.Ordinal);

    /// <summary>Base URL for the Maven repository this library comes from.</summary>
    public string RepositoryUrl { get; set; } = string.Empty;

    /// <summary>A direct URL, bypassing the Maven layout entirely.</summary>
    public string AbsoluteUrl { get; set; } = string.Empty;

    /// <summary>"local" or "always-stale"; drives caching and download behaviour.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>Overrides the filename derived from the coordinate.</summary>
    public string Filename { get; set; } = string.Empty;

    public string DisplayNameOverride { get; set; } = string.Empty;

    /// <summary>Paths not to extract from a native jar, e.g. "META-INF/".</summary>
    public List<string> ExtractExcludes { get; } = [];

    /// <summary>
    /// Whether the source JSON carried an "extract" block at all, as distinct from carrying an empty
    /// one. Kept so the field round-trips rather than silently disappearing.
    /// </summary>
    public bool HasExcludes { get; set; }

    /// <summary>Whether the source JSON carried a "rules" block.</summary>
    public bool ApplyRules { get; set; }

    public IReadOnlyList<Rule> Rules => _rules;

    private string _storagePrefix = string.Empty;

    public string ArtifactPrefix => Name.ArtifactPrefix;

    public string ArtifactId => Name.ArtifactId;

    public string Version => Name.Version;

    /// <summary>A library is native when it declares any native classifiers.</summary>
    public bool IsNative => NativeClassifiers.Count != 0;

    /// <summary>Local libraries live inside the instance rather than the shared cache.</summary>
    public bool IsLocal => string.Equals(Hint, "local", StringComparison.Ordinal);

    public bool IsAlwaysStale => string.Equals(Hint, "always-stale", StringComparison.Ordinal);

    public void SetRules(IEnumerable<Rule> rules)
    {
        _rules.Clear();
        _rules.AddRange(rules);
    }

    public void SetStoragePrefix(string prefix = "") => _storagePrefix = prefix;

    public void SetClassifier(string classifier) => Name.Classifier = classifier;

    /// <summary>A shallow copy carrying only the fields upstream's <c>limitedCopy</c> keeps.</summary>
    public static Library LimitedCopy(Library other)
    {
        var copy = new Library
        {
            Name = new GradleSpecifier(other.Name.Serialize()),
            RepositoryUrl = other.RepositoryUrl,
            Hint = other.Hint,
            AbsoluteUrl = other.AbsoluteUrl,
            Filename = other.Filename,
            _storagePrefix = other._storagePrefix,

            /*
             * SHARED, not cloned, exactly as upstream shares the shared_ptr. Nothing mutates a
             * downloads block after parsing, and a launch profile holding the same one as its patch
             * is how upstream behaves.
             *
             * Omitting this is what a first pass gets wrong -- and it hides, because a library with no
             * downloads block silently falls back to deriving a URL from its Maven coordinate, which
             * is right for almost every library in existence. It is only wrong for the repackaged
             * LWJGL natives, whose artifact id and actual path disagree. See the getDownloads tests.
             */
            MojangDownloads = other.MojangDownloads,
        };

        copy.ExtractExcludes.AddRange(other.ExtractExcludes);

        foreach (var (key, value) in other.NativeClassifiers)
        {
            copy.NativeClassifiers[key] = value;
        }

        copy.SetRules(other._rules);

        return copy;
    }

    /// <summary>
    /// Whether this library should be loaded (or, for natives, extracted) in the given context.
    /// </summary>
    /// <remarks>
    /// With no rules the answer is yes. With rules, the verdict starts at Disallow and each rule that
    /// has an opinion overwrites it, so the LAST applicable rule wins. A native additionally needs a
    /// classifier matching the current platform.
    /// </remarks>
    public bool IsActive(RuntimeContext runtimeContext)
    {
        var result = true;

        if (_rules.Count != 0)
        {
            var verdict = RuleAction.Disallow;

            foreach (var rule in _rules)
            {
                var applied = rule.Apply(this, runtimeContext);

                if (applied != RuleAction.Defer)
                {
                    verdict = applied;
                }
            }

            result = verdict == RuleAction.Allow;
        }

        if (IsNative)
        {
            result = result && GetCompatibleNative(runtimeContext) is not null;
        }

        return result;
    }

    /// <summary>The native classifier for this platform, or null if none matches.</summary>
    public string? GetCompatibleNative(RuntimeContext runtimeContext)
    {
        if (NativeClassifiers.TryGetValue(runtimeContext.GetClassifier(), out var precise))
        {
            return precise;
        }

        // Old version JSONs say "linux" rather than "linux-x86_64".
        if (runtimeContext.IsLegacyArch() && NativeClassifiers.TryGetValue(runtimeContext.System, out var legacy))
        {
            return legacy;
        }

        return null;
    }

    public string DefaultStoragePrefix => "libraries/";

    public string StoragePrefix => _storagePrefix.Length == 0 ? DefaultStoragePrefix : _storagePrefix;

    /// <summary>The on-disk filename for this library in the given context.</summary>
    public string GetFilename(RuntimeContext runtimeContext)
    {
        if (Filename.Length != 0)
        {
            return Filename;
        }

        if (!IsNative)
        {
            return Name.GetFileName();
        }

        return NativeSpecifier(runtimeContext).GetFileName();
    }

    public string GetDisplayName(RuntimeContext runtimeContext)
        => DisplayNameOverride.Length != 0 ? DisplayNameOverride : GetFilename(runtimeContext);

    /// <summary>The path relative to the storage prefix where this library is kept.</summary>
    public string StorageSuffix(RuntimeContext runtimeContext)
        => IsNative
            ? NativeSpecifier(runtimeContext).ToPath(Filename)
            : Name.ToPath(Filename);

    /// <summary>
    /// Resolves this library to the absolute paths it contributes, split by role.
    /// </summary>
    /// <param name="overridePath">
    /// When set and the library is local, files are taken from here by filename rather than from the
    /// Maven layout.
    /// </param>
    public void GetApplicableFiles(
        RuntimeContext runtimeContext,
        List<string> jar,
        List<string> native,
        List<string> native32,
        List<string> native64,
        string overridePath = "")
    {
        var local = IsLocal;

        string ActualPath(string relativePath)
        {
            relativePath = FileSystem.RemoveInvalidPathChars(relativePath);
            var combined = FileSystem.PathCombine(StoragePrefix, relativePath);

            if (local && !string.IsNullOrEmpty(overridePath))
            {
                return FileSystem.CleanPath(
                    Path.GetFullPath(FileSystem.PathCombine(overridePath, Path.GetFileName(combined))));
            }

            return FileSystem.CleanPath(Path.GetFullPath(combined));
        }

        var rawStorage = StorageSuffix(runtimeContext);

        if (!IsNative)
        {
            jar.Add(ActualPath(rawStorage));
            return;
        }

        // The "${arch}" hack: one library entry, two concrete native jars.
        if (rawStorage.Contains(ArchToken, StringComparison.Ordinal))
        {
            native32.Add(ActualPath(rawStorage.Replace(ArchToken, "32", StringComparison.Ordinal)));
            native64.Add(ActualPath(rawStorage.Replace(ArchToken, "64", StringComparison.Ordinal)));
            return;
        }

        native.Add(ActualPath(rawStorage));
    }

    /// <summary>Explicit per-artifact download info from a modern version JSON, if present.</summary>
    public MojangLibraryDownloadInfo? MojangDownloads { get; set; }

    /// <summary>
    /// Builds the download requests needed to put this library on disk.
    /// </summary>
    /// <param name="failedLocalFiles">
    /// Receives the paths of local-hint files that are missing. A local library is never downloaded;
    /// if it is absent, that is an error the caller has to surface.
    /// </param>
    /// <returns>Requests to run; empty when everything is already cached or local.</returns>
    public List<NetRequest> GetDownloads(
        RuntimeContext runtimeContext,
        HttpClient client,
        HttpMetaCache cache,
        List<string> failedLocalFiles,
        string overridePath = "")
    {
        var result = new List<NetRequest>();

        var stale = IsAlwaysStale;
        var local = IsLocal;

        bool CheckLocalFile(string storage)
        {
            var fullPath = FileSystem.PathCombine(overridePath, Path.GetFileName(storage));

            if (File.Exists(fullPath))
            {
                return true;
            }

            failedLocalFiles.Add(fullPath);
            return false;
        }

        bool AddDownload(string storage, string url, string sha1)
        {
            if (local)
            {
                return CheckLocalFile(storage);
            }

            var entry = cache.ResolveEntry("libraries", storage);

            if (stale)
            {
                entry.IsStale = true;
            }

            // Already cached and current: nothing to do.
            if (!entry.IsStale)
            {
                return true;
            }

            var options = NetRequestOptions.MakeEternal;

            if (stale)
            {
                options |= NetRequestOptions.AcceptLocalFiles;
            }

            // Libraries are immutable once published, so their cache entries never time out.
            var download = Download.Make(client, new Uri(url), new MetaCacheSink(entry, cache, isEternal: true));
            download.Options = options;

            if (sha1.Length != 0)
            {
                download.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA1, Convert.FromHexString(sha1)));
            }

            result.Add(download);
            return true;
        }

        var rawStorage = StorageSuffix(runtimeContext);

        if (MojangDownloads is not null)
        {
            AddMojangDownloads(runtimeContext, rawStorage, AddDownload);
            return result;
        }

        // Legacy path: derive the URL from the Maven coordinate.
        var rawUrl = ResolveLegacyUrl(rawStorage);

        if (rawStorage.Contains(ArchToken, StringComparison.Ordinal))
        {
            AddDownload(
                rawStorage.Replace(ArchToken, "32", StringComparison.Ordinal),
                rawUrl.Replace(ArchToken, "32", StringComparison.Ordinal),
                string.Empty);

            AddDownload(
                rawStorage.Replace(ArchToken, "64", StringComparison.Ordinal),
                rawUrl.Replace(ArchToken, "64", StringComparison.Ordinal),
                string.Empty);
        }
        else
        {
            AddDownload(rawStorage, rawUrl, string.Empty);
        }

        return result;
    }

    private void AddMojangDownloads(
        RuntimeContext runtimeContext,
        string rawStorage,
        Func<string, string, string, bool> addDownload)
    {
        if (!IsNative)
        {
            // A java library with no artifact block is silently ignored, matching upstream.
            if (MojangDownloads!.Artifact is { } artifact)
            {
                addDownload(rawStorage, artifact.Url, artifact.Sha1);
            }

            return;
        }

        var nativeClassifier = GetCompatibleNative(runtimeContext);

        // No native for this platform: nothing to fetch, and that is not an error.
        if (nativeClassifier is null)
        {
            return;
        }

        if (!nativeClassifier.Contains(ArchToken, StringComparison.Ordinal))
        {
            if (MojangDownloads!.GetDownloadInfo(nativeClassifier) is { } info)
            {
                addDownload(rawStorage, info.Url, info.Sha1);
            }

            return;
        }

        // The "${arch}" hack again, this time against the classifier map.
        foreach (var bitness in (ReadOnlySpan<string>)["32", "64"])
        {
            var classifier = nativeClassifier.Replace(ArchToken, bitness, StringComparison.Ordinal);

            if (MojangDownloads!.GetDownloadInfo(classifier) is { } info)
            {
                addDownload(
                    rawStorage.Replace(ArchToken, bitness, StringComparison.Ordinal),
                    info.Url,
                    info.Sha1);
            }
        }
    }

    private string ResolveLegacyUrl(string rawStorage)
    {
        if (AbsoluteUrl.Length != 0)
        {
            return AbsoluteUrl;
        }

        if (RepositoryUrl.Length == 0)
        {
            return BuildConfig.Instance.LibraryBase + rawStorage;
        }

        return RepositoryUrl.EndsWith('/')
            ? RepositoryUrl + rawStorage
            : RepositoryUrl + '/' + rawStorage;
    }

    /// <summary>
    /// The coordinate with its classifier swapped for the platform's native one.
    /// </summary>
    /// <remarks>
    /// When nothing matches, upstream deliberately substitutes "INVALID" rather than failing, so the
    /// resulting path is obviously wrong instead of silently colliding with the non-native jar.
    /// </remarks>
    private GradleSpecifier NativeSpecifier(RuntimeContext runtimeContext)
    {
        var spec = new GradleSpecifier(Name.Serialize())
        {
            Classifier = GetCompatibleNative(runtimeContext) ?? "INVALID",
        };

        return spec;
    }

    public override string ToString() => Name.Serialize();
}
