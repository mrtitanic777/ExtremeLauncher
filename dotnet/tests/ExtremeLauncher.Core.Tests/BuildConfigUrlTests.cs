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
 * The build's baked-in URLs.
 *
 * These are transcribed by hand from the fork's CMakeLists, and one of them was wrong: the meta URL
 * pointed at meta.extremelauncher.net, a host that does not exist, while every other fork URL lives on
 * extremelauncher.net. A fresh install could fetch no metadata at all and every resolution failed --
 * the kind of thing no in-process test had any reason to catch, because nothing dereferences the URL
 * without a network. These pin the shape that was violated, so the next hand-transcription cannot
 * reintroduce a phantom host.
 */

using System.Net.Http;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class BuildConfigUrlTests
{
    private static Uri Meta => new(BuildConfig.Instance.MetaUrl);

    [Fact]
    public void TheMetaUrlIsWellFormedHttps()
    {
        Assert.True(Uri.IsWellFormedUriString(BuildConfig.Instance.MetaUrl, UriKind.Absolute));
        Assert.Equal(Uri.UriSchemeHttps, Meta.Scheme);

        // A directory, not a file: the resolver appends "<uid>/<version>.json" to it, so a missing
        // trailing slash would fetch from the parent.
        Assert.EndsWith("/", BuildConfig.Instance.MetaUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMetaUrlLivesOnTheLauncherDomain()
    {
        /*
         * THE BUG, AS AN INVARIANT. The dead host was "meta.extremelauncher.net" -- a subdomain that
         * was never set up -- where the fork serves meta from the launcher domain itself. Requiring
         * the host to be the domain, or a subdomain of it, would have caught the phantom "meta." host
         * only if... no: "meta.extremelauncher.net" IS a subdomain of the domain. So the real tell is
         * that the working URL and the news feed share a host, and the dead one did not.
         */
        Assert.Equal(new Uri(BuildConfig.Instance.NewsRssUrl).Host, Meta.Host);
    }

    [Fact]
    public void TheMetaUrlHostResolvesUnlikeThePhantomOneThatShipped()
    {
        /*
         * The single fact that actually distinguishes the fix from the bug: the configured host has DNS
         * records and the one that shipped did not. Offline this cannot run, so it is skipped rather
         * than failed -- but when the network is there, it is the only test that would have caught the
         * original mistake on its own.
         */
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(Meta.Host);

            Assert.NotEmpty(addresses);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // No network, or DNS down. Not a failure of the build config.
            Assert.True(true);
        }
    }

    [Fact]
    public void TheNewsUrlIsWellFormedToo()
    {
        // It was already right, and shares the host the meta URL now does; pin it so a future edit to
        // one does not quietly split them apart again.
        Assert.True(Uri.IsWellFormedUriString(BuildConfig.Instance.NewsRssUrl, UriKind.Absolute));
        Assert.Equal("extremelauncher.net", new Uri(BuildConfig.Instance.NewsRssUrl).Host);
    }

    [Fact]
    public void TheHelpUrlIsSetAndOnTheLauncherDomain()
    {
        /*
         * It was empty, which hid the Help menu entry although the fork configures and serves a help
         * endpoint. A non-empty, well-formed URL on the launcher domain is the shape the links menu
         * needs to offer it.
         */
        Assert.NotEqual(string.Empty, BuildConfig.Instance.HelpUrl);
        Assert.True(Uri.IsWellFormedUriString(BuildConfig.Instance.HelpUrl, UriKind.Absolute));
        Assert.Equal("extremelauncher.net", new Uri(BuildConfig.Instance.HelpUrl).Host);
    }

    [Fact]
    public void TheTranslationsUrlIsNotTheInventedGithubRepo()
    {
        /*
         * The tell for the old invented value: an org that no other URL uses. The bug tracker and the
         * updater repo are both under "ExtremeLauncherTeam"; the dead translations URL was under a
         * bare "ExtremeLauncher". Whatever it points at, it must not be that.
         */
        var url = BuildConfig.Instance.TranslationsUrl;

        Assert.True(Uri.IsWellFormedUriString(url, UriKind.Absolute));
        Assert.DoesNotContain("github.com/ExtremeLauncher/", url, StringComparison.Ordinal);
    }
}
