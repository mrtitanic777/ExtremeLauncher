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
 * Ported from launcher/meta/VersionList.{h,cpp}.
 *
 * Every known version of one package, newest first. Upstream is a QAbstractListModel; the model
 * surface belongs to the UI wave, so only the collection semantics come across here.
 *
 * MERGE COMES IN TWO FLAVOURS, and conflating them loses data:
 *   MergeFromIndex -- the index only knows a package's name and hash, never its versions. Merging an
 *                     index entry must NOT touch the version list, or a loaded list gets wiped by a
 *                     later index refresh.
 *   Merge          -- a fully-loaded list, which does carry versions.
 */

namespace ExtremeLauncher.Meta;

public sealed class VersionList : MetaEntity
{
    public override string LocalFilename => $"{Uid}/index.json";

    public override void Parse(System.Text.Json.Nodes.JsonObject obj) => MetaJsonFormat.ParseVersionList(obj, this);

    private readonly List<MetaVersion> _versions = [];
    private readonly Dictionary<string, MetaVersion> _lookup = new(StringComparer.Ordinal);

    public VersionList(string uid) => Uid = uid;

    public string Uid { get; }

    public string Name { get; set; } = string.Empty;

    // NOTE: Sha256 is inherited from MetaEntity. Redeclaring it here would shadow the base field that
    // IsLoaded reads, so the hash written by MergeFromIndex would never be the one checked.

    public IReadOnlyList<MetaVersion> Versions => _versions;

    /// <summary>The version the launcher offers by default.</summary>
    public MetaVersion? Recommended { get; private set; }

    public string HumanReadable => Name.Length != 0 ? Name : Uid;

    /// <summary>Looks a version up. Returns null when the list has never heard of it.</summary>
    public MetaVersion? GetVersion(string version) => _lookup.GetValueOrDefault(version);

    /// <summary>
    /// Looks a version up, CREATING an empty placeholder when the list does not have it.
    /// </summary>
    /// <remarks>
    /// This is upstream's <c>getVersion</c>, which always creates — the same pattern as
    /// <c>Index::get(uid)</c>. It exists for the cold-cache case: on a first run nothing has been
    /// fetched, so the list is empty, and asking for "net.minecraft 1.20.1" has to yield an entity
    /// whose URL can be constructed and fetched rather than nothing at all. The plain
    /// <see cref="GetVersion"/> above stays a pure lookup, because callers that mean "is this known?"
    /// should not be quietly populating the list.
    /// </remarks>
    public MetaVersion GetOrCreateVersion(string version)
    {
        if (_lookup.TryGetValue(version, out var existing))
        {
            return existing;
        }

        var created = new MetaVersion(Uid, version);

        _lookup[version] = created;
        _versions.Add(created);

        return created;
    }

    public bool HasVersion(string version) => _lookup.ContainsKey(version);

    /// <summary>Replaces the contents, sorting newest first.</summary>
    public void SetVersions(IEnumerable<MetaVersion> versions)
    {
        _versions.Clear();
        _lookup.Clear();

        _versions.AddRange(versions);

        // Newest first, by release time.
        _versions.Sort((a, b) => b.RawTime.CompareTo(a.RawTime));

        foreach (var version in _versions)
        {
            _lookup[version.VersionString] = version;
        }

        // QUIRK, preserved: the recommended version is the first entry of type "release", not the one
        // flagged recommended in the metadata. Upstream's own comment calls this dumb.
        Recommended = _versions.Find(v => string.Equals(v.Type, "release", StringComparison.Ordinal));
    }

    /// <summary>
    /// Folds in what an index entry knows: the display name and hash, never versions.
    /// </summary>
    public void MergeFromIndex(VersionList other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.Equals(Name, other.Name, StringComparison.Ordinal))
        {
            Name = other.Name;
        }

        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }
    }

    /// <summary>Folds in a fully-loaded list, merging versions by version string.</summary>
    public void Merge(VersionList other)
    {
        ArgumentNullException.ThrowIfNull(other);

        MergeFromIndex(other);

        foreach (var incoming in other._versions)
        {
            MetaVersion version;

            if (_lookup.TryGetValue(incoming.VersionString, out var existing))
            {
                existing.MergeFromList(incoming);
                version = existing;
            }
            else
            {
                version = incoming;
                _lookup[version.VersionString] = version;
                _versions.Add(version);
            }

            Recommended = GetBetterVersion(Recommended, version);
        }
    }

    /// <summary>
    /// Picks between two candidate versions.
    /// </summary>
    /// <remarks>
    /// TYPE BEATS RECENCY: a "release" always wins over a snapshot or beta, however much newer the
    /// other is. Only when the two share a type does the newer one win. That is why a fresh snapshot
    /// does not quietly become the recommended build for a modloader.
    /// </remarks>
    internal static MetaVersion? GetBetterVersion(MetaVersion? a, MetaVersion? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        if (string.Equals(a.Type, b.Type, StringComparison.Ordinal))
        {
            return a.RawTime > b.RawTime ? a : b;
        }

        return string.Equals(a.Type, "release", StringComparison.Ordinal) ? a : b;
    }

    /// <summary>
    /// The version this list recommends for a given parent component and version.
    /// </summary>
    /// <remarks>
    /// EXPLICIT ONLY: a version qualifies solely when it names the parent with an exact
    /// <c>equalsVersion</c> requirement AND is itself marked recommended. A version merely compatible
    /// with the parent is not recommended for it, and a recommended version that says nothing about
    /// the parent does not count either. Returns null rather than falling back — the caller decides
    /// whether to try <see cref="GetLatestForParent"/> next.
    /// </remarks>
    public MetaVersion? GetRecommendedForParent(string uid, string version)
        => _versions.FirstOrDefault(v => v.IsRecommended && RequiresExactly(v, uid, version));

    /// <summary>The newest version compatible with a given parent component and version.</summary>
    public MetaVersion? GetLatestForParent(string uid, string version)
    {
        MetaVersion? latest = null;

        foreach (var candidate in _versions)
        {
            if (RequiresExactly(candidate, uid, version))
            {
                latest = GetBetterVersion(latest, candidate);
            }
        }

        return latest;
    }

    private static bool RequiresExactly(MetaVersion version, string uid, string parentVersion)
        => version.Requires.Any(
            r => string.Equals(r.Uid, uid, StringComparison.Ordinal)
                 && string.Equals(r.EqualsVersion, parentVersion, StringComparison.Ordinal));

    public override string ToString() => $"{Uid} ({_versions.Count} versions)";
}
