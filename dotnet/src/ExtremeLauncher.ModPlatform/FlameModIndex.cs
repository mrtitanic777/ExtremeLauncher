// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2022 flowln <flowlnlnln@gmail.com>
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
 * Ported from launcher/modplatform/flame/FlameModIndex.cpp.
 *
 * "gameVersions" IS THREE FIELDS IN A TRENCH COAT. CurseForge does not separate what a file targets:
 * one flat array of strings holds Minecraft versions, loader names, and the sides the file belongs on,
 * all mixed together --
 *
 *     ["1.20.1", "Forge", "Client", "1.20"]
 *
 * -- and it is on the reader to tell them apart. Upstream does it by shape: anything containing a dot
 * is a Minecraft version, anything matching a loader name is a loader, and "client"/"server" are
 * sides. That heuristic is the single most consequential line in this file, because getting it wrong
 * does not error -- it just quietly offers Forge files to a Fabric instance.
 *
 * AN EMPTY DOWNLOAD URL IS NORMAL HERE, unlike Modrinth. CurseForge omits it for projects whose
 * authors forbid third-party downloads, so it is read with Ensure rather than Require and the file is
 * still a valid version. What to do about it is the installer's problem, not the parser's.
 */

using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

public static class FlameModIndex
{
    /// <summary>Reads one project.</summary>
    public static void LoadIndexedPack(IndexedPack pack, JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(obj);

        pack.AddonId = Json.RequireInteger(obj, "id").ToString(CultureInfo.InvariantCulture);
        pack.Provider = ResourceProvider.Flame;
        pack.Name = Json.RequireString(obj, "name");
        pack.Slug = Json.RequireString(obj, "slug");

        pack.WebsiteUrl = Json.EnsureString(Json.EnsureObject(obj["links"]), "websiteUrl");
        pack.Description = Json.EnsureString(obj, "summary");

        var logo = Json.EnsureObject(obj["logo"]);

        pack.LogoName = Json.EnsureString(logo, "title");

        // The thumbnail if there is one, the full image otherwise: this feeds a list, not a viewer.
        pack.LogoUrl = Json.EnsureString(logo, "thumbnailUrl");

        if (pack.LogoUrl.Length == 0)
        {
            pack.LogoUrl = Json.EnsureString(logo, "url");
        }

        pack.Authors.Clear();

        foreach (var element in Json.EnsureArray(obj["authors"]))
        {
            var author = Json.RequireObject(element);

            pack.Authors.Add(new ModpackAuthor
            {
                Name = Json.RequireString(author, "name"),
                Url = Json.RequireString(author, "url"),
            });
        }

        pack.ExtraDataLoaded = false;

        LoadUrls(pack, obj);
    }

    /// <summary>
    /// Reads the project's links, which arrive with the project itself.
    /// </summary>
    /// <remarks>
    /// THE TWO HALVES OF THE EXTRA DATA CHECK EACH OTHER. The description is a separate request, so
    /// this half only declares the extra data complete once a body has already arrived, and
    /// <see cref="LoadBody"/> only does so once a link has. A project with neither stays incomplete
    /// forever, which is upstream's behaviour and harmless -- there is nothing more to show.
    /// </remarks>
    public static void LoadUrls(IndexedPack pack, JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(obj);

        var links = Json.EnsureObject(obj["links"]);

        pack.ExtraData.IssuesUrl = TrimTrailingSlash(Json.EnsureString(links, "issuesUrl"));
        pack.ExtraData.SourceUrl = TrimTrailingSlash(Json.EnsureString(links, "sourceUrl"));
        pack.ExtraData.WikiUrl = TrimTrailingSlash(Json.EnsureString(links, "wikiUrl"));

        if (pack.ExtraData.Body.Length != 0)
        {
            pack.ExtraDataLoaded = true;
        }
    }

    /// <summary>
    /// Records the long description, which CurseForge only serves from its own endpoint.
    /// </summary>
    /// <remarks>
    /// Takes the body rather than fetching it. Upstream calls the API from inside the parser through a
    /// file-static <c>FlameAPI</c>, which makes parsing a network operation and the parser untestable
    /// without one.
    /// </remarks>
    public static void LoadBody(IndexedPack pack, string body)
    {
        ArgumentNullException.ThrowIfNull(pack);

        pack.ExtraData.Body = body;

        if (pack.ExtraData.IssuesUrl.Length != 0
            || pack.ExtraData.SourceUrl.Length != 0
            || pack.ExtraData.WikiUrl.Length != 0)
        {
            pack.ExtraDataLoaded = true;
        }
    }

    private static string TrimTrailingSlash(string url)
        => url.EndsWith('/') ? url[..^1] : url;

    /// <summary>Reads a project's files, newest first.</summary>
    public static void LoadIndexedPackVersions(IndexedPack pack, JsonArray array)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(array);

        pack.Versions.Clear();
        pack.Versions.AddRange(SortNewestFirst(ParseAll(array)));
        pack.VersionsLoaded = true;
    }

    /// <summary>Picks the newest file of a dependency that fits the loaders in play.</summary>
    /// <remarks>
    /// A file declaring no loaders fits anything, as on Modrinth: resource packs and data packs have
    /// none.
    /// </remarks>
    public static IndexedVersion? LoadDependencyVersions(JsonArray array, ModLoaderTypes? loaders)
    {
        ArgumentNullException.ThrowIfNull(array);

        var matching = ParseAll(array)
            .Where(v => loaders is null || v.Loaders == ModLoaderTypes.None || (loaders.Value & v.Loaders) != 0);

        return SortNewestFirst(matching).FirstOrDefault();
    }

    private static IEnumerable<IndexedVersion> ParseAll(JsonArray array)
        => array
            .Select(element => element as JsonObject)
            .Where(obj => obj is not null)
            .Select(obj => LoadIndexedPackVersion(obj!))
            .Where(version => version is not null)
            .Select(version => version!);

    /// <remarks>
    /// Stable, unlike upstream's std::sort, so files sharing a timestamp keep CurseForge's order
    /// instead of being reshuffled differently on each run. Same reasoning as the Modrinth parser.
    /// </remarks>
    private static IEnumerable<IndexedVersion> SortNewestFirst(IEnumerable<IndexedVersion> versions)
        => versions.OrderByDescending(v => v.Date, StringComparer.Ordinal);

    /// <summary>Reads one file, or null if it targets no Minecraft version at all.</summary>
    /// <remarks>
    /// The changelog is not read here. CurseForge serves it from a separate endpoint, and upstream
    /// fetches it mid-parse behind a flag; a caller that wants one asks the API and assigns it.
    /// </remarks>
    public static IndexedVersion? LoadIndexedPackVersion(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var gameVersions = Json.RequireArray(obj, "gameVersions");

        // Checked before anything else is read, exactly as upstream orders it.
        if (gameVersions.Count == 0)
        {
            return null;
        }

        var file = new IndexedVersion();

        foreach (var element in gameVersions)
        {
            var value = Json.EnsureString(element);

            /*
             * A DOT MEANS A MINECRAFT VERSION. Crude, and it holds: every Minecraft version has one
             * and no loader or side name does. Note this is a separate test rather than an else, so
             * upstream would classify a dotted string as both -- which nothing in CurseForge's
             * vocabulary currently is.
             */
            if (value.Contains('.', StringComparison.Ordinal))
            {
                file.McVersion.Add(value);
            }

            // Case-insensitively: CurseForge writes "Forge" and "Fabric" capitalised.
            var lowered = value.ToLowerInvariant();

            file.Loaders |= ModIndex.ModLoaderFromString(lowered);

            if (lowered is "client" or "server")
            {
                /*
                 * Sides accumulate rather than overwrite: a file listing both ends up "both". The
                 * first one seen wins the empty slot, and a second, different one promotes it.
                 */
                if (file.Side.Length == 0)
                {
                    file.Side = lowered;
                }
                else if (file.Side != lowered)
                {
                    file.Side = "both";
                }
            }
        }

        file.AddonId = Json.RequireInteger(obj, "modId").ToString(CultureInfo.InvariantCulture);
        file.FileId = Json.RequireInteger(obj, "id").ToString(CultureInfo.InvariantCulture);
        file.Date = Json.RequireString(obj, "fileDate");
        file.Version = Json.RequireString(obj, "displayName");

        // Ensure, not Require: CurseForge omits it when the author forbids third-party downloads.
        file.DownloadUrl = Json.EnsureString(obj, "downloadUrl");

        file.FileName = FileSystem.RemoveInvalidPathChars(Json.RequireString(obj, "fileName"));

        file.VersionType = Json.RequireInteger(obj, "releaseType") switch
        {
            1 => VersionType.Release,
            2 => VersionType.Beta,
            3 => VersionType.Alpha,
            _ => VersionType.Unknown,
        };

        LoadHash(file, obj);

        foreach (var element in Json.EnsureArray(obj["dependencies"]))
        {
            var dependency = Json.EnsureObject(element);

            file.Dependencies.Add(new Dependency
            {
                AddonId = Json.RequireInteger(dependency, "modId").ToString(CultureInfo.InvariantCulture),
                Type = DependencyTypeFromRelation(Json.RequireInteger(dependency, "relationType")),
            });
        }

        return file;
    }

    /// <summary>
    /// Takes the first hash CurseForge lists that this launcher understands.
    /// </summary>
    /// <remarks>
    /// FIRST, not best -- unlike the Modrinth parser, which walks its provider's list in preference
    /// order. Here the ARRAY's order decides, so a file listing md5 before sha1 is verified by md5.
    /// Upstream's behaviour, kept: both are in CurseForge's capability list, so both are accepted, and
    /// reordering would change which hash a download is checked against.
    /// </remarks>
    private static void LoadHash(IndexedVersion file, JsonObject obj)
    {
        var known = ProviderCapabilities.HashTypes(ResourceProvider.Flame);

        foreach (var element in Json.EnsureArray(obj["hashes"]))
        {
            var entry = Json.EnsureObject(element);
            var algorithm = HashAlgorithmFromId(Json.EnsureInteger(entry["algo"], 1));

            if (known.Contains(algorithm))
            {
                file.Hash = Json.RequireString(entry, "value");
                file.HashType = algorithm;

                return;
            }
        }
    }

    /// <summary>
    /// CurseForge's numeric hash algorithms.
    /// </summary>
    /// <remarks>
    /// CARRIED-OVER HAZARD. Upstream's <c>default:</c> shares its case with 1, so an algorithm id
    /// CurseForge has not documented yet is reported as "sha1" -- a hash labelled with an algorithm
    /// that did not produce it, which fails verification with a message blaming the file. Kept because
    /// changing it would reject files that work today; the alternative is a third value the rest of
    /// the launcher has no handling for.
    /// </remarks>
    public static string HashAlgorithmFromId(int algorithm)
        => algorithm switch
        {
            2 => "md5",
            _ => "sha1",
        };

    /// <summary>CurseForge's numeric relation types, from their FileRelationType enum.</summary>
    public static DependencyType DependencyTypeFromRelation(int relationType)
        => relationType switch
        {
            1 => DependencyType.Embedded,
            2 => DependencyType.Optional,
            3 => DependencyType.Required,
            4 => DependencyType.Tool,
            5 => DependencyType.Incompatible,
            6 => DependencyType.Include,
            _ => DependencyType.Unknown,
        };
}
