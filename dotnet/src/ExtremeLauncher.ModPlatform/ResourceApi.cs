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
 * Ported from launcher/modplatform/ResourceAPI.h.
 *
 * THE QUESTIONS A PROVIDER CAN BE ASKED. Search, fetch one project, fetch many, fetch a project's
 * versions, resolve a dependency. Each provider answers in its own JSON, so what comes back here is
 * the raw document and a provider-specific parser turns it into ModIndex types -- that split is
 * upstream's and it is the right one: the shape of a request and the shape of a reply change
 * independently.
 *
 * THE CALLBACK STRUCTS ARE GONE. Upstream passes a struct of on_succeed / on_fail / on_abort per call,
 * because a Qt task cannot be awaited. Awaiting is exactly what those three describe: a return value,
 * an exception, and a cancellation token. Nothing is lost -- and the failure path gets better, because
 * "the caller forgot to set on_fail" stops being possible.
 *
 * NOT-SUPPORTED IS A REAL ANSWER. Upstream's defaults log "TODO" and return a null task, which the
 * caller must remember to check; a forgotten check is a null dereference. Here the base throws, so a
 * provider that cannot answer a question says so where it happens.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

// Core.Version is the Minecraft-version comparator, not System.Version.
using Version = ExtremeLauncher.Core.Version;

namespace ExtremeLauncher.ModPlatform;

/// <summary>One way a provider can order search results.</summary>
/// <param name="Index">
/// Position in the provider's own list. CurseForge sends this number in the request; Modrinth ignores
/// it. It also fixes the order the options are offered in, which is why it is data and not an index
/// into an array.
/// </param>
/// <param name="Name">The provider's own name for the sort. Modrinth sends this.</param>
/// <param name="ReadableName">What to show a user.</param>
public sealed record SortingMethod(uint Index, string Name, string ReadableName);

/// <summary>
/// What to search for. Everything but the type is optional, and absent means "do not constrain".
/// </summary>
/// <remarks>
/// Null rather than empty, mirroring upstream's std::optional, so a provider can tell "no filter"
/// from "an empty filter". Whether it SHOULD distinguish them is up to the provider, and upstream is
/// not consistent about it -- ModrinthApi drops an empty loader set from a search but sends it as
/// <c>loaders=[""]</c> when listing versions. That inconsistency is preserved and pinned by a test
/// rather than quietly resolved, because which behaviour was meant is not recoverable from the code.
/// </remarks>
public sealed record SearchArgs
{
    public required ResourceType Type { get; init; }

    /// <summary>How many results to skip. Paging is by offset, not cursor, on both providers.</summary>
    public int Offset { get; init; }

    public string? Search { get; init; }

    public SortingMethod? Sorting { get; init; }

    public ModLoaderTypes? Loaders { get; init; }

    public IReadOnlyList<Version>? Versions { get; init; }

    /// <summary>Client, server or both.</summary>
    public string? Side { get; init; }

    public IReadOnlyList<string>? CategoryIds { get; init; }
}

/// <summary>Which project's versions to fetch, and how to narrow them.</summary>
public sealed record VersionSearchArgs
{
    public required IndexedPack Pack { get; init; }

    public IReadOnlyList<Version>? McVersions { get; init; }

    public ModLoaderTypes? Loaders { get; init; }
}

/// <summary>Which project to fetch the long-form details of.</summary>
public sealed record ProjectInfoArgs
{
    public required IndexedPack Pack { get; init; }
}

/// <summary>A dependency to resolve to a concrete file.</summary>
public sealed record DependencySearchArgs
{
    public required Dependency Dependency { get; init; }

    public required Version McVersion { get; init; }

    public ModLoaderTypes Loader { get; init; }
}

/// <summary>What a provider can be asked. Implementations answer with the provider's raw JSON.</summary>
public interface IResourceApi
{
    /// <summary>A name for logs and error messages.</summary>
    string DebugName { get; }

    /// <summary>The orderings this provider offers, in the order to offer them.</summary>
    IReadOnlyList<SortingMethod> SortingMethods { get; }

    Task<JsonNode> SearchProjectsAsync(SearchArgs args, CancellationToken cancellationToken = default);

    Task<JsonNode> GetProjectAsync(string addonId, CancellationToken cancellationToken = default);

    Task<JsonNode> GetProjectsAsync(IReadOnlyList<string> addonIds, CancellationToken cancellationToken = default);

    Task<JsonNode> GetProjectInfoAsync(ProjectInfoArgs args, CancellationToken cancellationToken = default);

    Task<JsonNode> GetProjectVersionsAsync(VersionSearchArgs args, CancellationToken cancellationToken = default);

    Task<JsonNode> GetDependencyVersionAsync(DependencySearchArgs args, CancellationToken cancellationToken = default);
}

/// <summary>
/// The shared half of a provider API: everything a provider does not override is refused rather than
/// silently returning nothing.
/// </summary>
public abstract class ResourceApi : IResourceApi
{
    public virtual string DebugName => "External resource API";

    public abstract IReadOnlyList<SortingMethod> SortingMethods { get; }

    public virtual Task<JsonNode> SearchProjectsAsync(SearchArgs args, CancellationToken cancellationToken = default)
        => throw Unsupported(nameof(SearchProjectsAsync));

    public virtual Task<JsonNode> GetProjectAsync(string addonId, CancellationToken cancellationToken = default)
        => throw Unsupported(nameof(GetProjectAsync));

    public virtual Task<JsonNode> GetProjectsAsync(IReadOnlyList<string> addonIds, CancellationToken cancellationToken = default)
        => throw Unsupported(nameof(GetProjectsAsync));

    public virtual Task<JsonNode> GetProjectInfoAsync(ProjectInfoArgs args, CancellationToken cancellationToken = default)
        => throw Unsupported(nameof(GetProjectInfoAsync));

    public virtual Task<JsonNode> GetProjectVersionsAsync(VersionSearchArgs args, CancellationToken cancellationToken = default)
        => throw Unsupported(nameof(GetProjectVersionsAsync));

    public virtual Task<JsonNode> GetDependencyVersionAsync(DependencySearchArgs args, CancellationToken cancellationToken = default)
        => throw Unsupported(nameof(GetDependencyVersionAsync));

    private NotSupportedException Unsupported(string operation)
        => new($"{DebugName} does not support {operation}.");

    /// <summary>
    /// Renders Minecraft versions as the quoted, comma-separated list both APIs take in a query.
    /// </summary>
    /// <remarks>
    /// Upstream appends a comma per version and then deletes the last character. On an EMPTY list that
    /// is <c>remove(-1, 1)</c>, which Qt treats as out of range and ignores, so the result is the
    /// empty string rather than a crash. Building the join directly gets there without depending on
    /// that: an empty list is a real case, since "any version" is the default filter.
    /// </remarks>
    protected static string GetGameVersionsString(IEnumerable<Version> mcVersions)
    {
        ArgumentNullException.ThrowIfNull(mcVersions);

        return string.Join(',', mcVersions.Select(v => $"\"{v}\""));
    }
}
