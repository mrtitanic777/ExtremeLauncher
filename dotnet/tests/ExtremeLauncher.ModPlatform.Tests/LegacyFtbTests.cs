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
 * legacy_ftb has no upstream unit test, so these pin the parse rules ported from
 * PackFetchTask::parseAndAddPacks -- the ";"-separated version list, the "bugged" flag an empty entry
 * raises, and the current-version fallback / "broken" flag. The live test is a probe of the FTB CDN,
 * skipped rather than failed when the (old) endpoint is unreachable.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class LegacyFtbTests
{
    private static List<LegacyFtbModpack> Parse(string xml)
    {
        Assert.True(LegacyFtbPackParser.TryParse(xml, LegacyFtbPackType.Public, out var packs));

        return packs;
    }

    [Fact]
    public void AWellFormedPackMapsEveryField()
    {
        var packs = Parse(
            """
            <modpacks>
              <modpack name="Direwolf20" version="1.12.0" mcVersion="1.20.1" author="FTB"
                       description="A kitchen-sink pack" mods="Thermal;Create" logo="dw20.png"
                       oldVersions="1.10.0;1.11.0;1.12.0" dir="dw20" url="Direwolf20.zip" />
            </modpacks>
            """);

        var pack = Assert.Single(packs);

        Assert.Equal("Direwolf20", pack.Name);
        Assert.Equal("1.12.0", pack.CurrentVersion);
        Assert.Equal("1.20.1", pack.McVersion);
        Assert.Equal("FTB", pack.Author);
        Assert.Equal("A kitchen-sink pack", pack.Description);
        Assert.Equal("Thermal;Create", pack.Mods);
        Assert.Equal("dw20.png", pack.Logo);
        Assert.Equal("dw20", pack.Dir);

        // The archive attribute is called "url" in the XML but is really the file name.
        Assert.Equal("Direwolf20.zip", pack.File);
        Assert.Equal(["1.10.0", "1.11.0", "1.12.0"], pack.OldVersions);

        Assert.False(pack.Bugged);
        Assert.False(pack.Broken);
        Assert.Equal(LegacyFtbPackType.Public, pack.Type);
    }

    [Fact]
    public void AnEmptyVersionEntryIsDroppedAndFlagsThePackBugged()
    {
        var pack = Assert.Single(Parse(
            """
            <modpacks><modpack name="Gappy" version="1.1" oldVersions="1.0;;1.1" /></modpacks>
            """));

        Assert.Equal(["1.0", "1.1"], pack.OldVersions);
        Assert.True(pack.Bugged);
        Assert.False(pack.Broken);
    }

    [Fact]
    public void WithNoVersionListTheCurrentVersionIsUsed()
    {
        var pack = Assert.Single(Parse(
            """
            <modpacks><modpack name="Solo" version="3.0" /></modpacks>
            """));

        // No oldVersions attribute at all still trips the empty-entry path (bugged), then recovers by
        // falling back to the current version -- exactly as upstream does.
        Assert.Equal(["3.0"], pack.OldVersions);
        Assert.True(pack.Bugged);
        Assert.False(pack.Broken);
    }

    [Fact]
    public void WithNoVersionAtAllThePackIsBroken()
    {
        var pack = Assert.Single(Parse(
            """
            <modpacks><modpack name="Empty" /></modpacks>
            """));

        Assert.Empty(pack.OldVersions);
        Assert.True(pack.Broken);
    }

    [Fact]
    public void TheListTypeIsStampedOnEveryPack()
    {
        Assert.True(LegacyFtbPackParser.TryParse(
            """<modpacks><modpack name="A" version="1" /></modpacks>""",
            LegacyFtbPackType.ThirdParty,
            out var packs));

        Assert.Equal(LegacyFtbPackType.ThirdParty, Assert.Single(packs).Type);
    }

    [Fact]
    public void MalformedXmlIsReportedAsAFailedList()
    {
        Assert.False(LegacyFtbPackParser.TryParse("<modpacks><modpack", LegacyFtbPackType.Public, out var packs));
        Assert.Empty(packs);
    }

    [Fact]
    public void AListWithNoPacksParsesToNothingButSucceeds()
    {
        Assert.True(LegacyFtbPackParser.TryParse("<modpacks></modpacks>", LegacyFtbPackType.Public, out var packs));
        Assert.Empty(packs);
    }

    [SkippableFact]
    public async Task TheLiveFtbListParsesIfTheCdnIsReachable()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildConfig.Instance.UserAgent);

        var source = new LegacyFtbPackSource(client);

        LegacyFtbFetchResult result;
        try
        {
            result = await source.FetchAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new SkipException($"The FTB CDN is not reachable: {e.Message}");
        }

        // The legacy CDN is old and may be gone or may serve a non-XML error page; either way that is
        // not this parser's failure, so skip rather than fail. When it IS serving the list, it has packs.
        Skip.If(result.FailedLists.Count != 0, "The FTB CDN did not return a parseable pack list.");

        Assert.NotEmpty(result.Public);
        Assert.All(result.Public, pack => Assert.NotEqual(string.Empty, pack.Name));
    }
}
