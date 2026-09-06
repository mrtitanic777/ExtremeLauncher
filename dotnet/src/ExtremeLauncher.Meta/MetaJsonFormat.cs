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
 * Ported from launcher/meta/JsonFormat.{h,cpp}.
 *
 * Parses the three document shapes the meta server serves:
 *   index         -- { "packages": [ { uid, name, sha256 } ] }
 *   version list  -- { "uid", "name", "versions": [ … ] }
 *   version       -- one version's full patch contents
 *
 * Every document carries a "formatVersion". An unrecognised one is a hard error rather than a
 * best-effort parse: metadata drives what gets downloaded and executed, so guessing is not safe.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.Meta;

public static class MetaJsonFormat
{
    /// <summary>Forwards to <see cref="MetadataFormat"/>, which owns the shared metadata plumbing.</summary>
    public static MetadataVersion ParseFormatVersion(JsonObject obj, bool required = true)
        => MetadataFormat.ParseFormatVersion(obj, required);

    public static void SerializeFormatVersion(JsonObject obj, MetadataVersion version)
        => MetadataFormat.SerializeFormatVersion(obj, version);

    public static RequireSet ParseRequires(JsonObject obj, string key) => MetadataFormat.ParseRequires(obj, key);

    public static void SerializeRequires(JsonObject obj, RequireSet? requires, string key)
        => MetadataFormat.SerializeRequires(obj, requires, key);

    // ================================================================== index

    public static void ParseIndex(JsonObject obj, Index target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ParseFormatVersion(obj) == MetadataVersion.Invalid)
        {
            throw new ParseException("Unknown format version!");
        }

        target.Merge(ParseIndexInternal(obj));
    }

    private static Index ParseIndexInternal(JsonObject obj)
    {
        var lists = new List<VersionList>();

        foreach (var element in Json.RequireArray(obj, "packages"))
        {
            var package = element as JsonObject ?? throw new ParseException("package entry is not an object");

            lists.Add(new VersionList(Json.RequireString(package, "uid"))
            {
                Name = Json.EnsureString(package, "name"),
                Sha256 = Json.EnsureString(package, "sha256"),
            });
        }

        return new Index(lists);
    }

    // ================================================================== version list

    public static void ParseVersionList(JsonObject obj, VersionList target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ParseFormatVersion(obj) == MetadataVersion.Invalid)
        {
            throw new ParseException("Unknown format version!");
        }

        target.Merge(ParseVersionListInternal(obj));
    }

    private static VersionList ParseVersionListInternal(JsonObject obj)
    {
        var uid = Json.RequireString(obj, "uid");
        var versions = new List<MetaVersion>();

        foreach (var element in Json.RequireArray(obj, "versions"))
        {
            var versionObject = element as JsonObject ?? throw new ParseException("version entry is not an object");

            var version = ParseCommonVersion(uid, versionObject);

            // Only a list is entitled to say what is recommended; see MetaVersion.MergeFromList.
            version.ProvidesRecommendations = true;

            versions.Add(version);
        }

        var result = new VersionList(uid) { Name = Json.EnsureString(obj, "name") };
        result.SetVersions(versions);

        return result;
    }

    // ================================================================== version

    public static void ParseVersion(JsonObject obj, MetaVersion target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ParseFormatVersion(obj) == MetadataVersion.Invalid)
        {
            throw new ParseException("Unknown format version!");
        }

        target.Merge(ParseVersionInternal(obj));
    }

    private static MetaVersion ParseVersionInternal(JsonObject obj)
    {
        var version = ParseCommonVersion(Json.RequireString(obj, "uid"), obj);

        // A standalone version document carries the whole patch inline, so parse it here.
        version.Data = OneSixVersionFormat.VersionFileFromJson(
            obj,
            $"{version.Uid}/{version.VersionString}.json",
            requireOrder: obj.ContainsKey("order"));

        return version;
    }

    /// <summary>The fields shared by a version-list entry and a standalone version document.</summary>
    private static MetaVersion ParseCommonVersion(string uid, JsonObject obj)
    {
        var version = new MetaVersion(uid, Json.RequireString(obj, "version"))
        {
            Type = Json.EnsureString(obj, "type"),
            IsRecommended = Json.EnsureBoolean(obj, "recommended"),
            IsVolatile = Json.EnsureBoolean(obj, "volatile"),
        };

        // Stored as Unix seconds; the wire format is ISO 8601.
        if (ParseUtils.TryTimeFromS3Time(Json.RequireString(obj, "releaseTime"), out var releaseTime))
        {
            version.RawTime = releaseTime.ToUnixTimeSeconds();
        }

        version.SetRequires(ParseRequires(obj, "requires"), ParseRequires(obj, "conflicts"));

        if (Json.EnsureString(obj, "sha256") is { Length: > 0 } sha256)
        {
            version.Sha256 = sha256;
        }

        return version;
    }
}
