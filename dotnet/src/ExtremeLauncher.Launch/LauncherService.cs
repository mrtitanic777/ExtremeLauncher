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
 * THE PATH FROM AN INSTANCE ID TO A RUNNING GAME, in one place, for both front-ends.
 *
 * This logic was written first inside the CLI, where it proved the port end to end. Leaving it there
 * and writing it again for the window would produce two launchers that agree until they do not -- and
 * the ways they would disagree are exactly the ways that matter: which JVM gets picked, whether an
 * offline launch is allowed, what happens to a component that will not resolve. The CLI now calls
 * this too, so the two cannot drift.
 *
 * NOTHING HERE PRINTS OR SHOWS A DIALOG. Progress arrives through a reporter, so the CLI can write it
 * to a terminal and the window can put it in a status bar, and neither has to know about the other.
 * That is the same reason the tasks underneath report progress rather than drawing it.
 *
 * FAILURES ARE EXCEPTIONS, not return codes. Every step below fails in the same shape -- a
 * LauncherException carrying a sentence meant for a person -- because the alternative is each caller
 * re-deriving "did that work?" from a different bool and getting it subtly wrong.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.Net;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

// Aliased: ExtremeLauncher.Meta.Index collides with System.Index, which is implicitly in scope.
using MetaIndex = ExtremeLauncher.Meta.Index;

namespace ExtremeLauncher.Launch;

/// <summary>How a launch reports itself. Implemented by whichever front-end asked for it.</summary>
public interface ILaunchReporter
{
    /// <summary>What is happening now, in a few words. Replaces the previous status.</summary>
    void Status(string status);

    /// <summary>How far along the current step is. <paramref name="total"/> is 0 when unknown.</summary>
    void Progress(long current, long total);

    /// <summary>One line for the log: the game's own output, and the launcher's.</summary>
    void Line(string text, bool isError = false);
}

/// <summary>A reporter that discards everything, for a caller that does not want any of it.</summary>
public sealed class NullLaunchReporter : ILaunchReporter
{
    public static readonly NullLaunchReporter Instance = new();

    public void Status(string status)
    {
    }

    public void Progress(long current, long total)
    {
    }

    public void Line(string text, bool isError = false)
    {
    }
}

/// <summary>Everything a launch needs that is not already on disk.</summary>
public sealed record LaunchRequest
{
    public required string InstanceId { get; init; }

    /// <summary>The metadata server. Only consulted for what the cache does not already hold.</summary>
    public string MetaUrl { get; init; } = BuildConfig.Instance.MetaUrl;

    /// <summary>Resolve from the cache alone, without reaching the network.</summary>
    public bool Offline { get; init; }

    /// <summary>A specific interpreter, or empty to use the instance's setting or a probed one.</summary>
    public string JavaPath { get; init; } = string.Empty;

    /// <summary>The offline player name. Ignored when <see cref="Account"/> is set.</summary>
    public string PlayerName { get; init; } = "Player";

    /// <summary>
    /// Who is playing, or null to launch offline under <see cref="PlayerName"/>.
    /// </summary>
    /// <remarks>
    /// The caller is expected to have refreshed it already -- see AccountRefresh -- because getting a
    /// fresh access token needs a network round trip and possibly a sign-in prompt, and neither
    /// belongs underneath a launch. What arrives here is used as it stands.
    /// </remarks>
    public MinecraftAccount? Account { get; init; }

    /// <summary>A server to join on start, as "host" or "host:port", or null.</summary>
    public string? Server { get; init; }

    /// <summary>A world to open on start, or null. <see cref="Server"/> takes precedence over it.</summary>
    /// <remarks>
    /// They are mutually exclusive in the game, which takes one <c>--quickPlay</c> target. Upstream
    /// resolves the same way when both are given -- <em>server</em> first (Application.cpp,
    /// <c>if (!m_serverToJoin.isEmpty()) ... else if (world)</c>) -- rather than refusing the combination.
    /// </remarks>
    public string? World { get; init; }

    /// <summary>Resolve and download everything, then stop before starting the game.</summary>
    public bool DryRun { get; init; }
}

/// <summary>What a launch produced.</summary>
/// <param name="Started">False for a dry run, which stops before the JVM.</param>
/// <param name="JavaPath">The interpreter that was chosen.</param>
/// <param name="CommandLine">The arguments the JVM was, or would have been, given.</param>
/// <param name="ExitCode">The game's exit code, or null when it was never started.</param>
public sealed record LaunchResult(
    bool Started,
    string JavaPath,
    IReadOnlyList<string> CommandLine,
    int? ExitCode);

public sealed class LauncherService
{
    private readonly LauncherPaths _paths;

    private readonly HttpClient? _client;

    /// <param name="client">
    /// Null makes the service strictly offline: it can still start anything already downloaded, and
    /// fails on anything that is not.
    /// </param>
    public LauncherService(LauncherPaths paths, HttpClient? client)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _client = client;
    }

    /// <summary>Server addresses out of the news feed, merged into an instance's list at launch.</summary>
    /// <remarks>
    /// THIS FORK'S OWN PATCH -- see CreateGameFolders. Settable rather than a constructor argument
    /// because the feed loads long after the service is built, and a launch must never wait on it:
    /// empty here simply means the news has not arrived yet, and the next launch will do it.
    ///
    /// Upstream holds the same thing in a global (`inline QStringList serverList` in Application.h).
    /// </remarks>
    public IReadOnlyList<string> FeedServers { get; set; } = [];

    /// <summary>The data directory this service works in.</summary>
    public LauncherPaths Paths => _paths;

    /// <summary>Reads the instance list off disk.</summary>
    public InstanceList OpenInstances()
    {
        _paths.EnsureExists();

        var list = new InstanceList(_paths.Instances, GlobalSettings.Create(_paths.LauncherConfig));

        list.LoadList();

        return list;
    }

    /// <summary>
    /// Resolves an instance's component list into a flattened launch profile.
    /// </summary>
    /// <remarks>
    /// The whole chain the port was built for: PackProfile out of mmc-pack.json, then
    /// ComponentUpdateTask against the meta server, then a flattened LaunchProfile.
    /// </remarks>
    public async Task<(InstanceRecord Instance, LaunchProfile Profile)> ResolveAsync(
        LaunchRequest request,
        ILaunchReporter reporter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reporter);

        if (request.InstanceId.Length == 0)
        {
            throw new LauncherException("No instance given.");
        }

        var instance = OpenInstances().GetInstanceById(request.InstanceId)
            ?? throw new LauncherException($"No instance called '{request.InstanceId}'.");

        /*
         * Refused here rather than left to fail later. An unsupported instance is one the launcher can
         * read enough of to list and not enough to run, and the useful moment to say so is before
         * anything has been downloaded on its behalf.
         */
        if (!instance.IsSupported)
        {
            throw new LauncherException(
                $"Instance '{request.InstanceId}' is of an unsupported type ('{instance.Settings.InstanceType}').");
        }

        var profile = new PackProfile(CurrentRuntimeContext());

        if (!profile.Load(instance.Paths.PackProfilePath))
        {
            throw new LauncherException($"Could not read {instance.Paths.PackProfilePath}.");
        }

        var index = new MetaIndex();
        var mode = request.Offline || _client is null ? NetMode.Offline : NetMode.Online;

        var update = new ComponentUpdateTask(
            profile,
            index,
            instance.Paths.PatchesDir,
            ComponentUpdateMode.Launch,
            mode,
            CreateVersionLoader(request, index, mode, reporter));

        Report(update, reporter);

        if (!await update.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new LauncherException($"Could not resolve the version: {update.FailReason}");
        }

        /*
         * Problems are WARNINGS, not failures. A component can be missing an optional dependency and
         * still produce a playable game, and refusing to start over one would make the launcher less
         * useful than editing the JSON by hand.
         */
        foreach (var component in profile.Components)
        {
            foreach (var problem in component.GetProblems())
            {
                reporter.Line($"{component.Name}: {problem.Description}", isError: true);
            }
        }

        return (instance, profile.GetProfile());
    }

    /// <summary>
    /// Resolves, downloads and starts an instance, and returns when the game exits.
    /// </summary>
    /// <remarks>
    /// The order matters and is upstream's: resolve, then download, then choose a JVM, then launch.
    /// Choosing the JVM after resolving is the point -- the profile is what says which majors are
    /// acceptable, and picking first would mean picking blind.
    /// </remarks>
    public async Task<LaunchResult> LaunchAsync(
        LaunchRequest request,
        ILaunchReporter reporter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reporter);

        reporter.Status("Resolving version");

        var (instance, profile) = await ResolveAsync(request, reporter, cancellationToken).ConfigureAwait(false);

        // ---------------------------------------------------------------- download

        reporter.Status("Updating game files");

        var cache = _paths.CreateCache();
        var runtimeContext = CurrentRuntimeContext();

        await UpdateGameFilesAsync(instance, profile, runtimeContext, cache, reporter, cancellationToken)
            .ConfigureAwait(false);

        // ---------------------------------------------------------------- java

        var javaPath = request.JavaPath.Length != 0 ? request.JavaPath : instance.Settings.JavaPath;

        if (javaPath.Length == 0)
        {
            reporter.Status("Looking for Java");

            /*
             * THE LAUNCHER'S OWN RUNTIMES FIRST. Wave 22 could download a Java and nothing could find
             * it again: the candidate list came from JavaUtils.FindJavaPaths, which looks where a
             * SYSTEM Java lives and not in <data>/java. The download worked, the probe passed, and
             * pressing Play still said "no compatible Java installation found".
             */
            javaPath = await FindUsableJavaAsync(
                profile,
                instance.Settings.IgnoreJavaCompatibility,
                ManagedJava.Candidates(_paths.Java),
                cancellationToken).ConfigureAwait(false);
        }

        if (javaPath.Length == 0 && instance.Settings.AutomaticJavaDownload)
        {
            /*
             * AND ONLY THEN. Downloading a hundred megabytes is not something to do while a usable
             * Java is sitting on the machine, so this is reached exactly when the search has failed --
             * which is the state the setting is named for.
             */
            javaPath = await DownloadJavaForAsync(profile, request, reporter, cancellationToken)
                .ConfigureAwait(false);
        }

        if (javaPath.Length == 0)
        {
            var needs = profile.CompatibleJavaMajors.Count != 0
                ? $" (this instance needs Java {string.Join(" or ", profile.CompatibleJavaMajors)})"
                : string.Empty;

            // The remedy is named, because "no compatible Java" tells somebody what is wrong and
            // nothing about what to do next.
            throw new LauncherException(
                $"No compatible Java installation found{needs}. "
                + "Install one from Settings, or turn on automatic Java downloads.");
        }

        var javaVersion = await ProbeJavaAsync(javaPath, reporter, cancellationToken).ConfigureAwait(false);

        // ---------------------------------------------------------------- launch

        var options = LaunchOptionsFactory.Create(
            instance.Paths,
            profile,
            instance.Name,
            _paths.Assets,
            javaVersion,
            instance.Settings.MinMemAlloc,
            instance.Settings.MaxMemAlloc,
            instance.Settings.PermGen,
            applyOnlineFixes: instance.Settings.OnlineFixes,
            customJvmArgumentsText: instance.Settings.JvmArgs);

        /*
         * WHO IS PLAYING. With no account this is the offline path it always was; with one, the
         * session carries a real access token and an online-mode server will let the player in.
         *
         * wantsOnline follows the account rather than the request's Offline flag, which is about
         * whether to reach the network for METADATA. They are genuinely different questions: resolving
         * a version from cache does not stop a signed-in player joining a server.
         */
        var account = request.Account ?? MinecraftAccount.CreateOffline(request.PlayerName);
        var session = account.CreateSession(wantsOnline: account.AccountType != AccountType.Offline);

        // Server first when both are set, matching upstream (Application.cpp). The rule lives in
        // LaunchTarget.Choose so the CLI and the window cannot disagree on which one wins.
        var target = LaunchTarget.Choose(request.Server, request.World);
        var commandLine = LaunchCommandBuilder.BuildCommandLine(profile, runtimeContext, options, session, target);

        if (request.DryRun)
        {
            return new LaunchResult(Started: false, javaPath, commandLine, ExitCode: null);
        }

        var pipeline = LaunchOptionsFactory.CreatePipeline(
            instance.Paths,
            profile,
            runtimeContext,
            options,
            javaPath,
            session,
            target,
            // The instance's own override. Without it a 1.20.1 instance refuses to start on anything
            // but Java 17, which is upstream's default but not upstream's only option.
            ignoreJavaCompatibility: instance.Settings.IgnoreJavaCompatibility,
            account: account,
            wrapperCommand: instance.Settings.WrapperCommand,
            feedServers: FeedServers,

            // User-configured hooks around the game. Empty for almost everyone; when set, a pre-launch
            // command that fails stops the launch, and a post-exit command runs even after a crash.
            preLaunchCommand: instance.Settings.PreLaunchCommand,
            postExitCommand: instance.Settings.PostExitCommand,
            instanceId: request.InstanceId);

        // The game's own output and the launcher's on one stream -- which is what the log window shows
        // and what a user pastes into a bug report.
        pipeline.LogLines += (_, e) =>
        {
            foreach (var line in e.Lines)
            {
                // Fatal as well as Error: both are failures worth showing in red, and a crash line is
                // usually the Fatal one. Now that levels are guessed from the game's own output, a
                // crash on stdout reaches here as Error/Fatal rather than plain StdOut.
                reporter.Line(line, e.Level is MessageLevel.Error or MessageLevel.Fatal);
            }
        };

        pipeline.StatusChanged += (_, status) => reporter.Status(status);

        if (!await pipeline.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new LauncherException(pipeline.FailReason);
        }

        /*
         * The exit code belongs to the step that owns the process, not to the pipeline, which only
         * knows whether every step succeeded. Asked for by type rather than by position so that
         * inserting a step ahead of it cannot silently start reporting the wrong number.
         */
        var exitCode = pipeline.Steps.OfType<LauncherPartLaunch>().LastOrDefault()?.ExitCode;

        return new LaunchResult(Started: true, javaPath, commandLine, exitCode);
    }

    /// <summary>Downloads the folders, libraries and assets the profile calls for.</summary>
    private async Task UpdateGameFilesAsync(
        InstanceRecord instance,
        LaunchProfile profile,
        RuntimeContext runtimeContext,
        HttpMetaCache cache,
        ILaunchReporter reporter,
        CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            // Nothing can be fetched, but everything may already be here. FoldersTask still runs,
            // since creating directories needs no network.
            var folders = new FoldersTask(instance.Paths);

            Report(folders, reporter);

            if (!await folders.RunAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new LauncherException(folders.FailReason);
            }

            return;
        }

        foreach (var task in new LauncherTask[]
                 {
                     new FoldersTask(instance.Paths),
                     new LibrariesTask(instance.Paths, profile, runtimeContext, _client, cache, instance.Name),
                     new AssetUpdateTask(profile, _client, cache, _paths.Assets, instanceName: instance.Name),
                 })
        {
            Report(task, reporter);

            if (!await task.RunAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new LauncherException(task.FailReason);
            }
        }

        // Written out now rather than at exit: a launcher killed with the game still running would
        // otherwise lose every etag it just learned and re-download the lot next time.
        cache.SaveNow();
    }

    private Func<string, string, CancellationToken, Task<bool>>? CreateVersionLoader(
        LaunchRequest request,
        MetaIndex index,
        NetMode mode,
        ILaunchReporter reporter)
    {
        if (_client is null)
        {
            return null;
        }

        return async (uid, version, token) =>
        {
            var task = index.CreateLoadVersionTask(
                uid, version, _client, _paths.CreateCache(), _paths.Meta, request.MetaUrl, mode);

            if (await task.RunAsync(token).ConfigureAwait(false))
            {
                return index.GetOrCreate(uid, version).Data is not null;
            }

            // Surfaced rather than swallowed: "load failed" with no reason is the least useful thing a
            // launcher can say about a metadata problem.
            reporter.Line(task.FailReason, isError: true);

            return false;
        };
    }

    /// <summary>
    /// Picks an installed JVM whose major version the profile accepts.
    /// </summary>
    /// <remarks>
    /// PROBED, NOT GUESSED FROM THE PATH. A folder called "jre-1.8.0" may hold anything at all, and
    /// starting a modern pack on a Java 8 that claimed to be 17 fails with a stack trace nobody can act
    /// on. With no stated requirement the first candidate is taken unprobed -- there is nothing to
    /// check it against.
    /// </remarks>
    /// <param name="allowIncompatible">
    /// The instance's IgnoreJavaCompatibility setting. When nothing acceptable is found it falls back
    /// to the first candidate instead of refusing.
    ///
    /// WITHOUT THIS THE SETTING IS ONLY HALF HONOURED: it reaches VerifyJavaInstall inside the
    /// pipeline, but selection would already have refused, so a user who ticked "ignore compatibility"
    /// still could not start the game. Found by running the CLI against a machine with Java 21 and 25
    /// and a pack asking for 17 -- which is the ordinary state of a machine, not a contrived one.
    /// </param>
    /// <param name="candidates">The JVMs to consider. Defaults to the ones installed on this machine.</param>
    public static async Task<string> FindUsableJavaAsync(
        LaunchProfile profile,
        bool allowIncompatible = false,
        IReadOnlyList<string>? candidates = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        candidates ??= JavaUtils.FindJavaPaths();

        if (profile.CompatibleJavaMajors.Count == 0)
        {
            return candidates.Count != 0 ? candidates[0] : string.Empty;
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var checker = new JavaChecker(candidate);

            if (!await checker.RunAsync(cancellationToken).ConfigureAwait(false)
                || checker.Result.Validity != JavaCheckValidity.Valid)
            {
                continue;
            }

            if (profile.CompatibleJavaMajors.Contains(new JavaVersion(checker.Result.JavaVersion).Major))
            {
                return candidate;
            }
        }

        /*
         * The fallback is UNPROBED, like the no-requirement branch above: whatever is wrong with it,
         * VerifyJavaInstall reports it against a real launch rather than as a refusal to start. The
         * user asked for compatibility to be ignored; refusing anyway would be answering a different
         * question.
         */
        return allowIncompatible && candidates.Count != 0 ? candidates[0] : string.Empty;
    }

    /// <summary>
    /// Fetches a Java the instance can use, when the search found none.
    /// </summary>
    /// <returns>The path to the new java binary, or empty when it could not be had.</returns>
    private async Task<string> DownloadJavaForAsync(
        LaunchProfile profile,
        LaunchRequest request,
        ILaunchReporter reporter,
        CancellationToken cancellationToken)
    {
        if (_client is null || profile.CompatibleJavaMajors.Count == 0)
        {
            // With no requirement there is nothing to pick, and any Java would already have matched.
            return string.Empty;
        }

        /*
         * THE HIGHEST MAJOR THE INSTANCE ACCEPTS. A profile that takes 17 or 21 works with either, and
         * the newer one is the one still receiving fixes.
         */
        var wanted = profile.CompatibleJavaMajors.Max();

        reporter.Line($"No Java {wanted} found. Downloading one…");

        try
        {
            var source = new JavaRuntimeSource(_paths, _client, request.MetaUrl);

            var available = await source.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            /*
             * A JRE IS PREFERRED OVER A JDK: it is a third of the size and the game needs nothing a
             * JDK adds. Mojang's build is preferred over the rest, because it is what the game is
             * tested against -- JavaRuntimeSource already orders vendors that way.
             */
            var pick = available.FirstOrDefault(j => j.Major == wanted && !j.IsJdk)
                ?? available.FirstOrDefault(j => j.Major == wanted);

            if (pick is null)
            {
                reporter.Line($"No Java {wanted} is available for this machine.", isError: true);

                return string.Empty;
            }

            reporter.Line($"Installing {pick.DisplayName}");

            var task = source.CreateInstallTask(pick);

            task.ProgressChanged += (_, p) => reporter.Progress(p.Current, p.Total);
            task.StatusChanged += (_, text) => reporter.Status(text);

            if (!await task.RunAsync(cancellationToken).ConfigureAwait(false))
            {
                reporter.Line($"Could not install Java: {task.FailReason}", isError: true);

                return string.Empty;
            }

            // Found by searching rather than derived: the two download types unpack to different
            // shapes, and ManagedJava.Find already knows how to handle both.
            var folder = FileSystem.PathCombine(_paths.Java, JavaRuntimeSource.FolderNameFor(pick));
            var installed = ManagedJava.Find(folder);

            if (installed.Count == 0)
            {
                reporter.Line("Java was downloaded but no java binary was found in it.", isError: true);

                return string.Empty;
            }

            reporter.Line($"Installed Java {wanted} into {folder}");

            return installed[0];
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or IOException)
        {
            // Reported and carried on from: the launch fails with the "no compatible Java" message
            // below, which now names the remedy.
            reporter.Line($"Could not download Java: {e.Message}", isError: true);

            return string.Empty;
        }
    }

    /// <summary>Asks a JVM what it is.</summary>
    /// <remarks>
    /// An unreadable answer is reported and carried on from, not refused: the JVM may still run the
    /// game, and VerifyJavaInstall inside the pipeline has its own say about compatibility.
    /// </remarks>
    public static async Task<JavaVersion> ProbeJavaAsync(
        string javaPath,
        ILaunchReporter reporter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reporter);

        var checker = new JavaChecker(javaPath);

        if (await checker.RunAsync(cancellationToken).ConfigureAwait(false)
            && checker.Result.Validity == JavaCheckValidity.Valid)
        {
            reporter.Line(
                $"Java {checker.Result.JavaVersion} ({checker.Result.JavaVendor}, {checker.Result.MojangPlatform}-bit)");

            return new JavaVersion(checker.Result.JavaVersion);
        }

        reporter.Line($"Could not determine the version of {javaPath}.", isError: true);

        return new JavaVersion();
    }

    /// <summary>The runtime this launcher is running on, in the vocabulary the rules use.</summary>
    public static RuntimeContext CurrentRuntimeContext()
    {
        var architecture = SysInfo.CurrentArchitecture();

        return new RuntimeContext
        {
            System = SysInfo.CurrentSystem(),

            // The rules speak in "64"/"32"; the real architecture is kept alongside for the ${arch}
            // substitution in native classifiers.
            JavaArchitecture = architecture is "x86_64" or "arm64" ? "64" : "32",
            JavaRealArchitecture = architecture,
        };
    }

    /// <summary>Forwards a task's progress and status to the reporter.</summary>
    private static void Report(LauncherTask task, ILaunchReporter reporter)
    {
        task.StatusChanged += (_, status) => reporter.Status(status);
        task.ProgressChanged += (_, progress) => reporter.Progress(progress.Current, progress.Total);
    }
}
