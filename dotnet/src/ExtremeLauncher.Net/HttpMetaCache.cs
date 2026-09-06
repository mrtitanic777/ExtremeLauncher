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
 * Ported from launcher/net/HttpMetaCache.{h,cpp}.
 *
 * An on-disk HTTP cache keyed by (base, relative path). "Bases" are named roots -- "assets",
 * "libraries", "meta" and so on -- each mapping to a directory. The index is a JSON sidecar recording
 * each cached file's ETag, Last-Modified, md5, and expiry, so a later request can be made conditional
 * instead of re-downloading.
 *
 * The index format is version "1" and is written by real installs, so it is preserved exactly:
 *   { "version": "1",
 *     "entries": [ { "base", "path", "md5sum", "etag", "last_changed_timestamp",
 *                    "remote_changed_timestamp", "eternal" | ("current_age" + "max_age") } ] }
 *
 * Stale entries are never written out -- they are, in upstream's words, dead.
 */

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Net;

/// <summary>One cached resource's metadata.</summary>
public sealed class MetaEntry
{
    public string BaseId { get; set; } = string.Empty;

    public string BasePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string Md5Sum { get; set; } = string.Empty;

    public string ETag { get; set; } = string.Empty;

    /// <summary>Local mtime in milliseconds since the Unix epoch, used to detect outside edits.</summary>
    public long LocalChangedTimestamp { get; set; }

    /// <summary>The server's Last-Modified, kept verbatim as an RFC 2822 string.</summary>
    public string RemoteChangedTimestamp { get; set; } = string.Empty;

    public long CurrentAge { get; set; }

    public long MaximumAge { get; set; }

    /// <summary>Eternal entries never expire.</summary>
    public bool IsEternal { get; set; }

    /// <summary>A stale entry is one that must be re-fetched before use.</summary>
    public bool IsStale { get; set; } = true;

    public string GetFullPath() => FileSystem.PathCombine(BasePath, RelativePath);

    public bool IsExpired(long offset) => !IsEternal && CurrentAge >= MaximumAge - offset;
}

public sealed class HttpMetaCache
{
    private const string IndexVersion = "1";

    private readonly Dictionary<string, EntryMap> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private readonly string _indexFile;

    public HttpMetaCache(string indexFile = "") => _indexFile = indexFile;

    /// <summary>Set by callers that batch writes; when false, <see cref="SaveEventually"/> is a no-op.</summary>
    public bool AutoSave { get; set; } = true;

    private sealed class EntryMap
    {
        public string BasePath { get; set; } = string.Empty;

        public Dictionary<string, MetaEntry> Entries { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Registers a named root, e.g. "assets" -&gt; &lt;data&gt;/assets.</summary>
    public void AddBase(string @base, string baseRoot)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(@base))
            {
                return;
            }

            _entries[@base] = new EntryMap { BasePath = baseRoot };
        }
    }

    public string GetBasePath(string @base)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(@base, out var map) ? map.BasePath : string.Empty;
        }
    }

    /// <summary>Looks an entry up without validating it. Usually you want <see cref="ResolveEntry"/>.</summary>
    public MetaEntry? GetEntry(string @base, string resourcePath)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(@base, out var map) && map.Entries.TryGetValue(resourcePath, out var entry)
                ? entry
                : null;
        }
    }

    /// <summary>
    /// Looks an entry up and verifies it still describes the file on disk, returning a fresh stale
    /// entry if anything fails to line up.
    /// </summary>
    public MetaEntry ResolveEntry(string @base, string resourcePath, string expectedETag = "")
    {
        resourcePath = FileSystem.RemoveInvalidPathChars(resourcePath);

        var entry = GetEntry(@base, resourcePath);

        if (entry is null)
        {
            return StaleEntry(@base, resourcePath);
        }

        var realPath = FileSystem.PathCombine(GetBasePath(@base), resourcePath);
        var info = new FileInfo(realPath);

        // Gone from disk: disown it.
        if (!info.Exists || info.Length == 0)
        {
            Forget(@base, resourcePath);
            return StaleEntry(@base, resourcePath);
        }

        // Caller expected a specific version and this is not it.
        if (expectedETag.Length != 0 && !string.Equals(expectedETag, entry.ETag, StringComparison.Ordinal))
        {
            Forget(@base, resourcePath);
            return StaleEntry(@base, resourcePath);
        }

        var lastChanged = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

        if (lastChanged != entry.LocalChangedTimestamp)
        {
            // Edited behind our back: only trust it if the content still hashes the same.
            string md5;

            try
            {
                md5 = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(realPath))).ToLowerInvariant();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Forget(@base, resourcePath);
                return StaleEntry(@base, resourcePath);
            }

            if (!string.Equals(entry.Md5Sum, md5, StringComparison.OrdinalIgnoreCase))
            {
                Forget(@base, resourcePath);
                return StaleEntry(@base, resourcePath);
            }

            entry.LocalChangedTimestamp = lastChanged;
            SaveEventually();
        }

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (entry.IsExpired(currentTime - (lastChanged / 1000)))
        {
            Forget(@base, resourcePath);
            return StaleEntry(@base, resourcePath);
        }

        entry.BasePath = GetBasePath(@base);
        return entry;
    }

    /// <summary>Files a freshly-downloaded entry. Stale entries are rejected.</summary>
    public bool UpdateEntry(MetaEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.BaseId, out var map))
            {
                return false;
            }

            if (entry.IsStale)
            {
                return false;
            }

            map.Entries[entry.RelativePath] = entry;
        }

        SaveEventually();
        return true;
    }

    public bool EvictEntry(MetaEntry? entry)
    {
        if (entry is null)
        {
            return false;
        }

        entry.IsStale = true;
        SaveEventually();
        return true;
    }

    /// <summary>Marks everything stale and deletes every base directory.</summary>
    public void EvictAll()
    {
        List<EntryMap> maps;

        lock (_gate)
        {
            maps = [.. _entries.Values];
        }

        foreach (var map in maps)
        {
            foreach (var entry in map.Entries.Values)
            {
                entry.IsStale = true;
            }

            map.Entries.Clear();
            FileSystem.DeletePath(map.BasePath);
        }

        SaveEventually();
    }

    private void Forget(string @base, string resourcePath)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(@base, out var map))
            {
                map.Entries.Remove(resourcePath);
            }
        }
    }

    private MetaEntry StaleEntry(string @base, string resourcePath)
        => new()
        {
            BaseId = @base,
            BasePath = GetBasePath(@base),
            RelativePath = resourcePath,
            IsStale = true,
        };

    // ================================================================== index persistence

    public void Load()
    {
        if (_indexFile.Length == 0 || !File.Exists(_indexFile))
        {
            return;
        }

        JsonObject root;

        try
        {
            root = Json.RequireObject(Json.RequireDocumentFromFile(_indexFile), "HttpMetaCache");
        }
        catch (Exception e) when (e is JsonException or FileSystemException)
        {
            // A corrupt index costs us the cache, not the launcher.
            return;
        }

        if (Json.EnsureString(root, "version") != IndexVersion)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var element in Json.EnsureArray(root, "entries"))
            {
                if (element is not JsonObject item)
                {
                    continue;
                }

                var @base = Json.EnsureString(item, "base");

                // Entries for bases nobody registered are dropped.
                if (!_entries.TryGetValue(@base, out var map))
                {
                    continue;
                }

                var entry = new MetaEntry
                {
                    BaseId = @base,
                    RelativePath = Json.EnsureString(item, "path"),
                    Md5Sum = Json.EnsureString(item, "md5sum"),
                    ETag = Json.EnsureString(item, "etag"),
                    LocalChangedTimestamp = (long)Json.EnsureDouble(item, "last_changed_timestamp"),
                    RemoteChangedTimestamp = Json.EnsureString(item, "remote_changed_timestamp"),
                    IsEternal = Json.EnsureBoolean(item, "eternal"),

                    // Presumed innocent until ResolveEntry examines it.
                    IsStale = false,
                };

                if (!entry.IsEternal)
                {
                    entry.CurrentAge = (long)Json.EnsureDouble(item, "current_age");
                    entry.MaximumAge = (long)Json.EnsureDouble(item, "max_age");
                }

                map.Entries[entry.RelativePath] = entry;
            }
        }
    }

    /// <summary>
    /// Upstream debounces this behind a 30-second timer. Here it writes immediately unless
    /// <see cref="AutoSave"/> is off, which keeps tests deterministic and avoids a background timer
    /// whose only job is to survive until process exit.
    /// </summary>
    public void SaveEventually()
    {
        if (AutoSave)
        {
            SaveNow();
        }
    }

    public void SaveNow()
    {
        if (_indexFile.Length == 0)
        {
            return;
        }

        var entriesArray = new JsonArray();

        lock (_gate)
        {
            foreach (var map in _entries.Values)
            {
                foreach (var entry in map.Entries.Values)
                {
                    // Stale entries are dead; they never reach the index.
                    if (entry.IsStale)
                    {
                        continue;
                    }

                    var item = new JsonObject();
                    Json.WriteString(item, "base", entry.BaseId);
                    Json.WriteString(item, "path", entry.RelativePath);
                    Json.WriteString(item, "md5sum", entry.Md5Sum);
                    Json.WriteString(item, "etag", entry.ETag);
                    item["last_changed_timestamp"] = entry.LocalChangedTimestamp;

                    if (entry.RemoteChangedTimestamp.Length != 0)
                    {
                        item["remote_changed_timestamp"] = entry.RemoteChangedTimestamp;
                    }

                    if (entry.IsEternal)
                    {
                        item["eternal"] = true;
                    }
                    else
                    {
                        item["current_age"] = entry.CurrentAge;
                        item["max_age"] = entry.MaximumAge;
                    }

                    entriesArray.Add(item);
                }
            }
        }

        var root = new JsonObject();
        Json.WriteString(root, "version", IndexVersion);
        root["entries"] = entriesArray;

        try
        {
            Json.Write(root, _indexFile);
        }
        catch (FileSystemException)
        {
            // Losing the index costs performance, not correctness.
        }
    }
}
