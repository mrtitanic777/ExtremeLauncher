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
 * Ported from launcher/meta/Version.{h,cpp} and the Require half of meta/JsonFormat.h.
 *
 * One entry in the metadata index: "net.minecraft 1.20.1", "net.fabricmc.fabric-loader 0.14.21".
 * Versions declare what they require and what they conflict with, which is how the launcher works out
 * that Fabric 0.14 needs Minecraft 1.20 and refuses to sit next to Forge.
 *
 * RENAMED from upstream's Meta::Version. There would otherwise be three types called Version in this
 * solution -- System.Version, Core.Version (the FlexVer comparator) and this one -- which is a
 * genuine footgun rather than a naming nicety.
 */

using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.Meta;

public sealed class MetaVersion : MetaEntity
{
    public override string LocalFilename => $"{Uid}/{VersionString}.json";

    public override void Parse(System.Text.Json.Nodes.JsonObject obj) => MetaJsonFormat.ParseVersion(obj, this);

    public MetaVersion(string uid, string version)
    {
        Uid = uid;
        VersionString = version;
    }

    public string Uid { get; }

    public string VersionString { get; }

    public string Name { get; set; } = string.Empty;

    /// <summary>"release", "snapshot", "beta"…</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Release time as Unix seconds.</summary>
    public long RawTime { get; set; }

    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeSeconds(RawTime);

    public bool IsRecommended { get; set; }

    /// <summary>Whether the containing list vouches for this version's recommended flag.</summary>
    public bool ProvidesRecommendations { get; set; }

    /// <summary>A volatile version may be replaced silently during resolution.</summary>
    public bool IsVolatile { get; set; }

    public RequireSet Requires { get; private set; } = [];

    public RequireSet Conflicts { get; private set; } = [];

    /// <summary>
    /// The actual patch contents, once the full version file has been fetched.
    /// </summary>
    /// <remarks>
    /// Populated by OneSixVersionFormat, which is not ported yet; index-level parsing leaves it null,
    /// exactly as upstream does before the version itself is loaded.
    /// </remarks>
    public VersionFile? Data { get; set; }

    /// <summary>
    /// A version is loaded only when its patch body is in memory AND the document it came from is
    /// current.
    /// </summary>
    /// <remarks>
    /// Both halves matter, and an earlier draft of this port dropped the second. Without it, a version
    /// parsed out of a stale file on disk — one whose hash no longer matches the index — reports itself
    /// loaded, and nothing ever fetches the current copy.
    /// </remarks>
    public override bool IsLoaded => Data is not null && base.IsLoaded;

    public void SetRequires(RequireSet requires, RequireSet conflicts)
    {
        Requires = requires;
        Conflicts = conflicts;
    }

    /// <summary>A comparable form for ordering, via the launcher's FlexVer comparator.</summary>
    public Core.Version ToComparableVersion() => new(VersionString);

    /// <summary>Folds in a fully-loaded version, keeping whatever the other side actually knows.</summary>
    public void Merge(MetaVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Type.Length != 0)
        {
            Type = other.Type;
        }

        if (other.RawTime != 0)
        {
            RawTime = other.RawTime;
        }

        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }

        if (other.Requires.Count != 0 || other.Conflicts.Count != 0)
        {
            SetRequires(other.Requires, other.Conflicts);
        }

        IsVolatile = other.IsVolatile;

        if (other.Data is not null)
        {
            Data = other.Data;
        }
    }

    /// <summary>
    /// Folds in an entry from a version list, which is authoritative about recommendations but
    /// carries no patch data.
    /// </summary>
    public void MergeFromList(MetaVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Type.Length != 0)
        {
            Type = other.Type;
        }

        if (other.RawTime != 0)
        {
            RawTime = other.RawTime;
        }

        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }

        if (other.Requires.Count != 0 || other.Conflicts.Count != 0)
        {
            SetRequires(other.Requires, other.Conflicts);
        }

        // Only a list is entitled to say what is recommended.
        if (other.ProvidesRecommendations)
        {
            ProvidesRecommendations = true;
            IsRecommended = other.IsRecommended;
        }
    }

    public override string ToString() => $"{Uid} {VersionString}";
}
