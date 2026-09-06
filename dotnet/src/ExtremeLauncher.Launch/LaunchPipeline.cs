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
 * Ported from launcher/launch/{LaunchStep,LaunchTask}.{h,cpp} and the simpler steps in
 * launcher/minecraft/launch/.
 *
 * Launching is a sequence of steps: check the JVM, make the folders, unpack natives, then start the
 * process. Each step can log, and each gets a chance to clean up afterwards.
 *
 * FINALIZERS RUN IN REVERSE, which is upstream's design and worth keeping: the natives directory is
 * created by an early step and deleted by its finalizer, so unwinding in reverse means nothing is torn
 * down while a later step still depends on it.
 *
 * The step base is a LauncherTask, so sequencing is SequentialTask from wave 2 rather than the
 * hand-rolled state machine upstream needs. LaunchTask's own ~270 lines are mostly that machine plus
 * Qt model plumbing for the log window.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>One stage of a launch.</summary>
public abstract class LaunchStep : LauncherTask
{
    protected LaunchStep(string name) : base(name)
    {
    }

    public event EventHandler<LogLinesEventArgs>? LogLines;

    /// <summary>
    /// Undoes whatever the step set up. Called in reverse order after the launch ends, and only for
    /// steps that actually ran.
    /// </summary>
    public virtual Task FinalizeAsync() => Task.CompletedTask;

    protected void LogLine(string line, MessageLevel level = MessageLevel.Launcher)
        => LogLines?.Invoke(this, new LogLinesEventArgs([line], level));

    protected void Log(IReadOnlyList<string> lines, MessageLevel level)
        => LogLines?.Invoke(this, new LogLinesEventArgs(lines, level));
}

/// <summary>Refuses to launch on a JVM the profile declares incompatible.</summary>
public sealed class VerifyJavaInstall : LaunchStep
{
    private readonly JavaVersion _javaVersion;
    private readonly IReadOnlyList<int> _compatibleMajors;
    private readonly string _javaArchitecture;
    private readonly int _maxMemoryMegabytes;
    private readonly bool _ignoreCompatibility;

    public VerifyJavaInstall(
        JavaVersion javaVersion,
        IReadOnlyList<int> compatibleMajors,
        string javaArchitecture = "64",
        int maxMemoryMegabytes = 4096,
        bool ignoreCompatibility = false)
        : base("Verify Java installation")
    {
        _javaVersion = javaVersion;
        _compatibleMajors = compatibleMajors;
        _javaArchitecture = javaArchitecture;
        _maxMemoryMegabytes = maxMemoryMegabytes;
        _ignoreCompatibility = ignoreCompatibility;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // A 32-bit JVM cannot address more than 2 GiB, so a larger setting simply fails to start.
        // Warned about rather than corrected, because the user chose both values.
        if (_javaArchitecture == "32" && _maxMemoryMegabytes > 2048)
        {
            LogLine(
                "Max memory allocation exceeds the supported value.\n"
                + "The selected installation of Java is 32-bit and doesn't support more than 2048MiB of RAM.\n"
                + "The instance may not start due to this.",
                MessageLevel.Error);
        }

        // An empty list means the profile expresses no opinion, which is the common case.
        if (_compatibleMajors.Count == 0 || _compatibleMajors.Contains(_javaVersion.Major))
        {
            return Task.CompletedTask;
        }

        if (_ignoreCompatibility)
        {
            LogLine("Java major version is incompatible. Things might break.", MessageLevel.Warning);
            return Task.CompletedTask;
        }

        LogLine(
            $"This instance is not compatible with Java version {_javaVersion.Major}.\n"
            + "Please switch to one of the following Java versions for this instance:",
            MessageLevel.Error);

        foreach (var major in _compatibleMajors)
        {
            LogLine($"Java version {major}", MessageLevel.Error);
        }

        LogLine(
            "Go to instance Java settings to change your Java version or disable the Java compatibility "
            + "check if you know what you're doing.",
            MessageLevel.Error);

        throw new TaskFailedException("Incompatible Java major version");
    }
}

/// <summary>Creates the folders the game expects to already exist, and adds the fork's servers.</summary>
/// <remarks>
/// THE SERVER LIST HALF IS A FORK-LOCAL PATCH, marked in upstream's own source with
/// "// uncommited - start" and "// uncommited - end": the addresses in the news feed's
/// &lt;serverlistentry&gt; elements are merged into EVERY instance's servers.dat at EVERY launch. So
/// the launcher puts entries in the player's multiplayer list, and what it puts there comes off the
/// network.
///
/// Ported because it is this fork's behaviour and parity is the goal, with three changes:
///
///   IT REFUSES TO WRITE A LIST IT COULD NOT READ. Upstream's parseServersDat returns null on any
///   exception -- including a servers.dat in a shape it does not expect -- and the code then carries
///   on and writes a fresh list containing ONLY the fork's servers. That is a silent, total loss of
///   the player's multiplayer list, and it is the whole reason ServerList.TryLoad exists.
///
///   IT SAYS WHAT IT DID, in the launch log. A launcher that quietly edits a game file should at
///   minimum leave a record; upstream's version is invisible.
///
///   IT NEVER TOUCHES AN EXISTING ENTRY. Upstream appends the new servers first and re-adds the
///   player's afterwards, which silently REORDERS their multiplayer list on the first launch. Here
///   the player's entries keep their order and anything new goes after them.
/// </remarks>
public sealed class CreateGameFolders : LaunchStep
{
    private readonly string _gameRoot;

    private readonly IReadOnlyList<string> _extraServers;

    /// <param name="extraServers">
    /// Addresses from the news feed, or empty. Empty is the normal case: a build with no feed, or a
    /// feed that has not loaded by the time somebody presses Launch.
    /// </param>
    public CreateGameFolders(string gameRoot, IReadOnlyList<string>? extraServers = null)
        : base("Create game folders")
    {
        _gameRoot = gameRoot;
        _extraServers = extraServers ?? [];
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!FileSystem.EnsureFolderPathExists(_gameRoot))
        {
            LogLine("Couldn't create the main game folder", MessageLevel.Error);
            throw new TaskFailedException("Couldn't create the main game folder");
        }

        // HACK, upstream's: works around MCL-3732, where the game fails if this folder is absent.
        // Not fatal — the game only needs it when a server sends a resource pack.
        if (!FileSystem.EnsureFolderPathExists(FileSystem.PathCombine(_gameRoot, "server-resource-packs")))
        {
            LogLine("Couldn't create the 'server-resource-packs' folder", MessageLevel.Error);
        }

        AddTheForksServers();

        return Task.CompletedTask;
    }

    private void AddTheForksServers()
    {
        if (_extraServers.Count == 0)
        {
            return;
        }

        if (!ServerList.TryLoad(_gameRoot, out var existing))
        {
            /*
             * THE CASE UPSTREAM GETS WRONG. Its parseServersDat returns null here and the code writes
             * a list holding only the fork's servers -- replacing everything the player had. Adding
             * an advertisement is not worth losing somebody's server list over, so this does nothing
             * and says so.
             */
            LogLine(
                "This instance's server list could not be read, so it has been left exactly as it is.",
                MessageLevel.Warning);

            return;
        }

        // Matched on address, which is what upstream compares -- a player who renamed one of these
        // must not get a duplicate.
        var known = existing
            .Select(s => s.Address)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = _extraServers
            .Where(address => address.Length != 0 && known.Add(address))
            .ToList();

        if (added.Count == 0)
        {
            return;
        }

        // AFTER the player's own, so a list they have arranged is not silently reordered.
        existing.AddRange(added.Select(address => new MinecraftServer
        {
            Name = "Minecraft Server",
            Address = address,
        }));

        try
        {
            ServerList.Save(_gameRoot, existing);

            LogLine(
                $"Added {added.Count} server(s) from the launcher's news feed to this instance's "
                + $"multiplayer list: {string.Join(", ", added)}",
                MessageLevel.Launcher);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never fatal. Failing a launch because an advertisement could not be written would be
            // absurd.
            LogLine("Couldn't update this instance's server list: " + e.Message, MessageLevel.Warning);
        }
    }
}

/// <summary>Starts the JVM and waits for the game to exit.</summary>
public sealed class LauncherPartLaunch : LaunchStep
{
    private readonly string _javaPath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly string _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly string _wrapperCommand;

    private LoggedProcess? _process;

    public LauncherPartLaunch(
        string javaPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        string wrapperCommand = "")
        : base("Launch game")
    {
        _javaPath = javaPath;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
        _environment = environment;
        _wrapperCommand = wrapperCommand.Trim();
    }

    public override bool CanAbort => true;

    /// <summary>The process's exit code, once it has exited.</summary>
    public int ExitCode { get; private set; }

    public LoggedProcess.ProcessState FinalState { get; private set; } = LoggedProcess.ProcessState.NotRunning;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var process = new LoggedProcess();
        _process = process;

        // Forward the game's output as the step's own, so one log stream covers the whole launch.
        process.Log += (_, e) => Log(e.Lines, e.Level);

        var (program, arguments) = ResolveWrapper();

        LogLine($"Launching with: {program}", MessageLevel.Launcher);

        process.Start(program, arguments, _workingDirectory, _environment);

        if (process.State == LoggedProcess.ProcessState.FailedToStart)
        {
            FinalState = LoggedProcess.ProcessState.FailedToStart;
            throw new TaskFailedException($"Could not launch Java: {_javaPath}");
        }

        FinalState = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        ExitCode = process.ExitCode;

        // A crash is reported, not thrown: the game ran, and the user wants the log, not a launcher
        // error. Upstream keeps the window open on a bad exit for the same reason.
        if (FinalState == LoggedProcess.ProcessState.Crashed)
        {
            LogLine($"Game exited abnormally with code {ExitCode}.", MessageLevel.Error);
        }
    }

    /// <summary>
    /// Works out what to actually start, given an optional wrapper command.
    /// </summary>
    /// <remarks>
    /// A wrapper runs the JVM as its own child — <c>prime-run</c>, <c>gamemoderun</c>, <c>mangohud</c>
    /// and the like. The user types a whole command line into a settings box, so it goes through the
    /// shell-style splitter: the first token is the program and the rest become arguments ahead of the
    /// java path, which is itself pushed in front of the game's own arguments.
    ///
    /// A wrapper that cannot be found FAILS THE LAUNCH rather than being skipped. Silently starting
    /// without it would run the game on the wrong GPU, or without the frame limiter the user set up,
    /// with nothing in the log to say why.
    /// </remarks>
    private (string Program, IReadOnlyList<string> Arguments) ResolveWrapper()
    {
        if (_wrapperCommand.Length == 0)
        {
            return (_javaPath, _arguments);
        }

        var parts = Commandline.SplitArgs(_wrapperCommand);

        if (parts.Count == 0)
        {
            return (_javaPath, _arguments);
        }

        var wrapper = parts[0];

        if (FileSystem.ResolveExecutable(wrapper).Length == 0)
        {
            LogLine($"The wrapper command \"{wrapper}\" couldn't be found.", MessageLevel.Fatal);
            throw new TaskFailedException($"The wrapper command \"{wrapper}\" couldn't be found.");
        }

        LogLine($"Wrapper command is:\n{_wrapperCommand}\n", MessageLevel.Launcher);

        return (wrapper, [.. parts[1..], _javaPath, .. _arguments]);
    }

    public void Kill() => _process?.Kill();
}

/// <summary>
/// Runs a user-configured command before the game starts or after it exits.
/// </summary>
/// <remarks>
/// Ported from launcher/launch/steps/PreLaunchCommand.cpp and PostLaunchCommand.cpp — the same step
/// with two labels. The command is a whole line the user typed into a settings box, so its $INST_*
/// variables are substituted, then it goes through the shell-style splitter.
///
/// A NON-ZERO EXIT FAILS THE STEP, for both kinds, which is upstream's behaviour. For a pre-launch
/// command that means the game never starts — and that is the entire reason to write one: "back up my
/// world, and if the backup fails, do not let me play and overwrite it." The variables are also put
/// in the process environment, so a script can read $INST_MC_DIR without the launcher substituting it.
/// </remarks>
public sealed class RunCommandStep : LaunchStep
{
    private readonly string _label;
    private readonly string _command;
    private readonly IReadOnlyDictionary<string, string> _variables;
    private readonly string _workingDirectory;

    private LoggedProcess? _process;

    public RunCommandStep(
        string label,
        string command,
        IReadOnlyDictionary<string, string> variables,
        string workingDirectory)
        : base($"{label} command")
    {
        ArgumentNullException.ThrowIfNull(variables);

        _label = label;
        _command = (command ?? string.Empty).Trim();
        _variables = variables;
        _workingDirectory = workingDirectory;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_command.Length == 0)
        {
            // Nothing configured. The pipeline only adds this step when there is a command, but a
            // whitespace-only setting reaches here and is a no-op rather than an empty-args error.
            return;
        }

        var substituted = LaunchVariables.Substitute(_command, _variables);
        var parts = Commandline.SplitArgs(substituted);

        if (parts.Count == 0)
        {
            return;
        }

        var program = parts[0];

        // Found the same way the wrapper command is, and failing the same way: a command the user set
        // that is not there is a mistake worth stopping for, not skipping past.
        if (FileSystem.ResolveExecutable(program).Length == 0)
        {
            var missing = $"The {_label} command \"{program}\" couldn't be found.";
            LogLine(missing, MessageLevel.Fatal);
            throw new TaskFailedException(missing);
        }

        LogLine($"Running {_label} command: {substituted}", MessageLevel.Launcher);

        using var process = new LoggedProcess();
        _process = process;

        // The command's output is the launcher's, so it lands in the same log as everything else.
        process.Log += (_, e) => Log(e.Lines, e.Level);

        process.Start(program, [.. parts[1..]], _workingDirectory, _variables);

        if (process.State == LoggedProcess.ProcessState.FailedToStart)
        {
            var error = $"{_label} command failed to start.";
            LogLine(error, MessageLevel.Fatal);
            throw new TaskFailedException(error);
        }

        var state = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (state != LoggedProcess.ProcessState.Finished || process.ExitCode != 0)
        {
            var error = $"{_label} command failed with code {process.ExitCode}.";
            LogLine(error, MessageLevel.Fatal);
            throw new TaskFailedException(error);
        }

        LogLine($"{_label} command ran successfully.", MessageLevel.Launcher);
    }

    public void Kill() => _process?.Kill();
}

/// <summary>
/// A whole launch: run each step in order, then unwind their finalizers in reverse.
/// </summary>
public sealed class LaunchPipeline : LauncherTask
{
    private readonly List<LaunchStep> _steps = [];
    private readonly List<LaunchStep> _ran = [];

    public LaunchPipeline(string name = "Launch") : base(name)
    {
    }

    public event EventHandler<LogLinesEventArgs>? LogLines;

    public IReadOnlyList<LaunchStep> Steps => _steps;

    public override bool CanAbort => true;

    public LaunchPipeline AddStep(LaunchStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        // One log stream for the whole launch, whichever step is speaking.
        step.LogLines += (_, e) => LogLines?.Invoke(this, e);

        _steps.Add(step);
        return this;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _ran.Clear();

        try
        {
            for (var i = 0; i < _steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = _steps[i];

                SetStatus(step.Name);
                SetProgress(i, _steps.Count);

                // Recorded before running, so a step that fails partway still gets its finalizer.
                _ran.Add(step);

                if (!await step.RunAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (step.State == TaskState.AbortedByUser)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new TaskFailedException($"{step.Name} failed: {step.FailReason}");
                }
            }

            SetProgress(_steps.Count, _steps.Count);
        }
        finally
        {
            await FinalizeStepsAsync().ConfigureAwait(false);
        }
    }

    /// <remarks>
    /// Reverse order, and every finalizer runs even if an earlier one throws — a failure while
    /// cleaning up must not strand the rest of the teardown.
    /// </remarks>
    private async Task FinalizeStepsAsync()
    {
        for (var i = _ran.Count - 1; i >= 0; i--)
        {
            try
            {
                await _ran[i].FinalizeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TaskFailedException)
            {
                LogLines?.Invoke(
                    this,
                    new LogLinesEventArgs([$"Cleanup of '{_ran[i].Name}' failed: {e.Message}"], MessageLevel.Warning));
            }
        }
    }
}
