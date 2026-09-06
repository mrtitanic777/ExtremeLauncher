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
 * Characterization tests for the instance-aware launch steps. Upstream has no Qt test for any of them.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstanceStepsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-steps-" + Guid.NewGuid().ToString("N"));

    public InstanceStepsTests() => Directory.CreateDirectory(_temp);

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

    private string Path_(string name) => Path.Combine(_temp, name);

    private string MakeZip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path_(name);

        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }

    // ================================================================== ModMinecraftJar

    [Fact]
    public async Task WithNoJarModsTheStepDoesNothing()
    {
        var step = new ModMinecraftJar(Path_("bin"), Path_("minecraft.jar"), []);

        // The unmodded jar is used directly, so there is nothing to build and nothing to clean up.
        Assert.True(await step.RunAsync());
        Assert.False(Directory.Exists(Path_("bin")));
    }

    [Fact]
    public async Task TheModdedJarIsBuiltIntoTheBinFolder()
    {
        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"), ("META-INF/SIG.RSA", "sig"));
        var mod = MakeZip("mod.zip", ("Main.class", "modded"));

        var step = new ModMinecraftJar(Path_("bin"), vanilla, [new JarMod(mod, JarModKind.ZipFile)]);

        Assert.True(await step.RunAsync());
        Assert.True(File.Exists(step.FinalJarPath));

        using var result = ZipFile.OpenRead(step.FinalJarPath);

        Assert.Contains(result.Entries, e => e.FullName == "Main.class");
        Assert.DoesNotContain(result.Entries, e => e.FullName.Contains("META-INF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheModdedJarIsScratchAndDoesNotSurviveTheLaunch()
    {
        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));
        var mod = MakeZip("mod.zip", ("Mod.class", "modded"));

        var step = new ModMinecraftJar(Path_("bin"), vanilla, [new JarMod(mod, JarModKind.ZipFile)]);

        var pipeline = new LaunchPipeline().AddStep(step);

        Assert.True(await pipeline.RunAsync());

        // Rebuilt every launch from the mod list and the version's main jar, so keeping it would only
        // risk launching a stale one.
        Assert.False(File.Exists(step.FinalJarPath));
    }

    [Fact]
    public async Task AStaleJarIsReplacedRatherThanAddedTo()
    {
        var binRoot = Path_("bin");
        Directory.CreateDirectory(binRoot);

        var stale = Path.Combine(binRoot, "minecraft.jar");
        File.WriteAllText(stale, "left over from a previous launch");

        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));
        var mod = MakeZip("mod.zip", ("Mod.class", "modded"));

        var step = new ModMinecraftJar(binRoot, vanilla, [new JarMod(mod, JarModKind.ZipFile)]);

        Assert.True(await step.RunAsync());

        using var result = ZipFile.OpenRead(step.FinalJarPath);
        Assert.Equal(2, result.Entries.Count);
    }

    [Fact]
    public async Task AnUnbuildableJarFailsTheStep()
    {
        var step = new ModMinecraftJar(
            Path_("bin"),
            Path_("no-such-source.jar"),
            [new JarMod(Path_("no-such-mod.zip"), JarModKind.ZipFile)]);

        Assert.False(await step.RunAsync());
        Assert.False(File.Exists(step.FinalJarPath));
    }

    [Fact]
    public async Task AFailedJarStopsThereRatherThanCarryingOn()
    {
        // UPSTREAM BUG (#8): upstream calls emitFailed() for the folder and stale-jar checks and then
        // falls through with no return, so it reports a failure and then tries to build the jar
        // anyway. Here the step stops, which is the only outcome a return value can express.
        var blocker = Path_("bin");
        await File.WriteAllTextAsync(blocker, "a file where the bin folder should go");

        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));
        var mod = MakeZip("mod.zip", ("Mod.class", "modded"));

        var step = new ModMinecraftJar(blocker, vanilla, [new JarMod(mod, JarModKind.ZipFile)]);

        Assert.False(await step.RunAsync());
    }

    // ================================================================== ClaimAccount

    private static AuthSession OnlineSession()
        => new() { Status = SessionStatus.PlayableOnline, PlayerName = "Steve" };

    [Fact]
    public async Task AnOnlineAccountIsLockedForTheDurationOfTheGame()
    {
        var account = MinecraftAccount.CreateBlankMsa();
        var step = new ClaimAccount(OnlineSession(), account);

        Assert.True(await step.RunAsync());

        // The lock is what stops a token refresh invalidating the session the game is holding.
        Assert.True(account.IsInUse);
        Assert.True(step.IsHeld);
        Assert.False(account.ShouldRefresh());

        await step.FinalizeAsync();

        Assert.False(account.IsInUse);
        Assert.False(step.IsHeld);
    }

    [Fact]
    public async Task AnOfflineSessionClaimsNothing()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        var session = new AuthSession { Status = SessionStatus.PlayableOffline, PlayerName = "Steve" };
        var step = new ClaimAccount(session, account);

        Assert.True(await step.RunAsync());

        // Nothing to invalidate, so nothing to hold.
        Assert.False(account.IsInUse);
    }

    [Fact]
    public async Task ADemoSessionClaimsNothing()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        var session = new AuthSession { Status = SessionStatus.PlayableOnline, PlayerName = "Steve", Demo = true };

        Assert.True(await new ClaimAccount(session, account).RunAsync());
        Assert.False(account.IsInUse);
    }

    [Fact]
    public async Task AnUnknownAccountIsNotAFailure()
    {
        // The session names a player the account list has never heard of. Upstream simply holds no
        // lock rather than refusing to launch.
        var step = new ClaimAccount(OnlineSession(), account: null);

        Assert.True(await step.RunAsync());
        Assert.False(step.IsHeld);

        await step.FinalizeAsync();
    }

    [Fact]
    public void TheAccountLockCannotBeAborted()
    {
        // Releasing it partway through would defeat the point.
        Assert.False(new ClaimAccount(OnlineSession(), MinecraftAccount.CreateBlankMsa()).CanAbort);
    }

    [Fact]
    public async Task TheLockIsReleasedWhenTheLaunchUnwinds()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        var pipeline = new LaunchPipeline().AddStep(new ClaimAccount(OnlineSession(), account));

        Assert.True(await pipeline.RunAsync());

        // Released by the pipeline's reverse unwind, not by the step's own success.
        Assert.False(account.IsInUse);
    }

    [Fact]
    public async Task TwoLaunchesOnOneAccountNest()
    {
        var account = MinecraftAccount.CreateBlankMsa();

        var first = new ClaimAccount(OnlineSession(), account);
        var second = new ClaimAccount(OnlineSession(), account);

        await first.RunAsync();
        await second.RunAsync();

        await first.FinalizeAsync();

        // One game exited; the other is still running, so the account stays locked.
        Assert.True(account.IsInUse);

        await second.FinalizeAsync();
        Assert.False(account.IsInUse);
    }

    // ================================================================== PrintInstanceInfo

    [Fact]
    public async Task TheInstanceDescriptionIsLogged()
    {
        var step = new PrintInstanceInfo(["Instance: Test", "Java: 17.0.1"]);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());

        Assert.Contains("Instance: Test", lines);
        Assert.Contains("Java: 17.0.1", lines);
    }

    [Fact]
    public async Task NothingToSayIsNotAFailure()
    {
        var step = new PrintInstanceInfo([]);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());

        // The hardware probes only run on Linux and FreeBSD, and are best-effort even there.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            Assert.Empty(lines);
        }
    }

    [Fact]
    public async Task AMissingProbeDoesNotStopTheLaunch()
    {
        // Every probe shells out to a tool that may not be installed — and on Windows and macOS, none
        // of them are. A launch must not depend on any of it.
        var pipeline = new LaunchPipeline().AddStep(new PrintInstanceInfo(["hello"]));

        Assert.True(await pipeline.RunAsync().WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
