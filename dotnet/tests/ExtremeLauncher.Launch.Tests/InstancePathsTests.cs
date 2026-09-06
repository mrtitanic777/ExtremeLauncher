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
 * Characterization tests for the instance layout. No upstream Qt test exists for these accessors, but
 * the minecraft/ versus .minecraft/ rule decides which folder an existing instance's saves live in,
 * so it is pinned in every combination.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstancePathsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-paths-" + Guid.NewGuid().ToString("N"));

    public InstancePathsTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private InstancePaths NewInstance(string name, bool visible = false, bool hidden = false)
    {
        var root = Path.Combine(_temp, name);
        Directory.CreateDirectory(root);

        if (visible)
        {
            Directory.CreateDirectory(Path.Combine(root, "minecraft"));
        }

        if (hidden)
        {
            Directory.CreateDirectory(Path.Combine(root, ".minecraft"));
        }

        return new InstancePaths(root);
    }

    // ================================================================== the game folder rule

    [Fact]
    public void HiddenWinsOnlyWhenVisibleIsAbsent()
    {
        // A migrated instance: only ".minecraft" exists.
        Assert.EndsWith(".minecraft", NewInstance("migrated", hidden: true).GameRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleWinsWhenBothExist()
    {
        // Both present — pointing at ".minecraft" here would hand the game an empty world folder.
        var root = NewInstance("both", visible: true, hidden: true).GameRoot;

        Assert.EndsWith("minecraft", root, StringComparison.Ordinal);
        Assert.DoesNotContain(".minecraft", FileSystem.CleanPath(root), StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleIsTheDefaultWhenNeitherExists()
    {
        var root = NewInstance("fresh").GameRoot;

        Assert.EndsWith("minecraft", root, StringComparison.Ordinal);
        Assert.DoesNotContain(".minecraft", FileSystem.CleanPath(root), StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleWinsWhenOnlyItExists()
        => Assert.DoesNotContain(
            ".minecraft",
            FileSystem.CleanPath(NewInstance("classic", visible: true).GameRoot),
            StringComparison.Ordinal);

    // ================================================================== the rest of the layout

    [Fact]
    public void SubdirectoriesHangOffTheRightRoots()
    {
        var paths = NewInstance("layout", hidden: true);

        // Natives and local libraries are per-instance but live beside the game folder, not inside
        // it — they are launcher bookkeeping rather than something the game should see.
        Assert.Equal(FileSystem.PathCombine(paths.InstanceRoot, "natives"), paths.NativesPath);
        Assert.Equal(FileSystem.PathCombine(paths.InstanceRoot, "libraries"), paths.LocalLibraryPath);
        Assert.Equal(FileSystem.PathCombine(paths.InstanceRoot, "patches"), paths.PatchesDir);

        // These belong to the game.
        Assert.Equal(FileSystem.PathCombine(paths.GameRoot, "bin"), paths.BinRoot);
        Assert.Equal(FileSystem.PathCombine(paths.GameRoot, "resources"), paths.ResourcesDir);
    }

    [Fact]
    public void ConfigAndPackProfileSitAtTheInstanceRoot()
    {
        var paths = NewInstance("files");

        Assert.EndsWith("mmc-pack.json", paths.PackProfilePath, StringComparison.Ordinal);
        Assert.EndsWith("instance.cfg", paths.ConfigPath, StringComparison.Ordinal);
    }

    // ================================================================== classpath

    private static LaunchProfile ProfileWith(params Library[] libraries)
    {
        var profile = new LaunchProfile();
        var patch = new VersionFile { Uid = "net.minecraft", Version = "1.20.1", MainClass = "Main" };

        foreach (var library in libraries)
        {
            patch.Libraries.Add(library);
        }

        patch.MainJar = new Library("com.mojang:minecraft:1.20.1:client");
        profile.Apply(patch, Context());

        return profile;
    }

    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    [Fact]
    public void ClassPathAndNativesAreSeparated()
    {
        var paths = NewInstance("cp");

        var native = new Library("org.lwjgl:lwjgl-platform:2.9.4");
        native.NativeClassifiers["linux"] = "natives-linux";

        var profile = ProfileWith(new Library("com.google.guava:guava:31.1-jre"), native);

        var classPath = paths.GetClassPath(profile, Context());
        var natives = paths.GetNativeJars(profile, Context());

        // The library plus the main jar; the native goes in the other list.
        Assert.Equal(2, classPath.Count);
        Assert.Single(natives);
        Assert.DoesNotContain(classPath, p => p.Contains("natives-linux", StringComparison.Ordinal));
    }

    // ================================================================== options and pipeline

    [Fact]
    public void OptionsAreAssembledFromTheInstanceLayout()
    {
        var paths = NewInstance("options", hidden: true);

        var options = LaunchOptionsFactory.Create(
            paths,
            ProfileWith(),
            "My Instance",
            Path.Combine(_temp, "assets"),
            new JavaVersion("17"));

        Assert.Equal("My Instance", options.InstanceName);
        Assert.Equal(paths.GameRoot, options.GameDirectory);
        Assert.Equal(paths.NativesPath, options.NativesDirectory);
        Assert.Equal(paths.LocalLibraryPath, options.LocalLibraryPath);
    }

    [Fact]
    public void GameAssetsFallsBackToTheSharedStoreWithoutAnIndex()
    {
        var paths = NewInstance("assets");
        var shared = Path.Combine(_temp, "assets");

        var options = LaunchOptionsFactory.Create(paths, ProfileWith(), "x", shared, new JavaVersion("17"));

        // No index on disk to consult, so the shared store stands in.
        Assert.Equal(shared, options.GameAssetsDirectory);
    }

    [Fact]
    public void TheStandardPipelineFollowsUpstreamsOrder()
    {
        var paths = NewInstance("pipeline");
        var profile = ProfileWith();

        var options = LaunchOptionsFactory.Create(
            paths, profile, "x", Path.Combine(_temp, "assets"), new JavaVersion("17"));

        var pipeline = LaunchOptionsFactory.CreatePipeline(
            paths, profile, Context(), options, javaPath: "/usr/bin/java");

        // Straight from MinecraftInstance::createLaunchTask, and NOT the order that looks obvious:
        // Java is verified LATE, after the natives are already unpacked. Log ordering is observable to
        // anyone comparing a pasted log against the Qt launcher's, and the compatible-major list the
        // check reads is produced by the component update that runs before any of this.
        Assert.Collection(
            pipeline.Steps,
            step => Assert.IsType<CreateGameFolders>(step),
            step => Assert.IsType<ScanModFolders>(step),
            step => Assert.IsType<PrintInstanceInfo>(step),
            step => Assert.IsType<ExtractNativesStep>(step),
            step => Assert.IsType<VerifyJavaInstall>(step),
            step => Assert.IsType<LauncherPartLaunch>(step));
    }

    [Fact]
    public void PreAndPostCommandsAreAddedOnlyWhenSet()
    {
        var paths = NewInstance("hooks");
        var profile = ProfileWith();

        var options = LaunchOptionsFactory.Create(
            paths, profile, "x", Path.Combine(_temp, "assets"), new JavaVersion("17"));

        // None set: the pipeline has neither hook.
        var plain = LaunchOptionsFactory.CreatePipeline(paths, profile, Context(), options, "/usr/bin/java");

        Assert.DoesNotContain(plain.Steps, step => step is RunCommandStep);

        var withHooks = LaunchOptionsFactory.CreatePipeline(
            paths, profile, Context(), options, "/usr/bin/java",
            preLaunchCommand: "echo before",
            postExitCommand: "echo after");

        var commandSteps = withHooks.Steps.Where(s => s is RunCommandStep).ToList();

        Assert.Equal(2, commandSteps.Count);
    }

    [Fact]
    public void PreLaunchRunsFirstAndPostExitRunsLast()
    {
        /*
         * The placement is the point: pre-launch before anything touches the instance, so "back up
         * before you let me play" means what it says; post-exit after the game step, so cleanup runs
         * once the session is over -- even after a crash, since LauncherPartLaunch reports rather than
         * throws on a bad exit.
         */
        var paths = NewInstance("hookorder");
        var profile = ProfileWith();

        var options = LaunchOptionsFactory.Create(
            paths, profile, "x", Path.Combine(_temp, "assets"), new JavaVersion("17"));

        var pipeline = LaunchOptionsFactory.CreatePipeline(
            paths, profile, Context(), options, "/usr/bin/java",
            preLaunchCommand: "echo before",
            postExitCommand: "echo after");

        var steps = pipeline.Steps;

        Assert.IsType<RunCommandStep>(steps[0]);
        Assert.IsType<RunCommandStep>(steps[^1]);

        // The game runs between the two hooks, not outside them.
        var launch = steps.ToList().FindIndex(s => s is LauncherPartLaunch);

        Assert.True(launch > 0 && launch < steps.Count - 1);
    }

    [Fact]
    public void OnlyTheConfiguredHookIsAdded()
    {
        // A pre-launch command with no post-exit adds one step, at the front.
        var paths = NewInstance("hookone");
        var profile = ProfileWith();

        var options = LaunchOptionsFactory.Create(
            paths, profile, "x", Path.Combine(_temp, "assets"), new JavaVersion("17"));

        var pipeline = LaunchOptionsFactory.CreatePipeline(
            paths, profile, Context(), options, "/usr/bin/java",
            preLaunchCommand: "echo before");

        Assert.Single(pipeline.Steps, s => s is RunCommandStep);
        Assert.IsType<RunCommandStep>(pipeline.Steps[0]);
    }

    [Fact]
    public void TheAccountIsClaimedOnlyWhenThereIsASession()
    {
        var paths = NewInstance("claim");
        var profile = ProfileWith();

        var options = LaunchOptionsFactory.Create(
            paths, profile, "x", Path.Combine(_temp, "assets"), new JavaVersion("17"));

        var withSession = LaunchOptionsFactory.CreatePipeline(
            paths, profile, Context(), options, "/usr/bin/java",
            session: new AuthSession { Status = SessionStatus.PlayableOnline, PlayerName = "Steve" });

        Assert.Contains(withSession.Steps, step => step is ClaimAccount);

        var withoutSession = LaunchOptionsFactory.CreatePipeline(
            paths, profile, Context(), options, "/usr/bin/java");

        Assert.DoesNotContain(withoutSession.Steps, step => step is ClaimAccount);
    }

    [Fact]
    public void TheJarIsOnlyModdedWhenThereAreJarMods()
    {
        var paths = NewInstance("jarmods");
        var profile = ProfileWith();

        var options = LaunchOptionsFactory.Create(
            paths, profile, "x", Path.Combine(_temp, "assets"), new JavaVersion("17"));

        var withMods = LaunchOptionsFactory.CreatePipeline(
            paths, profile, Context(), options, "/usr/bin/java",
            jarMods: [new JarMod("mod.zip", JarModKind.ZipFile)]);

        // And it lands before the natives are unpacked, matching upstream.
        var index = withMods.Steps.ToList().FindIndex(step => step is ModMinecraftJar);

        Assert.True(index >= 0);
        Assert.True(index < withMods.Steps.ToList().FindIndex(step => step is ExtractNativesStep));

        var withoutMods = LaunchOptionsFactory.CreatePipeline(
            paths, profile, Context(), options, "/usr/bin/java");

        Assert.DoesNotContain(withoutMods.Steps, step => step is ModMinecraftJar);
    }

    [Fact]
    public void CustomJvmArgumentsAreSplitShellStyle()
    {
        var paths = NewInstance("jvmargs");

        var options = LaunchOptionsFactory.Create(
            paths,
            ProfileWith(),
            "x",
            Path.Combine(_temp, "assets"),
            new JavaVersion("17"),
            customJvmArgumentsText: @"-Xmx6G -Dfoo=""a b""");

        // One string in the settings box, two arguments out — with the quotes consumed.
        Assert.Equal(["-Xmx6G", "-Dfoo=a b"], options.CustomJvmArguments);
    }

    [Fact]
    public async Task TheNativesDirectoryIsCleanedUpAfterTheLaunch()
    {
        var paths = NewInstance("cleanup");
        Directory.CreateDirectory(paths.NativesPath);
        File.WriteAllText(Path.Combine(paths.NativesPath, "leftover.so"), "stale");

        var step = new ExtractNativesStep([], paths.NativesPath, new JavaVersion("17"));

        var pipeline = new LaunchPipeline().AddStep(step);

        Assert.True(await pipeline.RunAsync());

        // Scratch space: it must not survive, or a stale native outlives the version that needed it.
        Assert.False(Directory.Exists(paths.NativesPath));
    }
}
