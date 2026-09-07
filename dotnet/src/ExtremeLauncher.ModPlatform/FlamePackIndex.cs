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
 * Ported from launcher/modplatform/flame/FlamePackIndex.cpp -- the CurseForge MODPACK index, the
 * browser's search results and their versions. Distinct from FlameModIndex (individual mods) and
 * FlamePackManifest (the manifest.json inside a downloaded pack): this reads the listing CurseForge
 * returns for a modpack search, and the file list for one pack. It fills the shared IndexedPack /
 * IndexedVersion, as the other providers' parsers do.
 *
 * Two Flame-specific quirks are kept: a listing with no usable default file is rejected outright (a
 * pack whose main file targets no Minecraft version is not worth showing), and the logo filename is
 * built from the pack slug plus the logo URL's extension rather than taken from the payload.
 */

using System.Globalization;
using System.Text.Json.Nodes;

using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>Reads the CurseForge modpack index.</summary>
public static class FlamePackIndex
{
    /// <summary>
    /// Reads one modpack listing into <paramref name="pack"/>, then its links.
    /// </summary>
    /// <exception cref="JsonException">
    /// The pack's main file is missing or targets no Minecraft version — upstream skips such a pack
    /// rather than list something that cannot be installed.
    /// </exception>
    public static void LoadIndexedPack(IndexedPack pack, JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(obj);

        pack.AddonId = Json.RequireInteger(obj, "id").ToString(CultureInfo.InvariantCulture);
        pack.Provider = ResourceProvider.Flame;
        pack.Name = Json.RequireString(obj, "name");
        pack.Slug = Json.EnsureString(obj, "slug");
        pack.Description = Json.EnsureString(obj, "summary");

        var logo = Json.RequireObject(obj, "logo");
        pack.LogoUrl = Json.RequireString(logo, "thumbnailUrl");
        pack.LogoName = Json.RequireString(obj, "slug") + "." + SuffixFromUrl(pack.LogoUrl);

        pack.Authors.Clear();

        foreach (var element in Json.RequireArray(obj, "authors"))
        {
            var author = Json.RequireObject(element);

            pack.Authors.Add(new ModpackAuthor
            {
                Name = Json.RequireString(author, "name"),
                Url = Json.RequireString(author, "url"),
            });
        }

        var defaultFileId = Json.RequireInteger(obj, "mainFileId");

        // The pack is only worth listing if its default file exists and targets a Minecraft version.
        var found = false;

        foreach (var element in Json.RequireArray(obj, "latestFiles"))
        {
            var file = Json.RequireObject(element);

            if (Json.RequireInteger(file, "id") != defaultFileId)
            {
                continue;
            }

            if (Json.RequireArray(file, "gameVersions").Count >= 1)
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            throw new JsonException($"Pack with no good file, skipping: {pack.Name}");
        }

        LoadIndexedInfo(pack, obj);
    }

    /// <summary>
    /// Reads the pack's links (website, issues, source, wiki), which arrive with the listing. Each has
    /// a trailing slash trimmed, and the extra data is marked loaded once done.
    /// </summary>
    public static void LoadIndexedInfo(IndexedPack pack, JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(obj);

        var links = Json.EnsureObject(obj["links"]);

        pack.WebsiteUrl = TrimTrailingSlash(Json.EnsureString(links, "websiteUrl"));
        pack.ExtraData.IssuesUrl = TrimTrailingSlash(Json.EnsureString(links, "issuesUrl"));
        pack.ExtraData.SourceUrl = TrimTrailingSlash(Json.EnsureString(links, "sourceUrl"));
        pack.ExtraData.WikiUrl = TrimTrailingSlash(Json.EnsureString(links, "wikiUrl"));

        pack.ExtraDataLoaded = true;
    }

    /// <summary>
    /// Reads a pack's file list into <paramref name="pack"/>, newest first. A file is dropped when it
    /// targets no Minecraft version, or when CurseForge withholds its download URL (the author has
    /// forbidden third-party distribution and there is nothing to fetch).
    /// </summary>
    public static void LoadIndexedPackVersions(IndexedPack pack, JsonArray array)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(array);

        var versions = new List<IndexedVersion>();

        foreach (var element in array)
        {
            if (element is not JsonObject version || ParseVersion(pack.AddonId, version) is not { } file)
            {
                continue;
            }

            versions.Add(file);
        }

        // Newest first, by file id — CurseForge's ids increase over time, so the largest is the latest.
        pack.Versions.Clear();
        pack.Versions.AddRange(versions.OrderByDescending(FileIdValue));
        pack.VersionsLoaded = true;
    }

    private static IndexedVersion? ParseVersion(string addonId, JsonObject version)
    {
        var gameVersions = Json.RequireArray(version, "gameVersions");

        if (gameVersions.Count < 1)
        {
            return null;
        }

        var file = new IndexedVersion
        {
            AddonId = addonId,
            FileId = Json.RequireInteger(version, "id").ToString(CultureInfo.InvariantCulture),
        };

        foreach (var element in gameVersions)
        {
            var value = Json.EnsureString(element);

            // A dot means a Minecraft version; anything else is a loader name.
            if (value.Contains('.', StringComparison.Ordinal))
            {
                file.McVersion.Add(value);
            }

            file.Loaders |= ModIndex.ModLoaderFromString(value.ToLowerInvariant());
        }

        file.Version = Json.RequireString(version, "displayName");

        file.VersionType = Json.RequireInteger(version, "releaseType") switch
        {
            1 => VersionType.Release,
            2 => VersionType.Beta,
            3 => VersionType.Alpha,
            _ => VersionType.Unknown,
        };

        // Ensure, not Require: CurseForge omits it when third-party distribution is off.
        file.DownloadUrl = Json.EnsureString(version, "downloadUrl");

        // Only usable with a download URL; upstream drops the rest.
        return file.DownloadUrl.Length != 0 ? file : null;
    }

    private static long FileIdValue(IndexedVersion version)
        => long.TryParse(version.FileId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string TrimTrailingSlash(string url) => url.EndsWith('/') ? url[..^1] : url;

    /// <summary>
    /// The file extension of a URL's last path segment — the part QFileInfo::suffix returns: the text
    /// after the last dot of the filename, empty when there is none.
    /// </summary>
    private static string SuffixFromUrl(string url)
    {
        // Drop the query and fragment, then take the last path segment.
        var path = url;

        var cut = path.IndexOfAny(['?', '#']);

        if (cut >= 0)
        {
            path = path[..cut];
        }

        var lastSlash = path.LastIndexOf('/');

        if (lastSlash >= 0)
        {
            path = path[(lastSlash + 1)..];
        }

        var lastDot = path.LastIndexOf('.');

        return lastDot >= 0 ? path[(lastDot + 1)..] : string.Empty;
    }
}
