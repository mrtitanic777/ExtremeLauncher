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
 * Ported from launcher/meta/BaseEntity.{h,cpp}.
 *
 * The fetch-and-cache flow every metadata document loads through: try the copy on disk, verify its
 * hash, and only go to the network when that fails or is stale. The index, each version list and each
 * version are all entities.
 *
 * RENAMED from BaseEntity: "Base" says nothing, and the type is the metadata document itself.
 *
 * THE PARSING VALIDATOR IS THE INTERESTING PART. Rather than download, save, then parse, the parse
 * happens *inside* validation — so a document that fails to parse fails the download, and the sink
 * discards it instead of writing a corrupt file over a good one. That is exactly the guarantee
 * FileSink already provides for checksums, reused for structure.
 */

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Meta;

/// <summary>Whether a load may reach the network.</summary>
public enum NetMode
{
    Offline,
    Online,
}

public abstract class MetaEntity
{
    public enum LoadStatus
    {
        NotLoaded,
        Local,
        Remote,
    }

    /// <summary>Path of this document relative to the meta root, e.g. "net.minecraft/index.json".</summary>
    public abstract string LocalFilename { get; }

    /// <summary>The expected hash, supplied by whatever referenced this document.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The hash of the copy actually on disk.</summary>
    public string FileSha256 { get; internal set; } = string.Empty;

    public LoadStatus Status { get; internal set; } = LoadStatus.NotLoaded;

    /// <summary>
    /// Whether this document can be trusted as current.
    /// </summary>
    /// <remarks>
    /// With no expected hash — the index itself, which nothing vouches for — only a remote fetch
    /// counts. With one, any load counts provided the hashes agree.
    /// </remarks>
    /// <remarks>
    /// Virtual where upstream's is not. <c>Meta::Version</c> shadows this with a stricter test, and in
    /// C++ that resolves by static type — call it through a <c>BaseEntity*</c> and you silently get the
    /// looser answer. Making it virtual here means both call sites get the same answer.
    /// </remarks>
    public virtual bool IsLoaded
        => Sha256.Length == 0
            ? Status == LoadStatus.Remote
            : Status != LoadStatus.NotLoaded && string.Equals(Sha256, FileSha256, StringComparison.OrdinalIgnoreCase);

    /*
     * ONE LOAD AT A TIME PER DOCUMENT.
     *
     * Upstream never needs this: it is single-threaded around a Qt event loop, so two loads of the
     * same entity cannot interleave. Here component updates run concurrently (ComponentUpdateTask
     * awaits them with Task.WhenAll), and every one of them wants the same shared index. Two loaders
     * then read, download and rename the same file at once, and on Windows the rename fails outright
     * -- intermittently, and only once a cached copy exists for them to race over.
     *
     * The second loader is not wasted work: it re-checks after the gate and finds the document
     * already current, so it returns without touching the network or the disk.
     *
     * Never disposed, deliberately -- a SemaphoreSlim with no wait handle has nothing to release, and
     * entities live as long as the launcher does.
     */
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    internal async Task<IDisposable> AcquireLoadGateAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new GateRelease(_loadGate);
    }

    private sealed class GateRelease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public GateRelease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }

    public abstract void Parse(JsonObject obj);

    public Uri GetUrl(string metaBaseUrl) => new(new Uri(metaBaseUrl), LocalFilename);

    /// <summary>Builds the task that brings this document up to date.</summary>
    public MetaEntityLoadTask CreateLoadTask(
        HttpClient client,
        HttpMetaCache cache,
        string metaDirectory,
        string metaBaseUrl,
        NetMode mode = NetMode.Online)
        => new(this, client, cache, metaDirectory, metaBaseUrl, mode);
}

/// <summary>
/// A validator that parses the response into its entity, failing the download if it will not parse.
/// </summary>
public sealed class ParsingValidator : IValidator
{
    private readonly MetaEntity _entity;
    private readonly MemoryStream _buffer = new();

    public ParsingValidator(MetaEntity entity) => _entity = entity;

    public void Init() => _buffer.SetLength(0);

    public void Write(ReadOnlySpan<byte> data) => _buffer.Write(data);

    public void Abort() => _buffer.SetLength(0);

    /// <summary>Why the last parse failed, or an empty string. Kept for diagnostics.</summary>
    public string FailReason { get; private set; } = string.Empty;

    public bool Validate()
    {
        try
        {
            _entity.Parse(Json.RequireObject(Json.RequireDocument(_buffer.ToArray(), _entity.LocalFilename)));

            FailReason = string.Empty;
            return true;
        }
        catch (LauncherException e)
        {
            /*
             * Returning false makes the sink throw the file away rather than publish it -- a document
             * that will not parse must not end up cached, or every later run reads the same bad file.
             *
             * The REASON is kept rather than discarded. Upstream drops it, which leaves a user whose
             * meta server serves something unexpected with "load failed" and nothing else; the parse
             * error names the field.
             */
            FailReason = e.Message;
            return false;
        }
    }
}

public sealed class MetaEntityLoadTask : LauncherTask
{
    private readonly MetaEntity _entity;
    private readonly HttpClient _client;
    private readonly HttpMetaCache _cache;
    private readonly string _metaDirectory;
    private readonly string _metaBaseUrl;
    private readonly NetMode _mode;

    public MetaEntityLoadTask(
        MetaEntity entity,
        HttpClient client,
        HttpMetaCache cache,
        string metaDirectory,
        string metaBaseUrl,
        NetMode mode)
        : base($"Load meta for {entity.LocalFilename}")
    {
        _entity = entity;
        _client = client;
        _cache = cache;
        _metaDirectory = metaDirectory;
        _metaBaseUrl = metaBaseUrl;
        _mode = mode;
    }

    public override bool CanAbort => true;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var gate = await _entity.AcquireLoadGateAsync(cancellationToken).ConfigureAwait(false);

        var path = FileSystem.PathCombine(_metaDirectory, _entity.LocalFilename);
        var hashMatches = false;

        if (File.Exists(path))
        {
            hashMatches = TryLoadLocal(path);
        }

        // Offline with anything at all loaded is as good as it gets.
        var loadedOffline = _entity.Status != MetaEntity.LoadStatus.NotLoaded && _mode == NetMode.Offline;

        // With no expected hash, only a previous remote fetch counts as current.
        var loadedCurrent = _entity.Sha256.Length == 0
            ? _entity.Status == MetaEntity.LoadStatus.Remote
            : hashMatches;

        if (loadedOffline || loadedCurrent)
        {
            return;
        }

        if (_mode == NetMode.Offline)
        {
            throw new TaskFailedException($"{_entity.LocalFilename} is not available offline");
        }

        await FetchAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <returns>Whether the on-disk copy's hash matches what was expected.</returns>
    private bool TryLoadLocal(string path)
    {
        try
        {
            byte[] data = [];

            // Only re-read when there is nothing loaded or no hash recorded.
            if (_entity.Status == MetaEntity.LoadStatus.NotLoaded || _entity.FileSha256.Length == 0)
            {
                SetStatus("Loading local file");
                data = FileSystem.Read(path);
                _entity.FileSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            }

            var matches = string.Equals(_entity.Sha256, _entity.FileSha256, StringComparison.OrdinalIgnoreCase);

            // Online, a known-bad local copy is worse than none: fall through to a fresh download.
            if (_mode == NetMode.Online && _entity.Sha256.Length != 0 && !matches)
            {
                throw new LauncherException("mismatched checksum");
            }

            if (_entity.Status == MetaEntity.LoadStatus.NotLoaded)
            {
                _entity.Parse(Json.RequireObject(Json.RequireDocument(data, path)));
                _entity.Status = MetaEntity.LoadStatus.Local;
            }

            return matches;
        }
        catch (Exception e) when (e is LauncherException or IOException)
        {
            // Delete it so it is never considered again, rather than failing repeatedly on it.
            FileSystem.DeletePath(path);
            _entity.Status = MetaEntity.LoadStatus.NotLoaded;

            return false;
        }
    }

    private async Task FetchAsync(string path, CancellationToken cancellationToken)
    {
        SetStatus($"Downloading meta file {_entity.LocalFilename}");

        var entry = _cache.ResolveEntry("meta", _entity.LocalFilename);

        // Always re-fetch: the caller only gets here because what is on disk is not current.
        entry.IsStale = true;

        var download = Download.Make(_client, _entity.GetUrl(_metaBaseUrl), new MetaCacheSink(entry, _cache));

        if (_entity.Sha256.Length != 0)
        {
            download.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA256, Convert.FromHexString(_entity.Sha256)));
        }

        // Parses as it validates, so an unparseable document never reaches the disk.
        var parsing = new ParsingValidator(_entity);
        download.AddValidator(parsing);

        if (!await download.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            // The parse error names the offending field; the download's own reason only says the
            // response failed validation, which is true of a bad checksum too.
            var reason = parsing.FailReason.Length != 0 ? parsing.FailReason : download.FailReason;

            throw new TaskFailedException($"Failed to load {_entity.LocalFilename}: {reason}");
        }

        _entity.Status = MetaEntity.LoadStatus.Remote;

        if (File.Exists(path))
        {
            _entity.FileSha256 = Convert.ToHexString(SHA256.HashData(FileSystem.Read(path))).ToLowerInvariant();
        }
    }
}
