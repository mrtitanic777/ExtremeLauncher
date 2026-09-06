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
 * THE HEADLESS LAUNCHER. PORTING.md's own plan says: "After wave 7 you have a launcher that can start
 * Minecraft headlessly. That proves the port before a month goes into UI." This is that proof.
 *
 * It is deliberately thin. Every decision it makes lives in a library with tests around it; this file
 * only wires them together and prints things. If a command here needs logic, the logic belongs in the
 * library and the test belongs beside it.
 *
 * NOT A REPLACEMENT for the UI. There is no instance creation, no mod management and no account
 * sign-in, because those are the parts that genuinely need an interface. What is here is the path from
 * "an instance exists on disk" to "the game is running", which is the part that had never been run end
 * to end.
 */

using System.Globalization;
using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;

// Aliased: ExtremeLauncher.Meta.Index collides with System.Index, which is implicitly in scope.
using MetaIndex = ExtremeLauncher.Meta.Index;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var arguments = new List<string>(args);
        var dataDirectory = TakeOption(arguments, "--dir");

        var metaOverride = TakeOption(arguments, "--meta");

        var paths = new LauncherPaths(dataDirectory);

        /*
         * THE WORKING DIRECTORY IS PART OF THE CONTRACT, not housekeeping. Library storage paths are
         * relative ("libraries/org/lwjgl/...") and become absolute against the process directory, so
         * the classpath handed to the JVM silently points at wherever the launcher happened to be
         * started from. Upstream does the same chdir for the same reason (Application.cpp).
         */
        // Only the root, not the whole layout: "help" and "version" have no business creating an
        // instances folder. OpenInstances still builds the rest when something actually needs it.
        Directory.CreateDirectory(paths.Root);
        Directory.SetCurrentDirectory(paths.Root);

        // The setting, then the build default -- the same order the window uses, so the two front-ends
        // talk to the same server when neither is told otherwise.
        var metaUrl = metaOverride
            ?? GlobalSettings.ResolveMetaUrl(GlobalSettings.Create(paths.LauncherConfig));

        /*
         * The launcher's own log, alongside the console. The CLI already prints everything, but a run
         * that ends in a closed terminal leaves nothing behind -- and this is the same file the window
         * writes, so a bug report from either front-end reads the same.
         */
        using var log = LauncherLog.Open(paths.Root);

        log.Info($"{BuildConfig.Instance.LauncherDisplayName} {BuildConfig.Instance.VersionString} (headless)");
        log.Info($"Command: {string.Join(' ', args)}");

        var command = arguments.Count != 0 ? arguments[0] : "help";

        /*
         * ONE CLIENT AND ONE SERVICE for the whole run. Every command that touches the network shares
         * them, which is also what keeps connections pooled across the hundreds of library downloads a
         * cold instance needs.
         */
        using var client = CreateClient();

        var service = new LauncherService(paths, client);

        try
        {
            return command switch
            {
                "list" => List(service),
                // Flag first: Rest() consumes everything left, so it must not still be in there.
                "info" => await InfoCommandAsync(service, arguments, metaUrl).ConfigureAwait(false),
                "launch" => await LaunchAsync(service, arguments, metaUrl, log).ConfigureAwait(false),
                "java" => Java(),
                "version" => Version(),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command),
            };
        }
        catch (LauncherException e)
        {
            // The library's own failures carry a message meant for a person; anything else is a bug
            // and should keep its stack trace.
            Console.Error.WriteLine($"error: {e.Message}");
            log.Error(e.Message);

            return 1;
        }
    }

    // ================================================================== commands

    private static int Help()
    {
        Console.WriteLine($"""
            {BuildConfig.Instance.LauncherDisplayName} {BuildConfig.Instance.VersionString} (headless)

            usage: extremelauncher [--dir <data directory>] <command>

              list                    List the instances in the data directory.
              info <instance>         Show what an instance is made of.
              launch <instance>       Resolve, download and start an instance.
                --offline               Resolve from the metadata cache, without the network.
                --name <player>         Offline player name. Defaults to Player.
                --java <path>           Use this interpreter instead of the configured one.
                --server <address>      Join a server on start.
                --world <name>          Open a singleplayer world on start. A --server wins over this.
                --dry-run               Resolve and download, then print the command line and stop.
              --meta <url>            Use a different metadata server.
              java                    List the Java installations found on this machine.
              version                 Print the version.

            The data directory defaults to the folder holding this executable, which is what makes a
            portable install work. Point --dir at an existing Prism or MultiMC folder to use its
            instances.
            """);

        return 0;
    }

    private static int Version()
    {
        Console.WriteLine($"{BuildConfig.Instance.LauncherDisplayName} {BuildConfig.Instance.VersionString}");
        Console.WriteLine($"platform: {SysInfo.CurrentSystem()}-{SysInfo.CurrentArchitecture()}");
        Console.WriteLine($"runtime:  {Environment.Version}");

        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'. Try 'help'.");
        return 2;
    }

    private static int List(LauncherService service)
    {
        var paths = service.Paths;
        var list = service.OpenInstances();

        if (list.Count == 0)
        {
            Console.WriteLine($"No instances in {paths.Instances}");
            return 0;
        }

        // Grouped the way the UI would show them, so the output matches what a user expects to see.
        foreach (var group in list.Instances.GroupBy(i => list.GetInstanceGroup(i.Id)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Key.Length != 0)
            {
                Console.WriteLine($"[{group.Key}]");
            }

            foreach (var instance in group.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var flag = instance.IsSupported ? " " : "!";
                Console.WriteLine($" {flag} {instance.Id,-28} {instance.Name}");
            }
        }

        return 0;
    }

    private static Task<int> InfoCommandAsync(LauncherService service, List<string> arguments, string metaUrl)
    {
        var listLibraries = TakeFlag(arguments, "--libraries");

        return InfoAsync(service, Rest(arguments), metaUrl, listLibraries);
    }

    private static async Task<int> InfoAsync(
        LauncherService service,
        string id,
        string metaUrl,
        bool listLibraries)
    {
        // Fetches if it has to. Resolving a version needs its metadata whether the user asked to play
        // or only to look, and a cold cache is the normal state the first time an instance is opened.
        var request = new LaunchRequest { InstanceId = id, MetaUrl = metaUrl };

        var (instance, profile) = await service
            .ResolveAsync(request, ConsoleReporter.Instance)
            .ConfigureAwait(false);

        Console.WriteLine($"id:        {instance.Id}");
        Console.WriteLine($"name:      {instance.Name}");
        Console.WriteLine($"path:      {instance.Paths.InstanceRoot}");
        Console.WriteLine($"minecraft: {profile.MinecraftVersion}");
        Console.WriteLine($"main:      {profile.MainClass}");

        if (profile.CompatibleJavaMajors.Count != 0)
        {
            Console.WriteLine($"java:      {string.Join(", ", profile.CompatibleJavaMajors)}");
        }

        Console.WriteLine($"libraries: {profile.Libraries.Count} ({profile.NativeLibraries.Count} native)");

        /*
         * Listing the resolved URLs is the only way to see what the component stack actually decided:
         * which libraries this platform's rules kept, and whether each one carries its own artifact URL
         * or fell back to deriving one from the Maven coordinate. A download that 404s is nearly always
         * one of those two answers being wrong.
         */
        if (listLibraries)
        {
            var cache = service.Paths.CreateCache();
            var runtimeContext = LauncherService.CurrentRuntimeContext();

            using var client = CreateClient();

            foreach (var library in profile.Libraries.Concat(profile.NativeLibraries))
            {
                var urls = library.GetDownloads(
                    runtimeContext, client, cache, [], instance.Paths.LocalLibraryPath);

                Console.WriteLine($"  {library.Name.Serialize()}");

                foreach (var url in urls)
                {
                    Console.WriteLine($"      {url.Url}");
                }
            }
        }

        var mods = Minecraft.Mods.ResourceFolder.LoadAllMods(instance.Paths.GameRoot);

        if (mods.Count != 0)
        {
            Console.WriteLine($"mods:      {mods.Count}");
        }

        return 0;
    }

    private static int Java()
    {
        var found = JavaUtils.FindJavaPaths();

        if (found.Count == 0)
        {
            Console.WriteLine("No Java installations found.");
            return 0;
        }

        // NOT VALIDATED: FindJavaPaths deliberately does not run its candidates, so this is a list of
        // places a JVM might be. Use 'launch' to have one actually probed.
        foreach (var path in found)
        {
            Console.WriteLine(path);
        }

        return 0;
    }

    private static async Task<int> LaunchAsync(
        LauncherService service,
        List<string> arguments,
        string metaUrl,
        LauncherLog log)
    {
        var request = ParseLaunchRequest(arguments, metaUrl);

        /*
         * Ctrl-C STOPS THE LAUNCH rather than killing the launcher out from under a running game.
         * Cancel() lets the pipeline unwind its steps, which is what removes the extracted natives.
         */
        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            /*
             * Reported to the terminal and to the launcher's log at once. The terminal is what the user
             * is watching; the log is what they can send afterwards.
             */
            var reporter = new LoggingLaunchReporter(ConsoleReporter.Instance, log);

            var result = await service
                .LaunchAsync(request, reporter, cancellation.Token)
                .ConfigureAwait(false);

            if (!result.Started)
            {
                Console.WriteLine($"{result.JavaPath} {string.Join(' ', result.CommandLine)}");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled.");
            return 130;
        }
    }

    /// <summary>Builds a launch request from the flags left after "launch". Side-effect-free, so tests
    /// can pin the argument handling without a network, a data directory or a game.</summary>
    /// <remarks>
    /// The flags are pulled out BEFORE <see cref="Rest"/> reads the instance id, so a stray "--dry-run"
    /// is consumed as the flag it is and never mistaken for the id. Order among the flags themselves
    /// does not matter: each <see cref="TakeOption"/>/<see cref="TakeFlag"/> finds its own name.
    /// </remarks>
    internal static LaunchRequest ParseLaunchRequest(List<string> arguments, string metaUrl)
    {
        /*
         * --offline is A FLAG, not an option with a value. It used to take the player name directly,
         * which reads fine until someone writes "--offline --dry-run" and silently launches as a player
         * called "--dry-run" with the dry run never happening. An option that eats whatever follows it
         * cannot tell a name from the next flag, so the name moved to its own --name option.
         */
        var offline = TakeFlag(arguments, "--offline");
        var offlineName = TakeOption(arguments, "--name");

        // Note --offline governs METADATA, not the account: without a UI there is no way to sign in,
        // so the session is always an offline one either way.
        var javaOverride = TakeOption(arguments, "--java");
        var server = TakeOption(arguments, "--server");

        // A --server wins over a --world when both are given; LauncherService, not the CLI, enforces
        // that, so both are passed through untouched.
        var world = TakeOption(arguments, "--world");
        var dryRun = TakeFlag(arguments, "--dry-run");

        return new LaunchRequest
        {
            InstanceId = Rest(arguments),
            MetaUrl = metaUrl,
            Offline = offline,
            JavaPath = javaOverride ?? string.Empty,
            PlayerName = offlineName ?? "Player",
            Server = server,
            World = world,
            DryRun = dryRun,
        };
    }

    /// <summary>Writes a launch's progress to the terminal.</summary>
    /// <remarks>
    /// Status goes to stdout with the "==>" marker the rest of the CLI uses; everything flagged as an
    /// error goes to stderr, so "extremelauncher launch pack &gt; log.txt" still shows failures.
    ///
    /// PROGRESS IS DROPPED, deliberately. A percentage that repaints in place needs a terminal, and one
    /// that does not repaint scrolls thousands of lines past anything worth reading. The window has a
    /// progress bar; the CLI has the status line.
    /// </remarks>
    private sealed class ConsoleReporter : ILaunchReporter
    {
        public static readonly ConsoleReporter Instance = new();

        public void Status(string status) => Console.WriteLine($"==> {status}");

        public void Progress(long current, long total)
        {
        }

        public void Line(string text, bool isError = false)
            => (isError ? Console.Error : Console.Out).WriteLine(text);
    }


    // ================================================================== wiring

    /// <summary>An HTTP client that identifies itself, which the meta and CDN servers expect.</summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildConfig.Instance.UserAgent);

        return client;
    }

    // ================================================================== argument handling

    /// <summary>Takes a <c>--name value</c> pair out of the argument list.</summary>
    internal static string? TakeOption(List<string> arguments, string name)
    {
        var index = arguments.IndexOf(name);

        if (index < 0 || index + 1 >= arguments.Count)
        {
            return null;
        }

        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);

        return value;
    }

    internal static bool TakeFlag(List<string> arguments, string name) => arguments.Remove(name);

    /// <summary>Everything after the command name, joined -- so an instance id may contain spaces.</summary>
    internal static string Rest(List<string> arguments)
        => arguments.Count > 1 ? string.Join(' ', arguments.Skip(1)) : string.Empty;
}
