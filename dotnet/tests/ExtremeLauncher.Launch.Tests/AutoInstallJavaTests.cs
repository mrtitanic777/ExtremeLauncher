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
 * Characterization tests for automatic Java installation. Upstream has no Qt test for it.
 *
 * The property that matters most here is that NOTHING IN THIS STEP FAILS A LAUNCH. Every path that
 * cannot produce a runtime warns and succeeds, leaving the user's own Java alone — a meta outage or an
 * unpublished platform must not stop someone playing with the JVM they already have.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Tasks;
using Xunit;
using MetaIndex = ExtremeLauncher.Meta.Index;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AutoInstallJavaTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-autojava-" + Guid.NewGuid().ToString("N"));

    public AutoInstallJavaTests() => Directory.CreateDirectory(_temp);

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

    private string JavaDir => Path.Combine(_temp, "java");

    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    private static LaunchProfile ProfileWith(string javaName, params int[] majors)
    {
        var profile = new LaunchProfile();

        var patch = new VersionFile { Uid = "net.minecraft", Version = "1.20.1", CompatibleJavaName = javaName };
        patch.CompatibleJavaMajors.AddRange(majors);

        profile.Apply(patch, Context());

        return profile;
    }

    /// <summary>An index holding one loaded "net.minecraft.java" version with the given runtimes.</summary>
    private static MetaIndex IndexWithRuntimes(string version, params JavaMetadata[] runtimes)
    {
        var index = new MetaIndex();
        var meta = index.GetOrCreate("net.minecraft.java", version);

        // Only Data matters to the step: a version with a body is usable whether or not the entity is
        // formally "current", which is what lets an offline launch use a cached runtime list.
        var data = new VersionFile { Uid = "net.minecraft.java", Version = version };
        data.Runtimes.AddRange(runtimes);

        meta.Data = data;

        return index;
    }

    private static JavaMetadata Runtime(string name, string runtimeOS)
        => new() { Name = name, RuntimeOS = runtimeOS, DownloadType = DownloadType.Archive, Url = "https://java.invalid/x" };

    /// <summary>Stands in for a real download, writing the interpreter where one would end up.</summary>
    private static Func<JavaMetadata, string, LauncherTask> InstallerThatWrites(List<string> installed)
        => (_, finalPath) => new DelegateTask(() =>
        {
            var binary = Path.Combine(finalPath, "bin", JavaUtils.JavaExecutable);

            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllText(binary, "#!/bin/sh");

            installed.Add(finalPath);
        });

    /// <summary>A download that creates the directory and then fails, as a broken unpack would.</summary>
    private static Func<JavaMetadata, string, LauncherTask> InstallerThatBreaks()
        => (_, finalPath) => new DelegateTask(() =>
        {
            Directory.CreateDirectory(finalPath);
            throw new TaskFailedException("connection reset");
        });

    private sealed class DelegateTask : LauncherTask
    {
        private readonly Action _action;

        public DelegateTask(Action action) : base("stub install") => _action = action;

        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _action();
            return Task.CompletedTask;
        }
    }

    private AutoInstallJava Step(
        LaunchProfile profile,
        MetaIndex index,
        Func<JavaMetadata, string, LauncherTask>? createDownload = null,
        Func<string, string, CancellationToken, Task<bool>>? loadVersion = null,
        IReadOnlyList<JavaInstall>? installed = null,
        bool automaticDownload = true,
        string architecture = "linux-x64")
        => new(profile, index, JavaDir, loadVersion, createDownload, installed, automaticDownload, architecture);

    // ================================================================== downloading

    [Fact]
    public async Task ACompatibleRuntimeIsDownloadedAndPointedAt()
    {
        var index = IndexWithRuntimes("java17", Runtime("java-runtime-gamma", "linux-x64"));
        var installed = new List<string>();

        var step = Step(ProfileWith("java-runtime-gamma", 17), index, InstallerThatWrites(installed));

        Assert.True(await step.RunAsync());

        Assert.True(step.Resolution.Found);
        Assert.Equal(
            FileSystem.PathCombine(JavaDir, "java-runtime-gamma", "bin", JavaUtils.JavaExecutable),
            step.Resolution.JavaPath);
        Assert.Single(installed);
    }

    [Fact]
    public async Task OnlyTheRuntimeMatchingThisMachineIsChosen()
    {
        // The list spans every platform; exactly one entry is for this one.
        var index = IndexWithRuntimes(
            "java17",
            Runtime("java-runtime-gamma", "windows-x64"),
            Runtime("java-runtime-gamma", "mac-os-arm64"),
            Runtime("java-runtime-gamma", "linux-x64"));

        JavaMetadata? chosen = null;

        var step = Step(
            ProfileWith("java-runtime-gamma", 17),
            index,
            (runtime, finalPath) =>
            {
                chosen = runtime;

                var binary = Path.Combine(finalPath, "bin", JavaUtils.JavaExecutable);
                Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
                File.WriteAllText(binary, "#!/bin/sh");

                return new DelegateTask(() => { });
            });

        Assert.True(await step.RunAsync());
        Assert.Equal("linux-x64", chosen?.RuntimeOS);
    }

    [Fact]
    public async Task TheNameTheProfileAsksForMustMatchToo()
    {
        // Right platform, wrong runtime family — Mojang publishes several per major.
        var index = IndexWithRuntimes("java17", Runtime("java-runtime-delta", "linux-x64"));

        var step = Step(ProfileWith("java-runtime-gamma", 17), index, InstallerThatWrites([]));

        Assert.True(await step.RunAsync());
        Assert.False(step.Resolution.Found);
    }

    [Fact]
    public async Task AnAlreadyInstalledRuntimeIsReusedWithoutDownloading()
    {
        var binary = Path.Combine(JavaDir, "java-runtime-gamma", "bin", JavaUtils.JavaExecutable);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllTextAsync(binary, "#!/bin/sh");

        var downloads = 0;

        var step = Step(
            ProfileWith("java-runtime-gamma", 17),
            new MetaIndex(),
            (_, _) => new DelegateTask(() => downloads++));

        Assert.True(await step.RunAsync());

        Assert.True(step.Resolution.Found);
        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task ADirectoryWithNoInterpreterInItIsNotUsed()
    {
        // A failed install from a previous run: the folder exists but there is nothing to launch.
        Directory.CreateDirectory(Path.Combine(JavaDir, "java-runtime-gamma"));

        var step = Step(ProfileWith("java-runtime-gamma", 17), new MetaIndex());

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());

        Assert.False(step.Resolution.Found);
        Assert.Contains(lines, l => l.Contains("the binary file does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHalfUnpackedRuntimeIsDeleted()
    {
        var index = IndexWithRuntimes("java17", Runtime("java-runtime-gamma", "linux-x64"));

        var step = Step(ProfileWith("java-runtime-gamma", 17), index, InstallerThatBreaks());

        Assert.True(await step.RunAsync());

        // Left behind, the "already installed" check would find it next launch and hand the game a
        // path with no interpreter in it.
        Assert.False(Directory.Exists(Path.Combine(JavaDir, "java-runtime-gamma")));
        Assert.False(step.Resolution.Found);
    }

    [Fact]
    public async Task EachCompatibleMajorIsTriedInTurn()
    {
        // Nothing published for 21; 17 works.
        var index = IndexWithRuntimes("java17", Runtime("java-runtime-gamma", "linux-x64"));

        var attempted = new List<string>();

        var step = Step(
            ProfileWith("java-runtime-gamma", 21, 17),
            index,
            InstallerThatWrites(attempted),
            loadVersion: (_, version, _) =>
            {
                attempted.Add(version);
                return Task.FromResult(true);
            });

        Assert.True(await step.RunAsync());
        Assert.True(step.Resolution.Found);
    }

    [Fact]
    public async Task AVersionThatCannotBeFetchedIsSkipped()
    {
        var index = new MetaIndex();

        var step = Step(
            ProfileWith("java-runtime-gamma", 17),
            index,
            InstallerThatWrites([]),
            loadVersion: (_, _, _) => Task.FromResult(false));

        // A meta outage warns and moves on rather than stopping the launch.
        Assert.True(await step.RunAsync());
        Assert.False(step.Resolution.Found);
    }

    // ================================================================== the giving-up paths

    [Fact]
    public async Task AnUnsupportedPlatformIsSkippedWithAWarning()
    {
        var step = Step(
            ProfileWith("java-runtime-gamma", 17),
            new MetaIndex(),
            architecture: string.Empty);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        // FreeBSD and OpenBSD have no published runtimes. Not an error: the user's own Java works.
        Assert.True(await step.RunAsync());
        Assert.False(step.Resolution.Found);
        Assert.Contains(lines, l => l.Contains("not compatible with automatic Java installation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MetadataWithNoJavaNameIsSkippedWithAWarning()
    {
        var step = Step(ProfileWith(string.Empty, 17), new MetaIndex());

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());
        Assert.Contains(lines, l => l.Contains("meta information is out of date", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningOutOfCandidatesIsStillASuccess()
    {
        var step = Step(ProfileWith("java-runtime-gamma", 17, 21), new MetaIndex());

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());
        Assert.Contains(lines, l => l.Contains("Using the default one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStepNeverFailsALaunch()
    {
        // Every giving-up path in one place: no runtimes, no loader, no installer.
        var step = Step(ProfileWith("java-runtime-gamma", 8, 17, 21), new MetaIndex());

        Assert.True(await step.RunAsync());

        var pipeline = new LaunchPipeline().AddStep(Step(ProfileWith(string.Empty), new MetaIndex()));
        Assert.True(await pipeline.RunAsync());
    }

    // ================================================================== picking an installed Java

    [Fact]
    public async Task WithoutAutomaticDownloadAnInstalledJavaIsPicked()
    {
        var installed = new List<JavaInstall>
        {
            new("1.8.0_392", "64", "/usr/lib/jvm/java-8/bin/java"),
            new("17.0.9", "64", "/usr/lib/jvm/java-17/bin/java"),
        };

        var step = Step(ProfileWith("java-runtime-gamma", 17), new MetaIndex(), installed: installed, automaticDownload: false);

        Assert.True(await step.RunAsync());
        Assert.Equal("/usr/lib/jvm/java-17/bin/java", step.Resolution.JavaPath);
    }

    [Fact]
    public async Task TheFirstCompatibleMajorWinsRatherThanTheNewest()
    {
        var installed = new List<JavaInstall>
        {
            new("17.0.9", "64", "/usr/lib/jvm/java-17/bin/java"),
            new("21.0.1", "64", "/usr/lib/jvm/java-21/bin/java"),
        };

        // The profile lists them in preference order.
        var step = Step(ProfileWith("x", 17, 21), new MetaIndex(), installed: installed, automaticDownload: false);

        Assert.True(await step.RunAsync());
        Assert.Equal("/usr/lib/jvm/java-17/bin/java", step.Resolution.JavaPath);
    }

    [Fact]
    public async Task A32BitInstallIsAcceptedButCalledOut()
    {
        var installed = new List<JavaInstall> { new("17.0.9", "32", "C:/java32/bin/java.exe") };

        var step = Step(ProfileWith("x", 17), new MetaIndex(), installed: installed, automaticDownload: false);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());

        // Used, but flagged: it caps the heap at 2 GiB and the resulting crash is otherwise mystifying.
        Assert.Equal("C:/java32/bin/java.exe", step.Resolution.JavaPath);
        Assert.Contains(lines, l => l.Contains("32-bit installation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoInstalledJavaMatchesIsAWarningNotAFailure()
    {
        var installed = new List<JavaInstall> { new("8.0.392", "64", "/usr/lib/jvm/java-8/bin/java") };

        var step = Step(ProfileWith("x", 17), new MetaIndex(), installed: installed, automaticDownload: false);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());
        Assert.False(step.Resolution.Found);
        Assert.Contains(lines, l => l.Contains("No compatible Java version was found", StringComparison.Ordinal));
    }

    // ================================================================== SysInfo

    [Fact]
    public void TheTwoPlatformVocabulariesAreNotInterchangeable()
    {
        var system = SysInfo.CurrentSystem();
        var runtimeOS = SysInfo.SupportedJavaArchitecture();

        // "osx" versus "mac-os", "x86_64" versus "x64" — they look similar and are used for different
        // lookups, so a swap silently stops matching anything.
        Assert.Contains(system, new[] { "windows", "osx", "linux", "freebsd", "openbsd", "unknown" });

        if (system is "windows" or "osx" or "linux")
        {
            Assert.NotEmpty(runtimeOS);
            Assert.Contains('-', runtimeOS);
            Assert.StartsWith(system == "osx" ? "mac-os" : system, runtimeOS, StringComparison.Ordinal);
        }
        else
        {
            // An empty result is meaningful: no runtimes are published for this platform.
            Assert.Empty(runtimeOS);
        }
    }

    [Fact]
    public void ArchitectureUsesQtsSpellingRatherThanDotNets()
    {
        // The rules in a version JSON are written against Qt's names; ".NET says X64" would miss.
        Assert.Contains(SysInfo.CurrentArchitecture(), new[] { "x86_64", "i386", "arm64", "arm" });
    }

    [Fact]
    public void TheDefaultHeapIsCappedAtFourGigabytes()
    {
        var suggested = SysInfo.SuitableMaxMemory();

        // More rarely helps, and a larger heap means longer collection pauses.
        Assert.InRange(suggested, 1, 4096);
    }
}
