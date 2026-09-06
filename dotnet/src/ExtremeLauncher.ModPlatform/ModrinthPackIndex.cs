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
 * Ported from launcher/modplatform/modrinth/ModrinthPackIndex.cpp.
 *
 * MODRINTH'S JSON INTO THE SHARED VOCABULARY. Everything provider-specific stops here.
 *
 * AN UNUSABLE VERSION RETURNS NULL rather than a blank object. Upstream returns a default-constructed
 * IndexedVersion and callers test `fileId.isValid()` -- a heuristic, in upstream's own comment, and
 * one every caller has to remember. A nullable return says the same thing where the compiler can see
 * it. The cases are all upstream's: no game versions, no files, or a chosen file with no URL.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

public static class ModrinthPackIndex
{
    /// <summary>Whether a side marking means the file is wanted there.</summary>
    /// <remarks>
    /// "optional" counts. A mod marked optional on the client still runs on the client; only
    /// "unsupported" means genuinely not wanted.
    /// </remarks>
    public static bool ShouldDownloadOnSide(string side) => side is "required" or "optional";

    /// <summary>Reads one project, as a search hit or a direct fetch describes it.</summary>
    /// <remarks>
    /// The id lives under different keys depending on which: a search result carries "project_id", a
    /// direct project fetch carries "id". Upstream tries the first and falls back, and the fallback is
    /// required rather than optional -- a project with neither is not a project.
    /// </remarks>
    public static void LoadIndexedPack(IndexedPack pack, JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(obj);

        pack.AddonId = Json.EnsureString(obj, "project_id");

        if (pack.AddonId.Length == 0)
        {
            pack.AddonId = Json.RequireString(obj, "id");
        }

        pack.Provider = ResourceProvider.Modrinth;
        pack.Name = Json.RequireString(obj, "title");

        pack.Slug = Json.EnsureString(obj, "slug");

        // No slug means no page to link to. An empty URL is checked for; a half-built one is not.
        pack.WebsiteUrl = pack.Slug.Length != 0 ? "https://modrinth.com/mod/" + pack.Slug : string.Empty;

        pack.Description = Json.EnsureString(obj, "description");

        pack.LogoUrl = Json.EnsureString(obj, "icon_url");

        // The id doubles as the cache key for the icon, so it is stable across renames.
        pack.LogoName = pack.AddonId;

        pack.Authors.Clear();
        pack.Authors.Add(new ModpackAuthor
        {
            Name = Json.EnsureString(obj, "author", "No author(s)"),
            Url = ModrinthApi.GetAuthorUrl(Json.EnsureString(obj, "author", "No author(s)")),
        });

        var client = ShouldDownloadOnSide(Json.EnsureString(obj, "client_side"));
        var server = ShouldDownloadOnSide(Json.EnsureString(obj, "server_side"));

        /*
         * Unsupported on BOTH sides leaves the side unset rather than writing "none". Upstream has no
         * else branch, and a pack that claims neither side is a data error on Modrinth's end, not
         * something to invent a value for.
         */
        if (server && client)
        {
            pack.Side = "both";
        }
        else if (server)
        {
            pack.Side = "server";
        }
        else if (client)
        {
            pack.Side = "client";
        }

        // Modrinth has more to say than a search result carries, so it is worth going back for.
        pack.ExtraDataLoaded = false;
    }

    /// <summary>Reads the long-form project details a search result does not carry.</summary>
    public static void LoadExtraPackData(IndexedPack pack, JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(obj);

        // Trailing slashes are trimmed so the links read cleanly; Modrinth is inconsistent about them.
        pack.ExtraData.IssuesUrl = TrimTrailingSlash(Json.EnsureString(obj, "issues_url"));
        pack.ExtraData.SourceUrl = TrimTrailingSlash(Json.EnsureString(obj, "source_url"));
        pack.ExtraData.WikiUrl = TrimTrailingSlash(Json.EnsureString(obj, "wiki_url"));
        pack.ExtraData.DiscordUrl = TrimTrailingSlash(Json.EnsureString(obj, "discord_url"));

        pack.ExtraData.Donate.Clear();

        foreach (var element in Json.EnsureArray(obj["donation_urls"]))
        {
            var donation = Json.RequireObject(element);

            pack.ExtraData.Donate.Add(new DonationData
            {
                Id = Json.EnsureString(donation, "id"),
                Platform = Json.EnsureString(donation, "platform"),
                Url = Json.EnsureString(donation, "url"),
            });
        }

        pack.ExtraData.Status = Json.EnsureString(obj, "status");

        /*
         * The body is Markdown, and <br> in Markdown renders as a literal tag in anything that does
         * not also allow HTML. Upstream strips every one; nothing else is stripped, so other HTML
         * still comes through.
         */
        pack.ExtraData.Body = Json.EnsureString(obj, "body").Replace("<br>", string.Empty, StringComparison.Ordinal);

        pack.ExtraDataLoaded = true;
    }

    private static string TrimTrailingSlash(string url)
        => url.EndsWith('/') ? url[..^1] : url;

    /// <summary>Reads a project's versions, newest first.</summary>
    public static void LoadIndexedPackVersions(IndexedPack pack, JsonArray array)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(array);

        pack.Versions.Clear();
        pack.Versions.AddRange(SortNewestFirst(ParseAll(array)));
        pack.VersionsLoaded = true;
    }

    /// <summary>
    /// Picks the newest version of a dependency that fits the loaders in play.
    /// </summary>
    /// <param name="loaders">
    /// The loaders the instance supports, or null when that is unknown.
    /// </param>
    /// <returns>Null when nothing fits, where upstream returns a blank version.</returns>
    /// <remarks>
    /// A version declaring NO loaders passes the filter. That is upstream's <c>!file.loaders</c> and
    /// it is right: a resource pack or a data pack has no loader, and excluding those would make every
    /// non-mod dependency unresolvable.
    /// </remarks>
    /// <remarks>
    /// Takes the loaders rather than an instance. Upstream reads them off a MinecraftInstance and also
    /// reads the Minecraft version from it -- into a variable it never uses. Passing what is actually
    /// needed leaves nothing to be unused.
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

    /*
     * Dates are RFC 3339, which sorts chronologically as text -- that is the whole point of the
     * format, and it is why upstream compares the strings rather than parsing them.
     *
     * OrderByDescending rather than std::sort: LINQ's sort is stable, so versions published in the
     * same second keep the order Modrinth sent them in. Upstream's std::sort may reorder them on any
     * given run, which makes "the newest version" a coin flip for a project that published several at
     * once. Deterministic is strictly better here and costs nothing.
     */
    private static IEnumerable<IndexedVersion> SortNewestFirst(IEnumerable<IndexedVersion> versions)
        => versions.OrderByDescending(v => v.Date, StringComparer.Ordinal);

    /// <summary>Reads one version, or null if it is unusable.</summary>
    /// <param name="preferredHashType">Try this hash first; falls back to the provider's own order.</param>
    /// <param name="preferredFileName">
    /// Prefer a file whose name contains this, for picking the same file a user already has.
    /// </param>
    public static IndexedVersion? LoadIndexedPackVersion(
        JsonObject obj,
        string preferredHashType = "sha512",
        string preferredFileName = "")
    {
        ArgumentNullException.ThrowIfNull(obj);

        var file = new IndexedVersion
        {
            AddonId = Json.RequireString(obj, "project_id"),
            FileId = Json.RequireString(obj, "id"),
            Date = Json.RequireString(obj, "date_published"),
        };

        var versions = Json.RequireArray(obj, "game_versions");

        // A version supporting no Minecraft version cannot be installed anywhere.
        if (versions.Count == 0)
        {
            return null;
        }

        foreach (var version in versions)
        {
            file.McVersion.Add(Json.EnsureString(version));
        }

        // Unrecognised loader names are ignored rather than refused: Modrinth adds new ones over time.
        foreach (var loader in Json.RequireArray(obj, "loaders"))
        {
            file.Loaders |= ModIndex.ModLoaderFromString(Json.EnsureString(loader));
        }

        file.Version = Json.RequireString(obj, "name");
        file.VersionNumber = Json.RequireString(obj, "version_number");
        file.VersionType = ModIndex.VersionTypeFromString(Json.RequireString(obj, "version_type"));
        file.Changelog = Json.RequireString(obj, "changelog");

        foreach (var element in Json.EnsureArray(obj["dependencies"]))
        {
            var dependency = Json.EnsureObject(element);

            file.Dependencies.Add(new Dependency
            {
                AddonId = Json.EnsureString(dependency, "project_id"),
                Version = Json.EnsureString(dependency, "version_id"),
                Type = DependencyTypeFromString(Json.RequireString(dependency, "dependency_type")),
            });
        }

        var files = Json.RequireArray(obj, "files");

        // Modrinth should never send a version with no files, but it has to be survivable.
        if (files.Count == 0)
        {
            return null;
        }

        var chosen = ChooseFile(files, preferredFileName);

        if (chosen["url"] is null)
        {
            return null;
        }

        file.DownloadUrl = Json.RequireString(chosen, "url");
        file.FileName = FileSystem.RemoveInvalidPathChars(Json.RequireString(chosen, "filename"));

        /*
         * UPSTREAM BUG, reproduced. A file chosen because its name matched preferredFileName sets
         * is_preferred inside the selection loop, and this line then overwrites it unconditionally --
         * so that assignment is dead and the preference is lost. Reproduced rather than fixed because
         * "preferred" here means "this is the file Modrinth considers canonical", which is what every
         * caller reads it as; making a name match set it too would change what the flag means. Noted
         * in PORTING.md.
         */
        file.IsPreferred = Json.RequireBoolean(chosen, "primary") || files.Count == 1;

        var hashes = Json.RequireObject(chosen, "hashes");

        if (hashes[preferredHashType] is not null)
        {
            file.Hash = Json.RequireString(hashes, preferredHashType);
            file.HashType = preferredHashType;
        }
        else
        {
            // Best first, so a file with several hashes reports the strongest one available.
            foreach (var hashType in ProviderCapabilities.HashTypes(ResourceProvider.Modrinth))
            {
                if (hashes[hashType] is not null)
                {
                    file.Hash = Json.RequireString(hashes, hashType);
                    file.HashType = hashType;

                    break;
                }
            }
        }

        return file;
    }

    /// <summary>
    /// Picks which file of a multi-file version to install.
    /// </summary>
    /// <remarks>
    /// A name match wins, then the primary flag, and failing both the LAST file — upstream's loop
    /// stops one short of the end and never tests it, so it is the default by falling out rather than
    /// by being chosen. Preserved: Modrinth requires a primary in practice, so the default is close to
    /// unreachable, and changing it would silently install a different file for anyone it does reach.
    /// </remarks>
    private static JsonObject ChooseFile(JsonArray files, string preferredFileName)
    {
        var index = 0;

        while (index < files.Count - 1)
        {
            var candidate = Json.RequireObject(files[index]);
            var fileName = Json.RequireString(candidate, "filename");

            if (preferredFileName.Length != 0 && fileName.Contains(preferredFileName, StringComparison.Ordinal))
            {
                break;
            }

            if (Json.RequireBoolean(candidate, "primary"))
            {
                break;
            }

            index++;
        }

        return Json.RequireObject(files[index]);
    }

    /// <summary>Modrinth's dependency kinds. It has no tool or include kind.</summary>
    private static DependencyType DependencyTypeFromString(string type)
        => type switch
        {
            "required" => DependencyType.Required,
            "optional" => DependencyType.Optional,
            "incompatible" => DependencyType.Incompatible,
            "embedded" => DependencyType.Embedded,
            _ => DependencyType.Unknown,
        };
}
