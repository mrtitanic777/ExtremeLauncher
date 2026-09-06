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
 * Going through a proxy.
 *
 * THE FALLBACK CASES ARE THE POINT. A launcher does nothing but talk to the network, so a proxy
 * setting that is wrong in a way nobody notices takes every feature down at once -- and the settings
 * most likely to be wrong are the half-filled ones.
 */

using System.Net;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ProxyFactoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "el-proxy-" + Guid.NewGuid().ToString("N"));

    public ProxyFactoryTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SettingsObject Settings(params (string Key, object Value)[] values)
    {
        var settings = GlobalSettings.Create(Path.Combine(_folder, "extremelauncher.cfg"));

        foreach (var (key, value) in values)
        {
            settings.Set(key, value);
        }

        return settings;
    }

    private static SocketsHttpHandler Handler(SettingsObject? settings)
        => Assert.IsType<SocketsHttpHandler>(ProxyFactory.CreateHandler(settings));

    [Fact]
    public void AFreshInstallConnectsDirectlyRatherThanFollowingTheSystem()
    {
        /*
         * UPSTREAM'S DEFAULT IS "None", and this test was originally written the other way round on
         * the assumption that following the machine was the sensible default. Application.cpp:631
         * says otherwise, and it matters in both directions: somebody behind a corporate proxy gets
         * nothing until they come to the settings, and somebody with a stale system proxy entry is
         * unaffected by it.
         *
         * It is also a DIVERGENCE FROM WHAT THIS PORT DID BEFORE this wave -- a plain HttpClient
         * follows the system settings -- so this assertion is the one that pins the change.
         */
        var handler = Handler(Settings());

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void NoProxyTurnsItOffEvenWhenTheMachineHasOne()
    {
        // Not the same as "Default": this ignores a system proxy that does exist, and somebody chose
        // that on purpose.
        var handler = Handler(Settings(("ProxyType", "None")));

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void DefaultMeansTheMachinesOwnSettings()
    {
        // The value somebody on a network that requires a proxy has to come and pick.
        var handler = Handler(Settings(("ProxyType", "Default")));

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void AnHttpProxyIsBuiltFromTheAddressAndPort()
    {
        var handler = Handler(Settings(
            ("ProxyType", "HTTP"),
            ("ProxyAddr", "proxy.example.invalid"),
            ("ProxyPort", 3128)));

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);

        Assert.True(handler.UseProxy);
        Assert.Equal("http://proxy.example.invalid:3128/", proxy.Address?.ToString());
    }

    [Fact]
    public void ASocks5ProxyGetsTheSocksScheme()
    {
        // .NET tells the two apart by the URI scheme, so this is the whole difference between them.
        var handler = Handler(Settings(
            ("ProxyType", "SOCKS5"),
            ("ProxyAddr", "127.0.0.1"),
            ("ProxyPort", 1080)));

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);

        Assert.StartsWith("socks5://", proxy.Address?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialsAreAttachedWhenAUserNameIsGiven()
    {
        var handler = Handler(Settings(
            ("ProxyType", "HTTP"),
            ("ProxyAddr", "proxy.example.invalid"),
            ("ProxyPort", 3128),
            ("ProxyUser", "someone"),
            ("ProxyPass", "hunter2")));

        var credential = Assert.IsType<NetworkCredential>(Assert.IsType<WebProxy>(handler.Proxy).Credentials);

        Assert.Equal("someone", credential.UserName);
        Assert.Equal("hunter2", credential.Password);
    }

    [Fact]
    public void NoUserNameMeansNoCredentials()
    {
        // Attaching an empty credential is not the same as attaching none, and some proxies refuse
        // an anonymous bind that arrives with an empty user.
        var handler = Handler(Settings(
            ("ProxyType", "HTTP"),
            ("ProxyAddr", "proxy.example.invalid"),
            ("ProxyPort", 3128)));

        Assert.Null(Assert.IsType<WebProxy>(handler.Proxy).Credentials);
    }

    [Fact]
    public void AProxyWithNoAddressFallsBackToTheSystem()
    {
        /*
         * What you get from choosing "HTTP proxy" and not filling the form in. Building a proxy from
         * an empty host breaks every request with an error that says nothing about the real cause.
         */
        var handler = Handler(Settings(("ProxyType", "HTTP"), ("ProxyAddr", string.Empty)));

        Assert.Null(handler.Proxy);
        Assert.True(handler.UseProxy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void AnImpossiblePortFallsBackToTheSystem(int port)
    {
        var handler = Handler(Settings(
            ("ProxyType", "HTTP"),
            ("ProxyAddr", "proxy.example.invalid"),
            ("ProxyPort", port)));

        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void AnUnrecognisedTypeDoesNotTurnNetworkingOff()
    {
        // A config written by hand, or by a newer version with more proxy kinds, must still connect.
        var handler = Handler(Settings(("ProxyType", "carrier-pigeon")));

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void TheTypeIsReadLenientlyAboutCase()
    {
        /*
         * A DELIBERATE DIVERGENCE. Upstream compares the string case-sensitively, so to it "socks5"
         * is not "SOCKS5" and falls through to the system settings. Only a hand-edited config can
         * reach this, and honouring what somebody plainly meant beats using a proxy they did not ask
         * for.
         */
        var handler = Handler(Settings(
            ("ProxyType", "socks5"),
            ("ProxyAddr", "127.0.0.1"),
            ("ProxyPort", 1080)));

        Assert.NotNull(handler.Proxy);

        // Same reason: "system" reads as "Default", which is what somebody would expect it to mean.
        Assert.True(Handler(Settings(("ProxyType", "system"))).UseProxy);
    }

    [Fact]
    public void AConfigWrittenBeforeTheKeyWasRenamedStillWorks()
    {
        /*
         * ProxyAddr was called ProxyHostName once, and upstream registers the two as synonyms of one
         * setting. THE SYNONYM IS A FILE-FORMAT THING, NOT A LOOKUP KEY -- SettingsObject.cpp:68
         * inserts only synonyms.first() into the map, so Set("ProxyHostName", ...) finds no setting
         * at all. The first version of this test called Set and failed for exactly that reason,
         * which was the test being wrong about the mechanism rather than the mechanism being broken.
         *
         * So this goes through the only path that can really carry an old spelling: a config file on
         * disk, written by a version that predates the rename. Losing it silently would leave
         * somebody unable to reach anything with no idea why.
         */
        var path = Path.Combine(_folder, "legacy.cfg");

        File.WriteAllText(path, "ProxyType=HTTP\nProxyHostName=legacy.example.invalid\nProxyPort=3128\n");

        var proxy = Assert.IsType<WebProxy>(Handler(GlobalSettings.Create(path)).Proxy);

        Assert.Contains("legacy.example.invalid", proxy.Address?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoSettingsAtAllTheHandlerStillWorks()
    {
        // The CLI builds a client before it has read anything.
        Assert.True(Handler(null).UseProxy);
    }

    [Fact]
    public void RedirectsAreStillFollowed()
    {
        // Kept from the plain HttpClient this replaces: the metadata and CDN hosts redirect, and a
        // handler that refused to follow would break every download.
        Assert.True(Handler(Settings()).AllowAutoRedirect);
    }

    [Fact]
    public void TheDescriptionSaysWhatWillActuallyHappen()
    {
        // Three of the four modes look identical in a config file and behave completely differently.
        Assert.Contains("directly", ProxyFactory.Describe(Settings(("ProxyType", "none"))), StringComparison.Ordinal);

        Assert.Contains(
            "proxy.example.invalid:3128",
            ProxyFactory.Describe(Settings(("ProxyType", "HTTP"), ("ProxyAddr", "proxy.example.invalid"), ("ProxyPort", 3128))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheDescriptionAdmitsWhenAHalfFilledFormIsBeingIgnored()
    {
        // The case somebody would otherwise spend an afternoon on.
        Assert.Contains(
            "system settings",
            ProxyFactory.Describe(Settings(("ProxyType", "SOCKS5"), ("ProxyAddr", string.Empty))),
            StringComparison.Ordinal);
    }
}
