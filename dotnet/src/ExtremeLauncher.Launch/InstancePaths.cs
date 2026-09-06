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
 * Ported from the path accessors of launcher/minecraft/MinecraftInstance.cpp — gameRoot, binRoot,
 * getNativePath, getLocalLibraryPath, resourcesDir, getClassPath and getNativeJars.
 *
 * Everything an instance keeps on disk, derived from its root directory. Split out from the 1,256-line
 * MinecraftInstance because the launch pipeline needs the layout and nothing else that class owns.
 *
 * THE minecraft/ VERSUS .minecraft/ RULE is inherited and load-bearing. MultiMC originally used a
 * visible "minecraft" folder; later versions use ".minecraft" to match the vanilla launcher. Existing
 * instances have either. The rule is: prefer ".minecraft" ONLY when it exists and "minecraft" does
 * not — so an instance with both, or with neither, resolves to "minecraft". Getting this backwards
 * would point a migrated instance at an empty world folder.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class InstancePaths
{
    public InstancePaths(string instanceRoot) => InstanceRoot = FileSystem.CleanPath(Path.GetFullPath(instanceRoot));

    /// <summary>The instance directory itself, holding mmc-pack.json and the game folder.</summary>
    public string InstanceRoot { get; }

    /// <summary>
    /// The game's working directory — its saves, options and mods.
    /// </summary>
    /// <remarks>See the file header: ".minecraft" wins only when "minecraft" is absent.</remarks>
    public string GameRoot
    {
        get
        {
            var visible = FileSystem.PathCombine(InstanceRoot, "minecraft");
            var hidden = FileSystem.PathCombine(InstanceRoot, ".minecraft");

            return Directory.Exists(hidden) && !Directory.Exists(visible) ? hidden : visible;
        }
    }

    /// <summary>Where a jar-modded Minecraft jar is assembled.</summary>
    public string BinRoot => FileSystem.PathCombine(GameRoot, "bin");

    /// <summary>Scratch directory for unpacked natives. Disposable; cleared after each launch.</summary>
    public string NativesPath => FileSystem.PathCombine(InstanceRoot, "natives");

    /// <summary>Libraries belonging to this instance alone, rather than the shared store.</summary>
    public string LocalLibraryPath => FileSystem.PathCombine(InstanceRoot, "libraries");

    /// <summary>Pre-1.7.3 asset tree, when the index says to map assets to resources.</summary>
    public string ResourcesDir => FileSystem.PathCombine(GameRoot, "resources");

    /// <summary>Local overrides for components, one JSON per uid.</summary>
    public string PatchesDir => FileSystem.PathCombine(InstanceRoot, "patches");

    /// <summary>Jar mods belonging to this instance. Beside the game folder, not inside it.</summary>
    public string JarModsDir => FileSystem.PathCombine(InstanceRoot, "jarmods");

    /// <summary>
    /// Where old Forge looks for its loose libraries.
    /// </summary>
    /// <remarks>
    /// INSIDE the game folder, unlike everything else here — a 1.3-to-1.5 era arrangement where FML
    /// resolved a handful of jars by filename rather than from the classpath. See FMLLibrariesTask.
    /// </remarks>
    public string LibDir => FileSystem.PathCombine(GameRoot, "lib");

    public string PackProfilePath => FileSystem.PathCombine(InstanceRoot, "mmc-pack.json");

    public string ConfigPath => FileSystem.PathCombine(InstanceRoot, "instance.cfg");

    /// <summary>The classpath jars for a resolved profile.</summary>
    public List<string> GetClassPath(LaunchProfile profile, RuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(profile);

        List<string> jars = [], natives = [];
        profile.GetLibraryFiles(runtimeContext, jars, natives, LocalLibraryPath, BinRoot);

        return jars;
    }

    /// <summary>The native jars that need unpacking before launch.</summary>
    public List<string> GetNativeJars(LaunchProfile profile, RuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(profile);

        List<string> jars = [], natives = [];
        profile.GetLibraryFiles(runtimeContext, jars, natives, LocalLibraryPath, BinRoot);

        return natives;
    }

    public override string ToString() => InstanceRoot;
}

/// <summary>Assembles the inputs a launch needs from an instance and its resolved profile.</summary>
/// <remarks>
/// This is the seam MinecraftInstance occupies upstream. Keeping it a plain factory means the launch
/// pipeline never has to know where settings come from.
/// </remarks>
public static class LaunchOptionsFactory
{
    public static LaunchOptions Create(
        InstancePaths paths,
        LaunchProfile profile,
        string instanceName,
        string sharedAssetsDirectory,
        Java.JavaVersion javaVersion,
        int minMemoryMegabytes = 512,
        int maxMemoryMegabytes = 4096,
        int permGenMegabytes = 64,
        IReadOnlyList<string>? customJvmArguments = null,
        bool applyOnlineFixes = false,
        string customJvmArgumentsText = "")
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profile);

        // The game_assets token wants the reconstructed tree for old versions and the shared store
        // for everything else; the index decides which, so resolve it here rather than at use.
        var gameAssets = sharedAssetsDirectory;

        if (profile.MinecraftAssets is { Id.Length: > 0 } assets
            && AssetsIndex.Load(sharedAssetsDirectory, assets.Id) is { } index)
        {
            gameAssets = index.GetAssetsDir(sharedAssetsDirectory, paths.ResourcesDir);
        }

        return new LaunchOptions
        {
            InstanceName = instanceName,
            GameDirectory = paths.GameRoot,
            AssetsDirectory = sharedAssetsDirectory,
            GameAssetsDirectory = gameAssets,
            NativesDirectory = paths.NativesPath,
            LocalLibraryPath = paths.LocalLibraryPath,
            MinMemoryMegabytes = minMemoryMegabytes,
            MaxMemoryMegabytes = maxMemoryMegabytes,
            PermGenMegabytes = permGenMegabytes,
            // The settings box holds one string, so it is split shell-style here rather than by every
            // caller. See Commandline.SplitArgs — quoting a Windows path in there destroys it.
            CustomJvmArguments = customJvmArgumentsText.Length != 0
                ? Commandline.SplitArgs(customJvmArgumentsText)
                : customJvmArguments ?? [],
            JavaVersion = javaVersion,
            ApplyOnlineFixes = applyOnlineFixes,
        };
    }

    /// <summary>Builds the standard step sequence for a launch.</summary>
    /// <remarks>
    /// THE ORDER IS UPSTREAM'S, from MinecraftInstance::createLaunchTask, and it is not the order that
    /// looks obvious. Java is verified LATE — after the jar is modded and the natives are unpacked —
    /// rather than first. An earlier draft of this port put VerifyJavaInstall first on "fail before
    /// anything is written" reasoning, which is wrong twice over: the pipeline's reverse unwind cleans
    /// up regardless, so nothing is actually left behind either way, and the compatible-major list the
    /// check reads is produced by the component update that runs before all of this. Log ordering is
    /// also observable to anyone comparing a pasted log against the Qt launcher's.
    ///
    /// Not represented here, because they belong to parts of the launcher that are not ported yet:
    /// LookupServerAddress, PreLaunchCommand/PostLaunchCommand, MinecraftLoadAndCheck, AutoInstallJava,
    /// ScanModFolders, ReconstructAssets and QuitAfterGameStop.
    /// </remarks>
    public static LaunchPipeline CreatePipeline(
        InstancePaths paths,
        LaunchProfile profile,
        RuntimeContext runtimeContext,
        LaunchOptions options,
        string javaPath,
        AuthSession? session = null,
        LaunchTarget? target = null,
        bool ignoreJavaCompatibility = false,
        MinecraftAccount? account = null,
        IReadOnlyList<JarMod>? jarMods = null,
        IReadOnlyList<string>? instanceDescription = null,
        string wrapperCommand = "",
        IReadOnlyList<string>? feedServers = null,
        string preLaunchCommand = "",
        string postExitCommand = "",
        string instanceId = "")
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        var pipeline = new LaunchPipeline($"Launch {options.InstanceName}");

        /*
         * The $INST_* variables both the pre-launch and post-exit commands can use. Built once here
         * where every ingredient is to hand -- INST_JAVA_ARGS is the user's own JVM args, the closest
         * faithful match to upstream's javaArguments() without rebuilding the whole java command line.
         */
        var variables = LaunchVariables.Build(
            options.InstanceName,
            instanceId,
            paths.InstanceRoot,
            paths.GameRoot,
            javaPath,
            string.Join(' ', options.CustomJvmArguments));

        /*
         * PRE-LAUNCH RUNS FIRST, before anything touches the instance, which is upstream's placement
         * and the only one that makes "back up before you let me play" mean what it says. A non-zero
         * exit aborts the whole launch.
         */
        if (preLaunchCommand.Trim().Length != 0)
        {
            pipeline.AddStep(new RunCommandStep("Pre-Launch", preLaunchCommand, variables, paths.GameRoot));
        }

        /*
         * The feed's servers are this fork's own patch: addresses out of the news feed, merged into
         * every instance's multiplayer list at every launch. Empty in a build with no feed, and empty
         * whenever the feed has not loaded yet -- so a launch never waits on it.
         */
        pipeline.AddStep(new CreateGameFolders(paths.GameRoot, feedServers));

        // Held for the whole launch, so a token refresh cannot invalidate the running game's session.
        if (session is not null)
        {
            pipeline.AddStep(new ClaimAccount(session, account));
        }

        if (jarMods is { Count: > 0 })
        {
            pipeline.AddStep(new ModMinecraftJar(paths.BinRoot, MainJarPath(paths, profile, runtimeContext), jarMods));
        }

        // Upstream scans the mod folders between ModMinecraftJar and PrintInstanceInfo, so the
        // mod list lands in the log just above the instance description.
        pipeline.AddStep(new ScanModFolders(paths.GameRoot));

        pipeline.AddStep(new PrintInstanceInfo(instanceDescription ?? []));

        pipeline.AddStep(new ExtractNativesStep(
            paths.GetNativeJars(profile, runtimeContext),
            paths.NativesPath,
            options.JavaVersion));

        pipeline.AddStep(new VerifyJavaInstall(
            options.JavaVersion,
            profile.CompatibleJavaMajors,
            runtimeContext.JavaArchitecture,
            options.MaxMemoryMegabytes,
            ignoreJavaCompatibility));

        var arguments = LaunchCommandBuilder.BuildCommandLine(profile, runtimeContext, options, session, target);

        pipeline.AddStep(new LauncherPartLaunch(javaPath, arguments, paths.GameRoot, environment: null, wrapperCommand));

        /*
         * POST-EXIT RUNS LAST, after the game has gone. It runs even after a crash, because
         * LauncherPartLaunch reports a bad exit rather than throwing -- so cleanup a user set up
         * ("sync my saves when I am done") happens whether the session ended well or badly.
         */
        if (postExitCommand.Trim().Length != 0)
        {
            pipeline.AddStep(new RunCommandStep("Post-Exit", postExitCommand, variables, paths.GameRoot));
        }

        return pipeline;
    }

    /// <summary>The version's own jar, which jar modding uses as its source.</summary>
    private static string MainJarPath(InstancePaths paths, LaunchProfile profile, RuntimeContext runtimeContext)
    {
        if (profile.MainJar is not { } mainJar)
        {
            return string.Empty;
        }

        List<string> jars = [], native = [], native32 = [], native64 = [];
        mainJar.GetApplicableFiles(runtimeContext, jars, native, native32, native64, paths.LocalLibraryPath);

        return jars.Count != 0 ? jars[0] : string.Empty;
    }
}

/// <summary>Wraps native extraction as a pipeline step, cleaning the directory up afterwards.</summary>
public sealed class ExtractNativesStep : LaunchStep
{
    private readonly NativesExtractor _extractor;
    private readonly string _targetDirectory;

    public ExtractNativesStep(IReadOnlyList<string> nativeJars, string targetDirectory, Java.JavaVersion javaVersion)
        : base("Extract natives")
    {
        _extractor = new NativesExtractor(nativeJars, targetDirectory, javaVersion);
        _targetDirectory = targetDirectory;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!await _extractor.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_extractor.State == TaskState.AbortedByUser)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            LogLine(_extractor.FailReason, MessageLevel.Error);
            throw new TaskFailedException(_extractor.FailReason);
        }
    }

    /// <summary>The natives directory is scratch space and does not survive the launch.</summary>
    public override Task FinalizeAsync()
    {
        NativesExtractor.Cleanup(_targetDirectory);
        return Task.CompletedTask;
    }
}
