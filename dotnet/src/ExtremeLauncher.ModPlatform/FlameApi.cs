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
 * Ported from launcher/modplatform/flame/FlameAPI.{h,cpp}.
 *
 * WHERE CURSEFORGE DIFFERS FROM MODRINTH, since the two sit behind one interface and the differences
 * are the whole reason the interface exists:
 *
 *   - Sorting is by NUMBER, not name. This is why SortingMethod carries both.
 *   - Filters are query parameters rather than facets, so there is no nesting to get wrong -- but
 *     only ONE Minecraft version can be asked for, where Modrinth takes a list. Upstream sends the
 *     first and drops the rest.
 *   - Listing a project's files takes a SINGLE loader (modLoaderType), while searching takes an array
 *     (modLoaderTypes). A request with two loaders selected therefore cannot be narrowed at all, and
 *     upstream omits the parameter rather than picking one.
 *   - Resource types are numeric class ids.
 *   - Several endpoints are POSTs with a JSON body, so the body builders are here beside the URLs.
 *   - Requests need an API key header. Without one every call is a 403.
 */

using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

// Core.Version is the Minecraft-version comparator, not System.Version.
using Version = ExtremeLauncher.Core.Version;

namespace ExtremeLauncher.ModPlatform;

public sealed class FlameApi : ResourceApi
{
    public const string BaseUrl = "https://api.curseforge.com/v1";

    /// <summary>CurseForge's id for Minecraft. Every search is scoped to it.</summary>
    public const int MinecraftGameId = 432;

    public const int SearchPageSize = 25;

    /// <summary>
    /// The page size upstream asks for when listing a project's files.
    /// </summary>
    /// <remarks>
    /// Ten thousand is "all of them" spelled as a number: CurseForge pages this endpoint and upstream
    /// wants the whole list to pick a version from. No project has anywhere near that many files, so
    /// it is effectively "do not paginate".
    /// </remarks>
    public const int AllFilesPageSize = 10000;

    private readonly HttpClient? _client;
    private readonly string _apiKey;

    /// <summary>Builds URLs and request bodies only.</summary>
    public FlameApi() => _apiKey = string.Empty;

    public FlameApi(HttpClient client, string apiKey)
    {
        _client = client;
        _apiKey = apiKey;
    }

    public override string DebugName => "CurseForge";

    /// <summary>
    /// The orderings CurseForge offers.
    /// </summary>
    /// <remarks>
    /// The NUMBERS are what goes into the request as <c>sortField=</c>, unlike Modrinth where the name
    /// does. They come from ModsSearchSortField in CurseForge's API docs, so they are the API's
    /// numbering and not ours to renumber.
    /// </remarks>
    public override IReadOnlyList<SortingMethod> SortingMethods { get; } =
    [
        new(1, "Featured", "Sort by Featured"),
        new(2, "Popularity", "Sort by Popularity"),
        new(3, "LastUpdated", "Sort by Last Updated"),
        new(4, "Name", "Sort by Name"),
        new(5, "Author", "Sort by Author"),
        new(6, "TotalDownloads", "Sort by Downloads"),
        new(7, "Category", "Sort by Category"),
        new(8, "GameVersion", "Sort by Game Version"),
    ];

    /// <summary>
    /// CurseForge's numeric class id for a resource type.
    /// </summary>
    /// <remarks>
    /// Anything unrecognised falls back to mods, matching upstream's <c>default:</c> sharing a case
    /// with MOD. A search that quietly becomes a mod search is upstream's behaviour, and the only
    /// alternative -- refusing -- would break a caller that upstream serves.
    /// </remarks>
    public static int GetClassId(ResourceType type)
        => type switch
        {
            ResourceType.ResourcePack => 12,
            ResourceType.ShaderPack => 6552,
            ResourceType.Modpack => 4471,
            _ => 6,
        };

    /// <summary>
    /// CurseForge's numeric loader id, from ModLoaderType in their docs.
    /// </summary>
    /// <returns>0 for anything without an id, which CurseForge reads as "any".</returns>
    public static int GetMappedModLoader(ModLoaderTypes loader)
        => loader switch
        {
            ModLoaderTypes.Forge => 1,
            ModLoaderTypes.Cauldron => 2,
            ModLoaderTypes.LiteLoader => 3,
            ModLoaderTypes.Fabric => 4,
            ModLoaderTypes.Quilt => 5,
            ModLoaderTypes.NeoForge => 6,
            _ => 0,
        };

    /*
     * NARROWER THAN THE MAPPING ABOVE, and upstream means it. Cauldron and LiteLoader have ids, so
     * they can be named, but neither is offered as a search filter -- they are long dead and
     * CurseForge returns nothing for them. The mapping still needs them because a file already on
     * disk can report one.
     */
    private static readonly ModLoaderTypes[] SearchableLoaders =
    [
        ModLoaderTypes.NeoForge,
        ModLoaderTypes.Forge,
        ModLoaderTypes.Fabric,
        ModLoaderTypes.Quilt,
    ];

    public static IReadOnlyList<string> GetModLoaderStrings(ModLoaderTypes types)
        =>
        [
            .. SearchableLoaders
                .Where(l => types.HasFlag(l))
                .Select(l => GetMappedModLoader(l).ToString(CultureInfo.InvariantCulture)),
        ];

    public static string GetModLoaderFilters(ModLoaderTypes types)
        => "[" + string.Join(',', GetModLoaderStrings(types)) + "]";

    /// <summary>Whether any requested loader is one CurseForge will filter on.</summary>
    /// <remarks>
    /// Declared by upstream but never consulted by its own search builder; see
    /// <see cref="GetSearchUrl"/>.
    /// </remarks>
    public static bool ValidateModLoaders(ModLoaderTypes loaders)
        => (loaders & (ModLoaderTypes.NeoForge | ModLoaderTypes.Forge | ModLoaderTypes.Fabric | ModLoaderTypes.Quilt)) != 0;

    /// <summary>
    /// The search URL.
    /// </summary>
    /// <remarks>
    /// CARRIED-OVER QUIRK. Unlike <see cref="ModrinthApi.GetSearchUrl"/>, this never calls
    /// <see cref="ValidateModLoaders"/>, so a search for only Cauldron or LiteLoader sends
    /// <c>modLoaderTypes=[]</c> — the guard tests that the set is non-zero, which those are. Modrinth
    /// refuses the same search outright. Preserved rather than harmonised: the two providers'
    /// behaviours differ in upstream and a caller may depend on either.
    /// </remarks>
    /// <remarks>
    /// Only the FIRST Minecraft version is sent. CurseForge's search takes one; upstream drops the
    /// rest silently, and there is nowhere better to put them.
    /// </remarks>
    public string? GetSearchUrl(SearchArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var arguments = new List<string>
        {
            $"classId={GetClassId(args.Type).ToString(CultureInfo.InvariantCulture)}",

            // "index" is an offset here, not a sort key. The sort key is sortField.
            $"index={args.Offset.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={SearchPageSize.ToString(CultureInfo.InvariantCulture)}",
        };

        if (args.Search is { } search)
        {
            arguments.Add($"searchFilter={search}");
        }

        if (args.Sorting is { } sorting)
        {
            arguments.Add($"sortField={sorting.Index.ToString(CultureInfo.InvariantCulture)}");
        }

        arguments.Add("sortOrder=desc");

        if (args.Loaders is { } loaders && loaders != ModLoaderTypes.None)
        {
            arguments.Add($"modLoaderTypes={GetModLoaderFilters(loaders)}");
        }

        if (args.CategoryIds is { Count: > 0 } categories)
        {
            arguments.Add($"categoryIds=[{string.Join(',', categories)}]");
        }

        if (args.Versions is { Count: > 0 } versions)
        {
            arguments.Add($"gameVersion={versions[0]}");
        }

        return $"{BaseUrl}/mods/search?gameId={MinecraftGameId.ToString(CultureInfo.InvariantCulture)}&"
            + string.Join('&', arguments);
    }

    public static string GetInfoUrl(string id) => $"{BaseUrl}/mods/{id}";

    /// <summary>One file of one project.</summary>
    public static string GetFileUrl(string addonId, string fileId) => $"{BaseUrl}/mods/{addonId}/files/{fileId}";

    /// <summary>
    /// A project's files, narrowed by whatever can be expressed as a single value.
    /// </summary>
    /// <remarks>
    /// The loader parameter is SINGULAR here, so it is only sent when exactly one loader is selected.
    /// With two, upstream sends nothing rather than choosing — which is right: guessing would hide
    /// files the caller asked to see.
    /// </remarks>
    public static string GetVersionsUrl(VersionSearchArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var url = $"{BaseUrl}/mods/{args.Pack.AddonId}/files"
            + $"?pageSize={AllFilesPageSize.ToString(CultureInfo.InvariantCulture)}";

        // Note the lack of an emptiness check, matching upstream: an empty list would throw here.
        if (args.McVersions is { } mcVersions)
        {
            url += $"&gameVersion={mcVersions[0]}";
        }

        if (args.Loaders is { } loaders && ModIndex.HasSingleModLoader(loaders))
        {
            url += $"&modLoaderType={GetMappedModLoader(loaders).ToString(CultureInfo.InvariantCulture)}";
        }

        return url;
    }

    /// <summary>Resolves a dependency by listing the project's files for one version and loader.</summary>
    /// <remarks>
    /// No shortcut for a dependency pinned to an exact version, unlike Modrinth: CurseForge dependency
    /// entries name a project, never a file.
    /// </remarks>
    public static string GetDependencyUrl(DependencySearchArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var url = $"{BaseUrl}/mods/{args.Dependency.AddonId}/files"
            + $"?pageSize={AllFilesPageSize.ToString(CultureInfo.InvariantCulture)}"
            + $"&gameVersion={args.McVersion}";

        if (args.Loader != ModLoaderTypes.None && ModIndex.HasSingleModLoader(args.Loader))
        {
            url += $"&modLoaderType={GetMappedModLoader(args.Loader).ToString(CultureInfo.InvariantCulture)}";
        }

        return url;
    }

    /// <summary>The categories offered for one resource type.</summary>
    public static string GetCategoriesUrl(ResourceType type)
        => $"{BaseUrl}/categories?gameId={MinecraftGameId.ToString(CultureInfo.InvariantCulture)}"
            + $"&classId={GetClassId(type).ToString(CultureInfo.InvariantCulture)}";

    // ================================================================== request bodies

    /*
     * IDS GO OVER THE WIRE AS JSON STRINGS, not numbers -- including murmur2 fingerprints, which are
     * plainly numeric. That is what upstream builds (QJsonArray::append of a QString) and what
     * CurseForge accepts, so it is what gets built here. Worth stating because the opposite is the
     * natural assumption: CurseForge's own documentation types these fields as integers.
     */

    /// <summary>The body for the fingerprint match endpoint.</summary>
    public static JsonObject CreateFingerprintsBody(IEnumerable<uint> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);

        var array = new JsonArray();

        foreach (var fingerprint in fingerprints)
        {
            array.Add(fingerprint.ToString(CultureInfo.InvariantCulture));
        }

        return new JsonObject { ["fingerprints"] = array };
    }

    /// <summary>The body for fetching many projects at once.</summary>
    public static JsonObject CreateProjectsBody(IEnumerable<string> addonIds)
        => new() { ["modIds"] = ToArray(addonIds) };

    /// <summary>The body for fetching many files at once.</summary>
    public static JsonObject CreateFilesBody(IEnumerable<string> fileIds)
        => new() { ["fileIds"] = ToArray(fileIds) };

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var array = new JsonArray();

        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    public static string FingerprintsUrl => $"{BaseUrl}/fingerprints";

    public static string ProjectsUrl => $"{BaseUrl}/mods";

    public static string FilesUrl => $"{BaseUrl}/mods/files";

    // ================================================================== fetching

    public override Task<JsonNode> SearchProjectsAsync(SearchArgs args, CancellationToken cancellationToken = default)
        => GetJsonAsync(GetSearchUrl(args)!, cancellationToken);

    public override Task<JsonNode> GetProjectAsync(string addonId, CancellationToken cancellationToken = default)
        => GetJsonAsync(GetInfoUrl(addonId), cancellationToken);

    public override Task<JsonNode> GetProjectsAsync(
        IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken = default)
        => PostJsonAsync(ProjectsUrl, CreateProjectsBody(addonIds), cancellationToken);

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

    /// <summary>Identifies local files by their murmur2 fingerprints.</summary>
    public Task<JsonNode> MatchFingerprintsAsync(
        IEnumerable<uint> fingerprints,
        CancellationToken cancellationToken = default)
        => PostJsonAsync(FingerprintsUrl, CreateFingerprintsBody(fingerprints), cancellationToken);

    public Task<JsonNode> GetFilesAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken = default)
        => PostJsonAsync(FilesUrl, CreateFilesBody(fileIds), cancellationToken);

    public Task<JsonNode> GetFileAsync(string addonId, string fileId, CancellationToken cancellationToken = default)
        => GetJsonAsync(GetFileUrl(addonId, fileId), cancellationToken);

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, new Uri(url));

        // Every CurseForge endpoint is 403 without this, including the public ones.
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);

        return request;
    }

    private Task<JsonNode> GetJsonAsync(string url, CancellationToken cancellationToken)
        => SendAsync(NewRequest(HttpMethod.Get, url), url, cancellationToken);

    private Task<JsonNode> PostJsonAsync(string url, JsonObject body, CancellationToken cancellationToken)
    {
        var request = NewRequest(HttpMethod.Post, url);
        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        return SendAsync(request, url, cancellationToken);
    }

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, string url, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            request.Dispose();

            throw new InvalidOperationException($"{DebugName} was built for URL construction only.");
        }

        using (request)
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            return Json.RequireDocument(content, url);
        }
    }
}
