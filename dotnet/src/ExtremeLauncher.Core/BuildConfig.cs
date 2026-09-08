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
 * Ported from buildconfig/BuildConfig.{h,cpp.in}.
 *
 * Upstream this is a CMake-templated singleton: BuildConfig.cpp.in is expanded at configure time with
 * the version numbers, git hashes, platform slug and API keys baked in. The C# equivalent is an
 * instance with compile-time defaults that a build step can override, rather than a generated file --
 * same effect, no code generation.
 *
 * !! API KEYS ARE INTENTIONALLY EMPTY !!
 * README.md requires forks to either supply their own keys or blank them, and states that shipping
 * with the upstream keys means accepting the Microsoft Identity Platform and CurseForge 3rd-party API
 * terms. Empty means the dependent feature disables itself rather than the build breaking, which is
 * exactly the upstream escape hatch. Fill these in from a build-time secret, never from source
 * control.
 */

namespace ExtremeLauncher.Core;

/*
 * A RECORD, so a copy with one field changed is `with` rather than twenty assignments. Credentials are
 * supplied at startup (see BuildConfigOverrides) and hand-copying every other property to change three
 * of them is precisely the kind of code that silently drops a field when a new one is added.
 */
public sealed record BuildConfig
{
    public static BuildConfig Instance { get; set; } = new();

    // ---------------------------------------------------------------- identity

    public string LauncherName { get; init; } = "ExtremeLauncher";

    public string LauncherDisplayName { get; init; } = "Extreme Launcher";

    public string LauncherAppBinaryName { get; init; } = "extremelauncher";

    public string LauncherDomain { get; init; } = "extremelauncher.net";

    public string LauncherConfigFile { get; init; } = "extremelauncher.cfg";

    public string LauncherGit { get; init; } = "https://github.com/ExtremeLauncherTeam/ExtremeLauncher";

    public string Copyright { get; init; } = "Extreme Launcher Contributors";

    // ---------------------------------------------------------------- version

    public int VersionMajor { get; init; } = 5;

    public int VersionMinor { get; init; } = 1;

    public string VersionChannel { get; init; } = "develop";

    public string VersionString => $"{VersionMajor}.{VersionMinor}";

    /// <summary>
    /// A slug identifying the distribution, e.g. "archlinux" or "nixpkgs".
    /// </summary>
    /// <remarks>
    /// README.md: must NOT be "official" for third-party builds. "custom" is the safe default.
    /// </remarks>
    public string BuildPlatform { get; init; } = "custom";

    /// <summary>Identifies which updater artifacts apply, e.g. "win64".</summary>
    public string BuildArtifact { get; init; } = string.Empty;

    public string GitCommit { get; init; } = string.Empty;

    public string GitTag { get; init; } = string.Empty;

    public string GitRefspec { get; init; } = string.Empty;

    // ---------------------------------------------------------------- features

    public bool UpdaterEnabled { get; init; }

    public bool JavaDownloaderEnabled { get; init; }

    // ---------------------------------------------------------------- network

    public string UserAgent => $"{LauncherName}/{VersionString} ({BuildPlatform})";

    public string UserAgentUncached => $"{UserAgent} (uncached)";

    /// <summary>Where component metadata is fetched from.</summary>
    /// <remarks>
    /// THE FORK'S OWN URL, from CMakeLists' Launcher_META_URL. It was wrong here until a Quilt
    /// resolution probe hit it: this had "https://meta.extremelauncher.net/v1/", a host that does not
    /// resolve, while the fork actually configures "https://extremelauncher.net/_api/meta-v1/" -- on
    /// the same domain the news feed uses, and one that 301-redirects to Prism's meta server. With the
    /// dead host, a fresh install could fetch no metadata and every resolution failed; the redirect is
    /// followed because the shared HttpClient handler allows it (see ProxyFactory).
    /// </remarks>
    public string MetaUrl { get; init; } = "https://extremelauncher.net/_api/meta-v1/";

    public string ResourceBase { get; init; } = "https://resources.download.minecraft.net/";

    public string LibraryBase { get; init; } = "https://libraries.minecraft.net/";

    public string ImgurBaseUrl { get; init; } = "https://api.imgur.com/3/";

    /// <summary>The CDN for the legacy Feed The Beast pack lists and archives, from CMakeLists'
    /// Launcher_LEGACY_FTB_CDN_BASE_URL. The static pack lists live at <c>static/modpacks.xml</c> and
    /// <c>static/thirdparty.xml</c> under this base.</summary>
    public string LegacyFtbCdnBaseUrl { get; init; } = "https://dist.creeper.host/FTB2/";

    /// <summary>ATLauncher's download CDN — pack lists, images, pack configs and server-hosted mods live
    /// under this base. From BuildConfig's ATL_DOWNLOAD_SERVER_URL.</summary>
    public string AtlDownloadServerUrl { get; init; } = "https://download.nodecdn.net/containers/atl/";

    /// <summary>ATLauncher's API base, used for share codes. From BuildConfig's ATL_API_BASE_URL.</summary>
    public string AtlApiBaseUrl { get; init; } = "https://api.atlauncher.com/v1/";

    /// <summary>Where the news toolbar fetches from.</summary>
    /// <remarks>
    /// The fork's own CMake default (Launcher_NEWS_RSS_URL). Called RSS everywhere upstream -- the
    /// download job is even named "News RSS Feed" -- but what the server serves is Atom, and Atom is
    /// what NewsEntry::fromXmlElement actually reads. The name is the only thing that is wrong.
    ///
    /// CMake also defines Launcher_NEWS_OPEN_URL, described as "URL that gets opened when the user
    /// clicks 'More News'". NOTHING IN UPSTREAM READS IT -- More News opens the dialog -- so it is
    /// not carried over here.
    /// </remarks>
    public string NewsRssUrl { get; init; } = "https://extremelauncher.net/_api/news.xml";

    /// <summary>Where the Help / wiki link goes.</summary>
    /// <remarks>
    /// This was empty, which suppressed the Help menu entry entirely — but the fork configures
    /// Launcher_HELP_URL as "https://extremelauncher.net/_api/help/%1/", and that endpoint is live.
    /// Upstream's only caller, on_actionOpenWiki, is HELP_URL.arg("") — it always substitutes an
    /// EMPTY page, so the "%1/" collapses to nothing and the toolbar Help button opens the help root.
    /// The port has no per-page help, so it stores that already-resolved root directly rather than
    /// carrying a "%1" template with one possible argument.
    /// </remarks>
    public string HelpUrl { get; init; } = "https://extremelauncher.net/_api/help/";

    public string BugTrackerUrl { get; init; } = "https://github.com/ExtremeLauncherTeam/ExtremeLauncher/issues";

    /// <summary>Where "Translate" points.</summary>
    /// <remarks>
    /// The fork translates on Weblate (Launcher_TRANSLATIONS_URL). This had
    /// "https://github.com/ExtremeLauncher/Translations" — an invented URL, on an org
    /// ("ExtremeLauncher") that is not even the one the bug tracker uses ("ExtremeLauncherTeam"), and
    /// it 404s. Corrected to the fork's configured Weblate project. Nothing consumes it beyond the
    /// links menu yet — the translation machinery itself is not ported — and the project page is not
    /// populated at the time of writing, but the value now matches the source of truth.
    /// </remarks>
    public string TranslationsUrl { get; init; } = "https://hosted.weblate.org/projects/extremelauncher/launcher/";

    /*
     * COMMUNITY LINKS, EMPTY BY DEFAULT, and that is upstream's own choice -- CMakeLists.txt leaves
     * Launcher_DISCORD_URL, Launcher_MATRIX_URL and Launcher_SUBREDDIT_URL as empty strings for a
     * fork to fill in. The UI hides an entry whose URL is empty rather than offering a link to
     * nowhere, which is also what upstream does.
     */
    public string DiscordUrl { get; init; } = string.Empty;

    public string MatrixUrl { get; init; } = string.Empty;

    public string SubredditUrl { get; init; } = string.Empty;

    public string LoginCallbackUrl { get; init; } = "http://127.0.0.1:*/";

    // ---------------------------------------------------------------- credentials

    /// <summary>Microsoft account client id. Empty disables Microsoft login.</summary>
    /// <remarks>See the file header: never commit a real value here.</remarks>
    public string MsaClientId { get; init; } = string.Empty;

    /// <summary>CurseForge API key. Empty disables CurseForge browsing and imports.</summary>
    /// <remarks>See the file header: never commit a real value here.</remarks>
    public string FlameApiKey { get; init; } = string.Empty;

    /// <summary>Imgur client id, used for screenshot uploads. Empty disables them.</summary>
    public string ImgurClientId { get; init; } = string.Empty;

    // ---------------------------------------------------------------- derived helpers

    public bool IsMicrosoftLoginAvailable => MsaClientId.Length != 0;

    public bool IsCurseForgeAvailable => FlameApiKey.Length != 0;

    public bool IsScreenshotUploadAvailable => ImgurClientId.Length != 0;

    /// <summary>True when this build claims to be the upstream official one.</summary>
    public bool IsOfficial => string.Equals(BuildPlatform, "official", StringComparison.Ordinal);
}
