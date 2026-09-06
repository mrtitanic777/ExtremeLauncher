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
 * A LIVE PROBE (wave 68) of the pack browser's foundation: search Modrinth for real modpacks and read
 * the results through the port's own ResourceSearchSource, then load one pack's versions. Modrinth's
 * search is keyless, so this needs no credentials -- unlike CurseForge, which the browser also offers.
 *
 * SKIPPED when there is no network rather than failed: a probe against a live server cannot be a
 * build-breaker. When the network is there it is the only test that would catch Modrinth changing the
 * shape of its search response out from under the parser.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ModrinthPackSearchLiveTests
{
    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildConfig.Instance.UserAgent);

        return client;
    }

    [SkippableFact]
    public async Task ModrinthModpackSearchParsesRealResults()
    {
        using var client = Client();
        var source = new ResourceSearchSource(client);

        IReadOnlyList<IndexedPack> results;

        try
        {
            results = await source.SearchAsync(
                ResourceProvider.Modrinth,
                new SearchArgs { Type = ResourceType.Modpack, Search = "fabric" },
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new SkipException($"Modrinth is not reachable: {e.Message}");
        }

        // A search for "fabric" modpacks returns many; the parser must have made real rows of them.
        Assert.NotEmpty(results);
        Assert.All(results, pack =>
        {
            Assert.Equal(ResourceProvider.Modrinth, pack.Provider);
            Assert.NotEqual(string.Empty, pack.AddonId);
            Assert.NotEqual(string.Empty, pack.Name);
        });
    }

    [SkippableFact]
    public async Task AModrinthPacksVersionsLoadFromTheLiveApi()
    {
        using var client = Client();
        var source = new ResourceSearchSource(client);

        try
        {
            var results = await source.SearchAsync(
                ResourceProvider.Modrinth,
                new SearchArgs { Type = ResourceType.Modpack, Search = "fabric" },
                CancellationToken.None).ConfigureAwait(true);

            Skip.If(results.Count == 0, "Modrinth returned no modpacks to inspect.");

            var pack = results[0];
            await source.LoadVersionsAsync(pack, new VersionSearchArgs { Pack = pack }, CancellationToken.None)
                .ConfigureAwait(true);

            // The whole point of the version list: a real pack has releases, each with an id and files.
            Assert.True(pack.VersionsLoaded);
            Assert.NotEmpty(pack.Versions);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new SkipException($"Modrinth is not reachable: {e.Message}");
        }
    }
}
