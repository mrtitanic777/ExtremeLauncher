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
 * Ported from the QCommandLineParser block at the top of launcher/Application.cpp.
 *
 * THIS IS A COMPATIBILITY SURFACE, not a convenience. Desktop entries, Steam shortcuts, .desktop
 * files, batch files and other launchers invoke this lineage as `launcher --launch <id> --server
 * <address>`, and every one of those breaks if an option is renamed or stops taking a value. So the
 * short forms are here too, and they are upstream's letters rather than nicer ones.
 *
 * PARSING LIVES HERE, AWAY FROM AVALONIA, so it can be tested. Upstream's lives inside the
 * Application constructor, where testing it means constructing a QApplication -- which is exactly why
 * the "--server without --launch" check below has no test upstream despite being pure logic.
 */

namespace ExtremeLauncher.Launch;

/// <summary>What the launcher was asked to do on the command line.</summary>
public sealed record LauncherArguments
{
    /// <summary>The data directory, or null for the portable default beside the executable.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>
    /// A metadata server for this run, or null.
    /// </summary>
    /// <remarks>
    /// NOT AN UPSTREAM OPTION. Upstream configures this only through the MetaURLOverride setting and
    /// its API settings page. This port's CLI already had --meta, there is no settings page yet, and
    /// the built-in default points at a domain that does not resolve -- so without this the window has
    /// no way at all to reach a metadata server. Precedence is: this, then the setting, then the
    /// build default.
    /// </remarks>
    public string? MetaUrl { get; init; }

    /// <summary>An instance to start immediately, or empty.</summary>
    public string InstanceIdToLaunch { get; init; } = string.Empty;

    /// <summary>A server to join on start. Only meaningful with <see cref="InstanceIdToLaunch"/>.</summary>
    public string ServerToJoin { get; init; } = string.Empty;

    /// <summary>A world to open on start. Only meaningful with <see cref="InstanceIdToLaunch"/>.</summary>
    public string WorldToJoin { get; init; } = string.Empty;

    /// <summary>An account profile name to use. Only meaningful with <see cref="InstanceIdToLaunch"/>.</summary>
    public string ProfileToUse { get; init; } = string.Empty;

    /// <summary>An instance whose window should be opened.</summary>
    public string InstanceIdToShow { get; init; } = string.Empty;

    /// <summary>Write a small file once the launcher is up, so a script can tell that it started.</summary>
    public bool LiveCheck { get; init; }

    /// <summary>Resources to import: <c>--import</c> values and bare positional arguments alike.</summary>
    public IReadOnlyList<string> UrlsToImport { get; init; } = [];

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }

    /// <summary>What is wrong with the arguments, or empty if nothing is.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Options this build parses but cannot yet act on.</summary>
    /// <remarks>
    /// NAMED RATHER THAN IGNORED. An option that is accepted and silently does nothing is worse than
    /// one that is rejected: a script that passes <c>--profile</c> would appear to work while starting
    /// the game as somebody else. These are reported at startup.
    /// </remarks>
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    /// <summary>The usage text, matching upstream's option list.</summary>
    public static string Usage =>
        """
        usage: ExtremeLauncher [options] [URL...]

          -d, --dir <directory>   Use a custom path as application root ('.' for the current directory).
              --meta <url>         Use this metadata server for this run.
          -l, --launch <instance>  Launch the specified instance (by instance ID).
          -s, --server <address>   Join the specified server on launch (only with --launch).
          -w, --world <world>      Join the specified world on launch (only with --launch).
          -a, --profile <profile>  Use the account with this profile name (only with --launch).
              --alive              Write a small 'live.check' file after the launcher starts.
          -I, --import <url>       Import an instance or resource from a local path or URL.
              --show <instance>    Open the window for the specified instance.
          -h, --help               Show this help.
          -V, --version            Show the version.

        Bare arguments are treated as URLs to import, the same as -I.
        """;

    /// <summary>
    /// Parses a command line.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a general-purpose parser. It accepts what upstream accepts and no more: an
    /// option that takes a value takes the next argument, and there is no <c>--option=value</c> form
    /// because QCommandLineParser's callers here never relied on one.
    /// </remarks>
    public static LauncherArguments Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return new LauncherArguments();
        }

        string? dataDirectory = null;
        string? metaUrl = null;
        var launch = string.Empty;
        var server = string.Empty;
        var world = string.Empty;
        var profile = string.Empty;
        var show = string.Empty;
        var alive = false;
        var help = false;
        var version = false;
        var imports = new List<string>();
        var unsupported = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];

            // The value of an option that takes one, or null when it is the last argument.
            string? Value() => i + 1 < args.Count ? args[++i] : null;

            switch (argument)
            {
                case "-d" or "--dir":
                    dataDirectory = Value();
                    break;

                case "--meta":
                    metaUrl = Value();
                    break;

                case "-l" or "--launch":
                    launch = Value() ?? string.Empty;
                    break;

                case "-s" or "--server":
                    server = Value() ?? string.Empty;
                    break;

                case "-w" or "--world":
                    world = Value() ?? string.Empty;
                    break;

                case "-a" or "--profile":
                    profile = Value() ?? string.Empty;
                    unsupported.Add("--profile (no account UI yet; the offline session is always used)");
                    break;

                case "--show":
                    show = Value() ?? string.Empty;
                    unsupported.Add("--show (there is no per-instance window yet)");
                    break;

                case "--alive":
                    alive = true;
                    break;

                case "-I" or "--import":
                    if (Value() is { } url)
                    {
                        imports.Add(url);
                    }

                    unsupported.Add("--import (the import subsystem is not wired to the window yet)");
                    break;

                case "-h" or "--help":
                    help = true;
                    break;

                case "-V" or "--version":
                    version = true;
                    break;

                default:
                    /*
                     * Bare arguments are import URLs, as upstream has it -- that is what makes the
                     * launcher work as a handler for a .mrpack double-click. An unknown option is NOT
                     * treated as one, because silently importing "--typo" helps nobody.
                     */
                    if (argument.StartsWith('-'))
                    {
                        return new LauncherArguments { Error = $"Unknown option '{argument}'." };
                    }

                    imports.Add(argument);

                    if (unsupported.Count == 0 || !unsupported[^1].StartsWith("--import", StringComparison.Ordinal))
                    {
                        unsupported.Add("importing (the import subsystem is not wired to the window yet)");
                    }

                    break;
            }
        }

        /*
         * Upstream's own check, kept: --server, --world and --profile only mean anything alongside
         * --launch, and accepting them alone would start the launcher normally while the user waited
         * for a game to appear.
         */
        var error = (server.Length != 0 || world.Length != 0 || profile.Length != 0) && launch.Length == 0
            ? "--server, --world and --profile are only valid in combination with --launch."
            : string.Empty;

        return new LauncherArguments
        {
            DataDirectory = dataDirectory,
            MetaUrl = metaUrl,
            InstanceIdToLaunch = launch,
            ServerToJoin = server,
            WorldToJoin = world,
            ProfileToUse = profile,
            InstanceIdToShow = show,
            LiveCheck = alive,
            UrlsToImport = imports,
            ShowHelp = help,
            ShowVersion = version,
            Unsupported = unsupported,
            Error = error,
        };
    }
}
