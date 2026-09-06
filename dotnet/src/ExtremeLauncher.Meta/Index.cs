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
 * Ported from launcher/meta/Index.{h,cpp}.
 *
 * The root of the metadata tree: every package the meta server knows about, keyed by uid. Upstream is
 * a QAbstractListModel; the model surface belongs to the UI wave, so only the registry semantics are
 * here.
 *
 * THE LOAD CHAIN IS A DEPENDENCY CHAIN. The index names the hash of each version list, and a version
 * list names the hash of each version document, so bringing one version up to date means refreshing
 * the two above it first -- otherwise a changed document is validated against a stale hash.
 */

using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Meta;

public sealed class Index : MetaEntity
{
    /// <summary>The index sits at the meta root and nothing vouches for its hash.</summary>
    public override string LocalFilename => "index.json";

    public override void Parse(System.Text.Json.Nodes.JsonObject obj) => MetaJsonFormat.ParseIndex(obj, this);

    private readonly List<VersionList> _lists = [];
    private readonly Dictionary<string, VersionList> _uids = new(StringComparer.Ordinal);

    public Index()
    {
    }

    public Index(IEnumerable<VersionList> lists)
    {
        foreach (var list in lists)
        {
            _lists.Add(list);
            _uids[list.Uid] = list;
        }
    }

    public IReadOnlyList<VersionList> Lists => _lists;

    public bool HasUid(string uid) => _uids.ContainsKey(uid);

    /// <summary>
    /// Returns the list for <paramref name="uid"/>, CREATING an empty one if it is unknown.
    /// </summary>
    /// <remarks>
    /// Never returns null -- pinned by <c>Index_test.cpp</c>, which asserts that asking for a uid that
    /// was never registered still yields a list. That is what lets an instance reference a package
    /// before the index has been fetched.
    /// </remarks>
    public VersionList Get(string uid)
    {
        if (_uids.TryGetValue(uid, out var existing))
        {
            return existing;
        }

        var created = new VersionList(uid);
        _uids[uid] = created;
        _lists.Add(created);

        return created;
    }

    /// <summary>Looks a version up. Null when neither the list nor the version is known yet.</summary>
    public MetaVersion? Get(string uid, string version) => Get(uid).GetVersion(version);

    /// <summary>
    /// Looks a version up, CREATING placeholders for the list and the version if either is unknown.
    /// </summary>
    /// <remarks>
    /// The cold-cache path: on a first run the index has not been read, so nothing is known, and
    /// loading has to be able to construct <c>&lt;uid&gt;/&lt;version&gt;.json</c> anyway.
    /// </remarks>
    public MetaVersion GetOrCreate(string uid, string version) => Get(uid).GetOrCreateVersion(version);

    /// <summary>
    /// Builds the task that brings one version document up to date, fetching what it depends on first.
    /// </summary>
    /// <remarks>
    /// THE ORDER IS A DEPENDENCY CHAIN, not a preference: the index names the hash of each version
    /// list, and a version list names the hash of each version document. Loading a version without
    /// first refreshing the two above it means validating it against a hash that may be stale, so a
    /// changed document would be accepted or rejected on old information.
    ///
    /// The index itself is skipped when it has already been fetched remotely this session, since it
    /// is the one document nothing vouches for and re-fetching it every time would make every launch
    /// wait on the meta server.
    ///
    /// Offline, the chain collapses to the version document alone — the other two would have nothing
    /// to do, and the load either finds a cached copy or fails.
    /// </remarks>
    public SequentialTask CreateLoadVersionTask(
        string uid,
        string version,
        HttpClient client,
        HttpMetaCache cache,
        string metaDirectory,
        string metaBaseUrl,
        NetMode mode = NetMode.Online,
        bool force = false)
    {
        var task = new SequentialTask($"Load meta for {uid}:{version}");

        var versionList = Get(uid);
        var metaVersion = versionList.GetOrCreateVersion(version);

        if (mode == NetMode.Offline)
        {
            task.AddTask(metaVersion.CreateLoadTask(client, cache, metaDirectory, metaBaseUrl, mode));
            return task;
        }

        if (Status != LoadStatus.Remote || force)
        {
            task.AddTask(CreateLoadTask(client, cache, metaDirectory, metaBaseUrl, mode));
        }

        task.AddTask(versionList.CreateLoadTask(client, cache, metaDirectory, metaBaseUrl, mode));
        task.AddTask(metaVersion.CreateLoadTask(client, cache, metaDirectory, metaBaseUrl, mode));

        return task;
    }

    /// <summary>
    /// Runs <see cref="CreateLoadVersionTask"/> and hands back the version.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>getLoadedVersion</c> spins a nested QEventLoop to make this synchronous, which is
    /// how a UI thread ends up re-entered halfway through a network fetch. Awaitable here instead.
    /// Returns the version whether or not the load succeeded — a caller that cares checks
    /// <see cref="MetaEntity.IsLoaded"/>, exactly as upstream's callers do.
    /// </remarks>
    public async Task<MetaVersion> GetLoadedVersionAsync(
        string uid,
        string version,
        HttpClient client,
        HttpMetaCache cache,
        string metaDirectory,
        string metaBaseUrl,
        NetMode mode = NetMode.Online,
        CancellationToken cancellationToken = default)
    {
        var task = CreateLoadVersionTask(uid, version, client, cache, metaDirectory, metaBaseUrl, mode);

        await task.RunAsync(cancellationToken).ConfigureAwait(false);

        return GetOrCreate(uid, version);
    }

    /// <summary>
    /// A fetch callback shaped for <see cref="ComponentUpdateTask"/>.
    /// </summary>
    /// <remarks>
    /// The seam that keeps ComponentUpdateTask testable: it takes this delegate rather than reaching
    /// for a global application object the way upstream does.
    /// </remarks>
    public Func<string, string, CancellationToken, Task<bool>> CreateVersionLoader(
        HttpClient client,
        HttpMetaCache cache,
        string metaDirectory,
        string metaBaseUrl,
        NetMode mode = NetMode.Online)
        => async (uid, version, cancellationToken) =>
        {
            var loaded = await GetLoadedVersionAsync(
                uid, version, client, cache, metaDirectory, metaBaseUrl, mode, cancellationToken).ConfigureAwait(false);

            return loaded.Data is not null;
        };

    /// <summary>Folds another index in, merging per-uid rather than replacing.</summary>
    public void Merge(Index other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // First load: take the other side wholesale.
        if (_lists.Count == 0)
        {
            foreach (var list in other._lists)
            {
                _lists.Add(list);
                _uids[list.Uid] = list;
            }

            return;
        }

        foreach (var list in other._lists)
        {
            if (_uids.TryGetValue(list.Uid, out var existing))
            {
                // MergeFromIndex, not Merge: an index entry knows nothing about versions, and using
                // the wrong one here would wipe an already-loaded version list.
                existing.MergeFromIndex(list);
            }
            else
            {
                _lists.Add(list);
                _uids[list.Uid] = list;
            }
        }
    }

    public override string ToString() => $"Index ({_lists.Count} packages)";
}
