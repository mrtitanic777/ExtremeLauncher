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
 * Ported from launcher/java/JavaMetadata.{h,cpp}.
 *
 * One downloadable Java runtime, as the meta server describes it. The launcher fetches a list of these
 * and picks the newest that matches the instance's required major version and the host's OS.
 *
 * TWO DOWNLOAD SHAPES, and the distinction is the whole reason DownloadType exists: an "archive" is a
 * single zip or tar.gz, while a "manifest" is a JSON index of individual files, each fetched
 * separately. Mojang publishes manifests; Adoptium publishes archives.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Java;

public enum DownloadType
{
    /// <summary>A JSON index of individual files, each fetched separately.</summary>
    Manifest,

    /// <summary>A single zip or tarball.</summary>
    Archive,

    Unknown,
}

public sealed class JavaMetadata : IComparable<JavaMetadata>, IEquatable<JavaMetadata>
{
    public string Name { get; set; } = string.Empty;

    public string Vendor { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public DateTimeOffset ReleaseTime { get; set; }

    /// <summary>"sha1" or "sha256"; anything else is treated as sha1 by the download tasks.</summary>
    public string ChecksumType { get; set; } = string.Empty;

    public string ChecksumHash { get; set; } = string.Empty;

    public DownloadType DownloadType { get; set; } = DownloadType.Unknown;

    /// <summary>"jre" or "jdk".</summary>
    public string PackageType { get; set; } = string.Empty;

    public JavaVersion Version { get; set; } = new();

    /// <summary>The OS and architecture this build is for, e.g. "linux-x64". "unknown" when absent.</summary>
    public string RuntimeOS { get; set; } = "unknown";

    /// <summary>What a version list shows for this entry.</summary>
    public string Descriptor => Version.ToString();

    public string TypeString => Vendor;

    // ================================================================== parsing

    public static DownloadType ParseDownloadType(string javaDownload) => javaDownload switch
    {
        "manifest" => DownloadType.Manifest,
        "archive" => DownloadType.Archive,
        _ => DownloadType.Unknown,
    };

    public static string DownloadTypeToString(DownloadType javaDownload) => javaDownload switch
    {
        DownloadType.Manifest => "manifest",
        DownloadType.Archive => "archive",
        _ => "unknown",
    };

    /// <summary>Reads one entry out of the meta server's Java index.</summary>
    /// <exception cref="JsonException">A "checksum" or "version" member is present but not an object.</exception>
    public static JavaMetadata Parse(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var meta = new JavaMetadata
        {
            Name = Json.EnsureString(obj, "name"),
            Vendor = Json.EnsureString(obj, "vendor"),
            Url = Json.EnsureString(obj, "url"),
            DownloadType = ParseDownloadType(Json.EnsureString(obj, "downloadType")),
            PackageType = Json.EnsureString(obj, "packageType"),

            // Defaulted rather than left blank, so a comparison against a real OS string simply misses.
            RuntimeOS = Json.EnsureString(obj, "runtimeOS", "unknown"),
        };

        if (S3Time.TryTimeFromS3Time(Json.EnsureString(obj, "releaseTime"), out var releaseTime))
        {
            meta.ReleaseTime = releaseTime;
        }

        // Required to be an OBJECT when present -- a malformed checksum block is refused rather than
        // read as absent, because silently skipping verification is the wrong failure here.
        if (obj.ContainsKey("checksum"))
        {
            var checksum = Json.RequireObject(obj, "checksum");

            meta.ChecksumHash = Json.EnsureString(checksum, "hash");
            meta.ChecksumType = Json.EnsureString(checksum, "type");
        }

        if (obj.ContainsKey("version"))
        {
            var version = Json.RequireObject(obj, "version");

            meta.Version = new JavaVersion(
                Json.EnsureInteger(version, "major"),
                Json.EnsureInteger(version, "minor"),
                Json.EnsureInteger(version, "security"),
                Json.EnsureInteger(version, "build"),
                Json.EnsureString(version, "name"));
        }

        return meta;
    }

    // ================================================================== ordering

    /// <summary>
    /// Newest first by version, then by release date, then by name.
    /// </summary>
    /// <remarks>
    /// The name is compared with the natural comparator rather than ordinally, so "java-11" sorts
    /// before "java-100" the way a person would expect rather than the way a byte comparison would.
    /// </remarks>
    public int CompareTo(JavaMetadata? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byVersion = Version.CompareTo(other.Version);

        if (byVersion != 0)
        {
            return byVersion;
        }

        var byDate = ReleaseTime.CompareTo(other.ReleaseTime);

        return byDate != 0
            ? byDate
            : StringUtils.NaturalCompare(Name, other.Name, CaseSensitivity.CaseInsensitive);
    }

    /// <remarks>
    /// Version and name only. Two builds of the same version from different vendors, or for different
    /// operating systems, compare EQUAL — which is inherited, and worth knowing before using this to
    /// deduplicate a list that spans platforms.
    /// </remarks>
    public bool Equals(JavaMetadata? other)
        => other is not null && Version.Equals(other.Version) && string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as JavaMetadata);

    public override int GetHashCode() => HashCode.Combine(Version, Name);

    public static bool operator <(JavaMetadata? left, JavaMetadata? right)
        => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(JavaMetadata? left, JavaMetadata? right)
        => left is not null && left.CompareTo(right) > 0;

    public static bool operator <=(JavaMetadata? left, JavaMetadata? right) => !(left > right);

    public static bool operator >=(JavaMetadata? left, JavaMetadata? right) => !(left < right);

    public static bool operator ==(JavaMetadata? left, JavaMetadata? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(JavaMetadata? left, JavaMetadata? right) => !(left == right);

    public override string ToString() => $"{Name} ({Vendor} {Version}, {RuntimeOS})";
}
