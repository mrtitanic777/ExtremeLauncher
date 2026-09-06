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
 * Ported from launcher/modplatform/modrinth/ModrinthAPI.{h,cpp}.
 *
 * ALMOST ALL OF THIS IS URL CONSTRUCTION, and it is where the bugs live. Modrinth filters searches
 * with "facets": a JSON-ish array of arrays, inside a query string, where the outer level is AND and
 * each inner level is OR. Building it is string concatenation with quoting rules, no request goes out
 * to check it, and a malformed facet does not error -- it returns an empty page, which reads exactly
 * like "no mods match". So the builders are separated from the fetching and tested directly.
 */

using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;

// Core.Version is the Minecraft-version comparator, not System.Version.
using Version = ExtremeLauncher.Core.Version;

namespace ExtremeLauncher.ModPlatform;

public sealed class ModrinthApi : ResourceApi
{
    /// <summary>The API root, matching BuildConfig.MODRINTH_PROD_URL.</summary>
    public const string BaseUrl = "https://api.modrinth.com/v2";

    /// <summary>One page of search results, as upstream hardcodes it.</summary>
    public const int SearchPageSize = 25;

    private readonly HttpClient? _client;

    /// <summary>Builds URLs only. Enough for everything except actually fetching.</summary>
    public ModrinthApi()
    {
    }

    public ModrinthApi(HttpClient client) => _client = client;

    public override string DebugName => "Modrinth";

    /// <summary>
    /// The orderings Modrinth offers.
    /// </summary>
    /// <remarks>
    /// The names go into the request as <c>index=</c>; the numbers only fix the display order, since
    /// Modrinth sorts by name rather than by index. See the searchProjects operation in Modrinth's API
    /// spec.
    /// </remarks>
    public override IReadOnlyList<SortingMethod> SortingMethods { get; } =
    [
        new(1, "relevance", "Sort by Relevance"),
        new(2, "downloads", "Sort by Downloads"),
        new(3, "follows", "Sort by Follows"),
        new(4, "newest", "Sort by Newest"),
        new(5, "updated", "Sort by Last Updated"),
    ];

    public static string GetAuthorUrl(string name) => "https://modrinth.com/user/" + name;

    /*
     * THE LOADER ORDER IS FIXED AND CAULDRON IS ABSENT. Both are upstream's, and both are right:
     * Modrinth has no Cauldron category, so asking for one would filter every result away. Keeping a
     * declared order rather than iterating the flag bits also means the same request is built from the
     * same arguments every time, which is what makes these testable at all.
     */
    private static readonly ModLoaderTypes[] SupportedLoaders =
    [
        ModLoaderTypes.NeoForge,
        ModLoaderTypes.Forge,
        ModLoaderTypes.Fabric,
        ModLoaderTypes.Quilt,
        ModLoaderTypes.LiteLoader,
    ];

    /// <summary>The loaders in this set that Modrinth knows, in the provider's order.</summary>
    public static IReadOnlyList<string> GetModLoaderStrings(ModLoaderTypes types)
        => [.. SupportedLoaders.Where(l => types.HasFlag(l)).Select(ModIndex.ToString)];

    /// <summary>Whether any requested loader is one Modrinth has a category for.</summary>
    public static bool ValidateModLoaders(ModLoaderTypes loaders)
        => (loaders & (ModLoaderTypes.NeoForge | ModLoaderTypes.Forge | ModLoaderTypes.Fabric
                       | ModLoaderTypes.Quilt | ModLoaderTypes.LiteLoader)) != 0;

    public static string GetModLoaderFilters(ModLoaderTypes types)
        => string.Join(',', GetModLoaderStrings(types).Select(l => $"\"categories:{l}\""));

    public static string GetCategoriesFilters(IEnumerable<string> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        return string.Join(',', categories.Select(c => $"\"categories:{c}\""));
    }

    /// <summary>
    /// The facet for a client/server filter, or an empty string for no filter.
    /// </summary>
    /// <remarks>
    /// "required" and "optional" both count, and that is the point: a mod marked optional on the
    /// client still runs there. Only "unsupported" is excluded. Anything other than "client" or
    /// "server" -- including "both" and any unrecognised value -- filters nothing.
    /// </remarks>
    public static string GetSideFilters(string side)
        => side switch
        {
            "client" => "\"client_side:required\",\"client_side:optional\"",
            "server" => "\"server_side:required\",\"server_side:optional\"",
            _ => string.Empty,
        };

    /// <summary>Modrinth's name for a resource type, or an empty string if it has none.</summary>
    public static string ResourceTypeParameter(ResourceType type)
        => type switch
        {
            ResourceType.Mod => "mod",
            ResourceType.ResourcePack => "resourcepack",
            ResourceType.ShaderPack => "shader",
            ResourceType.Modpack => "modpack",
            _ => string.Empty,
        };

    private static string GetGameVersionsArray(IEnumerable<Version> mcVersions)
        => string.Join(',', mcVersions.Select(v => $"\"versions:{v}\""));

    /// <summary>
    /// The facets for a search: outer array is AND, each inner array is OR.
    /// </summary>
    /// <remarks>
    /// The project type is always appended last, so a search is never unconstrained -- without it a
    /// mod search would return modpacks too.
    /// </remarks>
    public string CreateFacets(SearchArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var facets = new List<string>();

        // Empty is skipped, not sent: an empty OR group would match nothing at all.
        if (args.Loaders is { } loaders && loaders != ModLoaderTypes.None)
        {
            facets.Add($"[{GetModLoaderFilters(loaders)}]");
        }

        if (args.Versions is { Count: > 0 } versions)
        {
            facets.Add($"[{GetGameVersionsArray(versions)}]");
        }

        if (args.Side is { } side && GetSideFilters(side) is { Length: > 0 } sideFilter)
        {
            facets.Add($"[{sideFilter}]");
        }

        if (args.CategoryIds is { Count: > 0 } categories)
        {
            facets.Add($"[{GetCategoriesFilters(categories)}]");
        }

        facets.Add($"[\"project_type:{ResourceTypeParameter(args.Type)}\"]");

        return $"[{string.Join(',', facets)}]";
    }

    /// <summary>
    /// The search URL, or null when no requested loader is one Modrinth supports.
    /// </summary>
    /// <remarks>
    /// Refusing beats searching without the filter. A user who asked for Cauldron mods and got a page
    /// of Fabric ones would have no way to tell the filter was dropped.
    /// </remarks>
    public string? GetSearchUrl(SearchArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Loaders is { } loaders && loaders != ModLoaderTypes.None && !ValidateModLoaders(loaders))
        {
            return null;
        }

        var arguments = new List<string>
        {
            $"offset={args.Offset.ToString(CultureInfo.InvariantCulture)}",
            $"limit={SearchPageSize.ToString(CultureInfo.InvariantCulture)}",
        };

        if (args.Search is { } search)
        {
            arguments.Add($"query={search}");
        }

        if (args.Sorting is { } sorting)
        {
            arguments.Add($"index={sorting.Name}");
        }

        arguments.Add($"facets={CreateFacets(args)}");

        return $"{BaseUrl}/search?{string.Join('&', arguments)}";
    }

    public static string GetInfoUrl(string id) => $"{BaseUrl}/project/{id}";

    public static string GetMultipleModInfoUrl(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return $"{BaseUrl}/projects?ids=[\"{string.Join("\",\"", ids)}\"]";
    }

    /// <summary>The URL for one project's versions, narrowed by whatever the args carry.</summary>
    /// <remarks>
    /// CARRIED-OVER QUIRK. Upstream tests only whether the loaders option is PRESENT here, where the
    /// search URL also tests that it is non-empty. So an explicitly empty loader set produces
    /// <c>loaders=[""]</c> -- a filter matching nothing -- while the same set in a search drops the
    /// filter entirely. Left as upstream has it: which of the two is intended is genuinely unclear,
    /// and callers pass a loader they actually have. Pinned by a test so a later change is a decision.
    /// </remarks>
    public static string GetVersionsUrl(VersionSearchArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var arguments = new List<string>();

        if (args.McVersions is { } mcVersions)
        {
            arguments.Add($"game_versions=[{GetGameVersionsString(mcVersions)}]");
        }

        if (args.Loaders is { } loaders)
        {
            arguments.Add($"loaders=[\"{string.Join("\",\"", GetModLoaderStrings(loaders))}\"]");
        }

        var query = arguments.Count == 0 ? string.Empty : "?" + string.Join('&', arguments);

        return $"{BaseUrl}/project/{args.Pack.AddonId}/version{query}";
    }

    /// <summary>
    /// The URL that resolves a dependency to a file.
    /// </summary>
    /// <remarks>
    /// A dependency naming an exact version is fetched directly; one naming only a project is asked
    /// for its versions, narrowed to the Minecraft version and loader in play.
    /// </remarks>
    public static string GetDependencyUrl(DependencySearchArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Dependency.Version.Length != 0)
        {
            return $"{BaseUrl}/version/{args.Dependency.Version}";
        }

        return $"{BaseUrl}/project/{args.Dependency.AddonId}/version"
            + $"?game_versions=[\"{args.McVersion}\"]"
            + $"&loaders=[\"{string.Join("\",\"", GetModLoaderStrings(args.Loader))}\"]";
    }

    // ================================================================== identifying local files

    /*
     * THE OTHER DIRECTION. Everything above starts from a project id; these start from a file already
     * on disk and ask Modrinth what it is. That is what makes metadata recoverable for a mods folder
     * someone copied in from elsewhere.
     */

    /// <summary>Looks up one file by its hash.</summary>
    public static string GetCurrentVersionUrl(string hash, string hashFormat)
        => $"{BaseUrl}/version_file/{hash}?algorithm={hashFormat}";

    /// <summary>Where a batch of hashes is looked up. A POST, so the body carries the hashes.</summary>
    public static string CurrentVersionsUrl => $"{BaseUrl}/version_files";

    /// <summary>
    /// The batch "what should this file be updated to" endpoint.
    /// </summary>
    /// <remarks>
    /// A DIFFERENT ENDPOINT FROM CurrentVersionsUrl, and the difference is the whole feature:
    /// /version_files says which version a hash IS, /version_files/update says what it should BECOME.
    /// One request answers for every mod in a folder, which is why checking a hundred mods for updates
    /// costs one round trip rather than a hundred.
    /// </remarks>
    public static string LatestVersionsUrl => $"{BaseUrl}/version_files/update";

    /// <summary>The body for a batch hash lookup.</summary>
    /// <remarks>
    /// The algorithm is named alongside the hashes rather than inferred, so every hash in one request
    /// must have been computed the same way.
    /// </remarks>
    public static JsonObject CreateCurrentVersionsBody(IEnumerable<string> hashes, string hashFormat)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        var array = new JsonArray();

        foreach (var hash in hashes)
        {
            array.Add(hash);
        }

        return new JsonObject { ["hashes"] = array, ["algorithm"] = hashFormat };
    }

    /// <summary>
    /// Looks up many files at once, keyed by hash.
    /// </summary>
    /// <remarks>
    /// Modrinth answers with an object keyed by the hashes it recognised, and simply omits the ones it
    /// does not -- so a missing key is "unknown file", not an error.
    /// </remarks>
    public async Task<JsonObject> CurrentVersionsAsync(
        IReadOnlyList<string> hashes,
        string hashFormat,
        CancellationToken cancellationToken = default)
    {
        var body = CreateCurrentVersionsBody(hashes, hashFormat);

        return Json.RequireObject(await PostJsonAsync(CurrentVersionsUrl, body, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The body for a batch update check.
    /// </summary>
    /// <remarks>
    /// The loaders and game versions are the FILTER, not a description of what is installed: without
    /// them Modrinth would happily offer a 1.21 build as the update for a mod in a 1.20.1 instance,
    /// which installs cleanly and then refuses to load.
    /// </remarks>
    public static JsonObject CreateUpdateBody(
        IEnumerable<string> hashes,
        string hashFormat,
        IEnumerable<string> loaders,
        IEnumerable<string> gameVersions)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(loaders);
        ArgumentNullException.ThrowIfNull(gameVersions);

        var hashArray = new JsonArray();

        foreach (var hash in hashes)
        {
            hashArray.Add(hash);
        }

        var loaderArray = new JsonArray();

        foreach (var loader in loaders)
        {
            loaderArray.Add(loader);
        }

        var versionArray = new JsonArray();

        foreach (var version in gameVersions)
        {
            versionArray.Add(version);
        }

        return new JsonObject
        {
            ["hashes"] = hashArray,
            ["algorithm"] = hashFormat,
            ["loaders"] = loaderArray,
            ["game_versions"] = versionArray,
        };
    }

    /// <summary>Asks what each file should be updated to.</summary>
    /// <remarks>
    /// Keyed by the hash asked about, and a hash with nothing newer is simply ABSENT from the answer
    /// rather than present with a null -- so a missing key means "already current", not "unknown".
    /// </remarks>
    public async Task<JsonObject> LatestVersionsAsync(
        IReadOnlyList<string> hashes,
        string hashFormat,
        IReadOnlyList<string> loaders,
        IReadOnlyList<string> gameVersions,
        CancellationToken cancellationToken = default)
    {
        var body = CreateUpdateBody(hashes, hashFormat, loaders, gameVersions);

        return Json.RequireObject(
            await PostJsonAsync(LatestVersionsUrl, body, cancellationToken).ConfigureAwait(false));
    }

    // ================================================================== fetching

    public override Task<JsonNode> SearchProjectsAsync(SearchArgs args, CancellationToken cancellationToken = default)
    {
        var url = GetSearchUrl(args)
            ?? throw new NotSupportedException("Modrinth supports none of the requested mod loaders.");

        return GetJsonAsync(url, cancellationToken);
    }

    public override Task<JsonNode> GetProjectAsync(string addonId, CancellationToken cancellationToken = default)
        => GetJsonAsync(GetInfoUrl(addonId), cancellationToken);

    public override Task<JsonNode> GetProjectsAsync(
        IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken = default)
        => GetJsonAsync(GetMultipleModInfoUrl(addonIds), cancellationToken);

    public override Task<JsonNode> GetProjectInfoAsync(ProjectInfoArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        return GetJsonAsync(GetInfoUrl(args.Pack.AddonId), cancellationToken);
    }

    public override Task<JsonNode> GetProjectVersionsAsync(
        VersionSearchArgs args,
        CancellationToken cancellationToken = default)
        => GetJsonAsync(GetVersionsUrl(args), cancellationToken);

    public override Task<JsonNode> GetDependencyVersionAsync(
        DependencySearchArgs args,
        CancellationToken cancellationToken = default)
        => GetJsonAsync(GetDependencyUrl(args), cancellationToken);

    private async Task<JsonNode> PostJsonAsync(string url, JsonObject body, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException($"{DebugName} was built for URL construction only.");
        }

        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(new Uri(url), content, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        return Json.RequireDocument(payload, url);
    }

    private async Task<JsonNode> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException($"{DebugName} was built for URL construction only.");
        }

        /*
         * Not routed through NetRequest: there is nothing to cache and nothing to validate, and the
         * caller wants the parsed document rather than a file on disk. The pieces NetRequest exists
         * for -- sinks, validators, the metadata cache -- would all be bypassed.
         */
        var download = new Uri(url);

        using var response = await _client.GetAsync(download, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        return Json.RequireDocument(body, url);
    }
}
