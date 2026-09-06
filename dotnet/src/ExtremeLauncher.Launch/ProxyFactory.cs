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
 * Ported from Application::updateProxySettings in launcher/Application.cpp and the page that drives
 * it, launcher/ui/pages/global/ProxyPage.cpp.
 *
 * GOING THROUGH A PROXY, which for a lot of people is the difference between a launcher that works at
 * all and one that cannot reach anything. A launcher does nothing but talk to the network -- metadata,
 * mods, assets, Java, sign-in -- so on a network that requires a proxy, none of it works.
 *
 * FOUR VALUES, upstream's, spelled its way because the config file is shared with it:
 *
 *   Default   whatever the machine is configured to use
 *   None      no proxy, even if the machine has one. THE DEFAULT.
 *   HTTP      an explicit HTTP proxy
 *   SOCKS5    an explicit SOCKS5 proxy
 *
 * "None" BEING THE DEFAULT IS THE SURPRISING PART, and it is upstream's choice, not a slip: a fresh
 * install connects directly and ignores the machine's proxy settings entirely. It cuts both ways --
 * somebody behind a corporate proxy has to come to the settings and pick "Default" before anything
 * works, but somebody with a stale WPAD or a leftover proxy entry (a common way for Windows to break
 * every application at once) is unaffected. This is a DIVERGENCE FROM WHAT THIS PORT DID BEFORE:
 * a plain HttpClient follows the system settings, so until this file existed the port behaved as if
 * "Default" were selected.
 *
 * ONE MORE THING WORTH KNOWING, which upstream puts at the top of the page: this applies to the
 * LAUNCHER ONLY. Minecraft itself takes no proxy settings, so the game's own traffic ignores all of
 * it. Somebody setting a proxy to make the game reach a server is going to be disappointed, and it is
 * better to say so on screen than to let them find out.
 */

using System.Globalization;
using System.Net;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.Launch;

public static class ProxyFactory
{
    /// <summary>The four values the setting can hold, in upstream's on-screen order.</summary>
    /// <remarks>
    /// The ids are a COMPATIBILITY SURFACE -- they are what lands in extremelauncher.cfg, and upstream
    /// reads the same file -- so they are upstream's exact spelling and must not be reworded when the
    /// labels are.
    /// </remarks>
    public static readonly (string Id, string Label)[] Types =
    [
        ("Default", "Use the system settings"),
        ("None", "No proxy"),
        ("HTTP", "HTTP proxy"),
        ("SOCKS5", "SOCKS5 proxy"),
    ];

    /// <summary>
    /// Builds the handler an HttpClient should use.
    /// </summary>
    /// <remarks>
    /// A HANDLER RATHER THAN A PROXY, because the two answers "use the system proxy" and "use no
    /// proxy at all" are not both expressible as an IWebProxy: the first is the .NET default and the
    /// second needs UseProxy turned off. Returning the handler keeps that decision in one place.
    ///
    /// This is read ONCE, when the client is built. A live HttpClient cannot have its proxy changed,
    /// so a proxy edited in the settings window applies from the next start -- which the settings
    /// window says on screen rather than leaving the user to discover.
    /// </remarks>
    public static HttpMessageHandler CreateHandler(SettingsObject? settings)
    {
        var handler = new SocketsHttpHandler
        {
            // Kept from the plain HttpClient this replaces: the metadata and CDN hosts redirect, and
            // a handler that refused to follow would break every download.
            AllowAutoRedirect = true,
        };

        if (settings is null)
        {
            // No settings read yet -- the .NET default, which is the machine's own configuration.
            return handler;
        }

        var type = Normalise(settings.GetString("ProxyType", "None"));

        switch (type)
        {
            case "none":
                // Explicitly OFF, which is not the same as leaving it alone: this ignores a proxy the
                // machine does have. It is also what a fresh install gets.
                handler.UseProxy = false;

                return handler;

            case "http":
            case "socks5":
                break;

            default:
                /*
                 * "Default" and ANYTHING UNRECOGNISED both land here, which is upstream's behaviour --
                 * its final else branch calls setUseSystemConfiguration(true). Note that this is not
                 * the same as the registered default: a config with no ProxyType at all reads as
                 * "None" and connects directly, while a config with a value nobody recognises uses
                 * the system settings. A value that cannot be read must not turn networking off.
                 */
                return handler;
        }

        var host = settings.GetString("ProxyAddr", string.Empty).Trim();
        var port = settings.GetInt("ProxyPort", 8080);

        if (host.Length == 0 || port is <= 0 or > 65535)
        {
            /*
             * A HALF-CONFIGURED PROXY FALLS BACK to the system rather than being applied. An empty
             * host with the type set to HTTP is what you get from picking the radio button and not
             * filling the form in, and building a proxy from it would break every request with an
             * error that says nothing about the real cause.
             *
             * The port check also covers an upstream bug rather than reproducing it: Application.cpp
             * reads ProxyPort through value<qint16>(), a SIGNED 16-bit read, so a proxy on any port
             * above 32767 arrives negative. The page itself writes and reads the same number as
             * uint16_t, so the settings window shows the right value while the connection uses a
             * wrong one. Here an impossible port is refused outright instead.
             */
            return handler;
        }

        var scheme = type == "socks5" ? "socks5" : "http";

        var proxy = new WebProxy(new Uri($"{scheme}://{host}:{port.ToString(CultureInfo.InvariantCulture)}"));

        var user = settings.GetString("ProxyUser", string.Empty);
        var password = settings.GetString("ProxyPass", string.Empty);

        if (user.Length != 0)
        {
            // Only when there is a user name. An empty credential is not the same as no credential,
            // and some proxies refuse an anonymous bind that arrives carrying an empty user.
            proxy.Credentials = new NetworkCredential(user, password);
        }

        handler.Proxy = proxy;
        handler.UseProxy = true;

        return handler;
    }

    /// <summary>
    /// A one-line description of what the proxy setting will actually do, for the log and the
    /// settings window.
    /// </summary>
    /// <remarks>
    /// Worth showing because three of the four values look identical in a config file and behave
    /// completely differently, and because a half-filled form silently does nothing.
    /// </remarks>
    public static string Describe(SettingsObject? settings)
    {
        if (settings is null)
        {
            return string.Empty;
        }

        var type = Normalise(settings.GetString("ProxyType", "None"));
        var host = settings.GetString("ProxyAddr", string.Empty).Trim();
        var port = settings.GetInt("ProxyPort", 8080);

        return type switch
        {
            "none" => "Connecting directly, ignoring any proxy this machine is configured with.",

            "http" or "socks5" when host.Length == 0 || port is <= 0 or > 65535
                => "No usable proxy address is set, so the system settings are being used instead.",

            "http" => $"Connecting through the HTTP proxy at {host}:{port}.",
            "socks5" => $"Connecting through the SOCKS5 proxy at {host}:{port}.",

            _ => "Using whatever proxy this machine is configured with.",
        };
    }

    /// <remarks>
    /// UPSTREAM COMPARES CASE-SENSITIVELY, so to it "none" is not "None" and falls through to the
    /// system settings. This is lenient instead, which is a deliberate divergence and only reachable
    /// through a hand-edited config: matching what somebody plainly meant beats using a proxy they
    /// did not ask for. "System" is accepted alongside "Default" for the same reason.
    /// </remarks>
    private static string Normalise(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();

        return trimmed == "system" ? "default" : trimmed;
    }
}
