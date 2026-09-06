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
 * Ported from launcher/minecraft/AssetsUtils.{h,cpp}.
 *
 * Minecraft's assets — sounds, language files, icons — live in a content-addressed store shared by
 * every instance. The index maps a logical name ("minecraft/sounds/step/grass1.ogg") to a SHA-1, and
 * the file itself sits at objects/<first two hex chars>/<full hash>. Thousands of small files, so
 * downloading only what is missing matters.
 *
 * THREE LAYOUTS, and the index says which:
 *   normal          — the game reads the hashed store directly (1.7.3+)
 *   virtual         — the game wants a real directory tree, reconstructed from the store
 *   mapToResources  — the tree goes in the instance's own resources/ folder (very old versions)
 *
 * Upstream's own comment on this file is "FIXME: this is absolutely horrendous. REDO!!!!". The shape
 * is kept because the on-disk layout is shared with every other launcher, but the parsing is
 * straightforward rather than a QVariantMap round trip.
 */

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;

namespace ExtremeLauncher.Minecraft;

/// <summary>One file in the asset store.</summary>
public sealed class AssetObject
{
    public required string Hash { get; init; }

    public long Size { get; init; }

    /// <summary>
    /// Path within the object store: the first two characters of the hash, then the whole hash.
    /// </summary>
    /// <remarks>The fan-out exists so no single directory holds tens of thousands of entries.</remarks>
    public string RelativePath => $"{Hash[..2]}/{Hash}";

    public Uri GetUrl(string resourceBase) => new(resourceBase + RelativePath);

    public string GetLocalPath(string assetsDirectory)
        => FileSystem.PathCombine(assetsDirectory, "objects", RelativePath);

    /// <summary>
    /// Builds a download for this object, or null when the local copy is already good.
    /// </summary>
    /// <remarks>
    /// The "already good" test is existence plus exact size — deliberately not a hash check. Hashing
    /// every one of several thousand files on every launch would cost more than it saves; the size
    /// catches truncated downloads, and the checksum validator catches corruption on the way in.
    /// </remarks>
    public Download? CreateDownload(HttpClient client, string assetsDirectory, string resourceBase)
    {
        var path = GetLocalPath(assetsDirectory);
        var info = new FileInfo(path);

        if (info.Exists && info.Length == Size)
        {
            return null;
        }

        var download = Download.MakeFile(client, GetUrl(resourceBase), path);

        if (Hash.Length != 0)
        {
            download.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA1, Convert.FromHexString(Hash)));
        }

        return download;
    }
}

public sealed class AssetsIndex
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Logical name to object, e.g. "minecraft/sounds/step/grass1.ogg".</summary>
    public Dictionary<string, AssetObject> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>The game wants a real directory tree rather than the hashed store.</summary>
    public bool IsVirtual { get; set; }

    /// <summary>The tree belongs in the instance's own resources/ folder.</summary>
    public bool MapToResources { get; set; }

    /// <summary>Parses an asset index document.</summary>
    public static AssetsIndex FromJson(JsonObject root, string assetsId)
    {
        ArgumentNullException.ThrowIfNull(root);

        var index = new AssetsIndex
        {
            Id = assetsId,
            IsVirtual = Json.EnsureBoolean(root, "virtual"),
            MapToResources = Json.EnsureBoolean(root, "map_to_resources"),
        };

        if (root["objects"] is not JsonObject objects)
        {
            return index;
        }

        foreach (var (name, value) in objects)
        {
            if (value is not JsonObject entry)
            {
                continue;
            }

            index.Objects[name] = new AssetObject
            {
                Hash = Json.RequireString(entry, "hash"),
                Size = (long)Json.EnsureDouble(entry, "size"),
            };
        }

        return index;
    }

    /// <summary>Loads an index from the shared assets directory.</summary>
    public static AssetsIndex? Load(string assetsDirectory, string assetsId)
    {
        var path = IndexPath(assetsDirectory, assetsId);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return FromJson(Json.RequireObject(Json.RequireDocumentFromFile(path)), assetsId);
        }
        catch (Exception e) when (e is JsonException or FileSystemException)
        {
            return null;
        }
    }

    public static string IndexPath(string assetsDirectory, string assetsId)
        => FileSystem.PathCombine(assetsDirectory, "indexes", $"{assetsId}.json");

    /// <summary>Where the game should be told to look for assets.</summary>
    /// <remarks>
    /// QUIRK, preserved: a normal (non-virtual, non-mapped) index also returns the virtual root, even
    /// though nothing is reconstructed there. It only feeds the pre-1.7.3 <c>${game_assets}</c> token,
    /// which modern versions ignore, so the wrong answer is harmless — but it is upstream's answer.
    /// </remarks>
    public string GetAssetsDir(string assetsDirectory, string resourcesFolder)
        => MapToResources && !IsVirtual
            ? resourcesFolder
            : FileSystem.PathCombine(assetsDirectory, "virtual", Id);

    /// <summary>Every object that is missing or the wrong size.</summary>
    public List<Download> CreateDownloads(HttpClient client, string assetsDirectory, string resourceBase)
    {
        var downloads = new List<Download>();

        foreach (var (_, asset) in Objects)
        {
            if (asset.CreateDownload(client, assetsDirectory, resourceBase) is { } download)
            {
                downloads.Add(download);
            }
        }

        return downloads;
    }

    /// <summary>A job that fetches everything missing, or null when nothing is.</summary>
    public NetJob? CreateDownloadJob(HttpClient client, string assetsDirectory, string resourceBase)
    {
        var downloads = CreateDownloads(client, assetsDirectory, resourceBase);

        if (downloads.Count == 0)
        {
            return null;
        }

        var job = new NetJob($"Assets for {Id}", client);

        foreach (var download in downloads)
        {
            job.AddNetAction(download);
        }

        return job;
    }

    /// <summary>
    /// Materialises the hashed store into a real directory tree, for versions that need one.
    /// </summary>
    /// <returns>The directory the game should be pointed at, or null when nothing was needed.</returns>
    public string? ReconstructVirtualTree(string assetsDirectory, string resourcesFolder)
    {
        if (!IsVirtual && !MapToResources)
        {
            return null;
        }

        var target = GetAssetsDir(assetsDirectory, resourcesFolder);

        foreach (var (name, asset) in Objects)
        {
            var source = asset.GetLocalPath(assetsDirectory);
            var destination = FileSystem.PathCombine(target, name);

            if (!File.Exists(source))
            {
                continue;
            }

            // Copy rather than link: these trees are small, and the game writes into some of them.
            var existing = new FileInfo(destination);

            if (existing.Exists && existing.Length == asset.Size)
            {
                continue;
            }

            FileSystem.EnsureFilePathExists(destination);
            File.Copy(source, destination, overwrite: true);
        }

        return target;
    }
}
