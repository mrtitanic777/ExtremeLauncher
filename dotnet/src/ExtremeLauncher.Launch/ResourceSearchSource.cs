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
 * Searching Modrinth and CurseForge for mods, and listing what versions of one exist.
 *
 * EVERY PIECE OF THIS WAS PORTED AND TESTED WAVES AGO and none of it was reachable: ModrinthApi and
 * FlameApi build the URLs, ModrinthPackIndex and FlameModIndex parse the answers, ResourceDownloadTask
 * installs the file. What was missing was the twenty lines that call one after the other.
 *
 * IT LIVES IN Launch, not in the view models, for the same reason MetaVersionListSource does: the view
 * models must stay free of HTTP so they can be tested without a server, and the CLI may want this too.
 *
 * THE TWO PROVIDERS ARE NOT INTERCHANGEABLE, and the difference is not hidden here:
 *
 *   Modrinth    open API, no key. Always available.
 *   CurseForge  needs an API key this build does not ship (see BuildConfigOverrides). Without one the
 *               provider is refused up front with a reason, rather than failing on a 403 that reads
 *               like the search was broken.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.Launch;

public sealed class ResourceSearchSource
{
    private readonly HttpClient _client;

    private readonly ModrinthApi _modrinth;

    private readonly FlameApi _flame;

    public ResourceSearchSource(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _modrinth = new ModrinthApi(client);

        /*
         * The key is read at construction, so a launcher started without one and given one later needs
         * restarting -- which is fine, because BuildConfigOverrides only reads it at startup anyway.
         */
        _flame = new FlameApi(client, BuildConfig.Instance.FlameApiKey);
    }

    /// <summary>Whether a provider can be used at all in this build.</summary>
    public static bool IsAvailable(ResourceProvider provider)
        => provider != ResourceProvider.Flame || BuildConfig.Instance.FlameApiKey.Length != 0;

    /// <summary>Why a provider is unavailable, or empty when it is fine.</summary>
    public static string UnavailableReason(ResourceProvider provider)
        => IsAvailable(provider)
            ? string.Empty
            : "CurseForge needs an API key, which this build does not have. "
              + $"Put \"flameApiKey\" in {BuildConfigOverrides.FileName} to enable it.";

    /// <summary>Searches a provider and returns the parsed results.</summary>
    public async Task<IReadOnlyList<IndexedPack>> SearchAsync(
        ResourceProvider provider,
        SearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!IsAvailable(provider))
        {
            throw new LauncherException(UnavailableReason(provider));
        }

        var api = ApiFor(provider);

        var response = await api.SearchProjectsAsync(args, cancellationToken).ConfigureAwait(false);

        /*
         * The two providers wrap their results differently -- Modrinth in "hits", CurseForge in
         * "data" -- and both are objects rather than bare arrays. Upstream's pages know this because
         * each has its own model class; here it is one line rather than two classes.
         */
        var array = response switch
        {
            JsonObject obj when obj["hits"] is JsonArray hits => hits,
            JsonObject obj when obj["data"] is JsonArray data => data,
            JsonArray bare => bare,
            _ => null,
        };

        if (array is null)
        {
            return [];
        }

        var results = new List<IndexedPack>(array.Count);

        foreach (var entry in array)
        {
            if (entry is not JsonObject obj)
            {
                continue;
            }

            var pack = new IndexedPack { Provider = provider };

            try
            {
                if (provider == ResourceProvider.Modrinth)
                {
                    ModrinthPackIndex.LoadIndexedPack(pack, obj);
                }
                else
                {
                    FlameModIndex.LoadIndexedPack(pack, obj);
                }
            }
            catch (Exception e) when (e is Core.JsonException or System.Text.Json.JsonException
                or InvalidOperationException or FormatException)
            {
                // One unparseable result must not lose the other twenty-four. A search that returns
                // nothing because of a single odd entry looks like "no mods match", which is wrong.
                continue;
            }

            if (pack.Name.Length != 0)
            {
                results.Add(pack);
            }
        }

        return results;
    }

    /// <summary>Fills in a pack's version list, in place.</summary>
    public async Task LoadVersionsAsync(
        IndexedPack pack,
        VersionSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var api = ApiFor(pack.Provider);

        var response = await api.GetProjectVersionsAsync(args, cancellationToken).ConfigureAwait(false);

        // CurseForge wraps its file list in "data"; Modrinth returns the array itself.
        var array = response switch
        {
            JsonArray bare => bare,
            JsonObject obj when obj["data"] is JsonArray data => data,
            _ => null,
        };

        if (array is null)
        {
            return;
        }

        if (pack.Provider == ResourceProvider.Modrinth)
        {
            ModrinthPackIndex.LoadIndexedPackVersions(pack, array);
        }
        else
        {
            FlameModIndex.LoadIndexedPackVersions(pack, array);
        }
    }

    private ResourceApi ApiFor(ResourceProvider provider)
        => provider == ResourceProvider.Modrinth ? _modrinth : _flame;
}
