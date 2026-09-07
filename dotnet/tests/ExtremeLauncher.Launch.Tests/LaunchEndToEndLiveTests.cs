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
 * THE END-TO-END LAUNCH PROOF, AUTOMATED. For a long time PORTING.md's biggest open question was
 * whether the launcher could actually start Minecraft; it was confirmed by hand (a real 26.2 window on
 * Windows) but nothing pinned it against a regression. This does.
 *
 * It walks the exact chain a launch walks, against the LIVE metadata server:
 *   create + resolve a vanilla instance  ->  LauncherService.ResolveAsync (the GUI's own resolve path)
 *   ->  build the JVM command line  ->  assert it is a real, complete Minecraft launch command.
 *
 * WHAT IT DELIBERATELY DOES NOT DO is download the hundreds of megabytes of libraries and assets or
 * spawn a JVM. The command line is built from the RESOLVED METADATA (GetLibraryFiles composes paths
 * from the component tree, not from files on disk), so this stays a metadata-weight test while still
 * proving the whole resolve-and-assemble chain wires together. The download pipeline and the process
 * supervisor have their own tests.
 *
 * SKIPPED, NOT FAILED, with no network: a probe against a live server cannot be a build-breaker. When
 * the network is there it is the test that catches the meta server, or the resolver, or the command
 * builder, drifting apart.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LaunchEndToEndLiveTests : IDisposable
{
    // A stable, long-lived Minecraft release the metadata server is certain to carry. Pinned rather
    // than "latest" so the assertions can name the version they expect to see in the command line.
    private const string Version = "1.21.1";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-e2e-" + Guid.NewGuid().ToString("N"));

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

    private static RuntimeContext Context() => new()
    {
        System = "windows",
        JavaArchitecture = "64",
        JavaRealArchitecture = "x86_64",
    };

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildConfig.Instance.UserAgent);

        return client;
    }

    [SkippableFact]
    public async Task AVanillaInstanceResolvesAndBuildsARealLaunchCommand()
    {
        using var client = Client();

        var paths = new LauncherPaths(_temp);
        paths.EnsureExists();

        var metaUrl = GlobalSettings.ResolveMetaUrl(GlobalSettings.Create(paths.LauncherConfig));

        // -------- create + resolve a vanilla instance, the way the New dialog does (with the network,
        // so the component tree -- lwjgl and the rest -- is filled in and the instance is launchable).
        var list = new InstanceList(paths.Instances, GlobalSettings.Create(paths.LauncherConfig));
        list.LoadGroupList();
        list.LoadList();

        var creation = new VanillaCreationTask(
            "E2E", Version, Context(), paths: paths, client: client, metaUrl: metaUrl);
        var staging = new InstanceStagingTask(list, creation, creation);

        bool created;
        try
        {
            created = await staging.RunAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LauncherException)
        {
            throw new SkipException($"The metadata server is not reachable: {e.Message}");
        }

        Skip.If(!created, "Instance creation/resolution did not succeed (likely offline).");

        // -------- resolve to a flattened LaunchProfile through the SAME service the Launch button uses.
        var service = new LauncherService(paths, client);
        var request = new LaunchRequest
        {
            InstanceId = staging.CommittedId,
            MetaUrl = metaUrl,
            PlayerName = "Steve",
        };

        InstanceRecord instance;
        LaunchProfile profile;
        try
        {
            (instance, profile) = await service
                .ResolveAsync(request, new NullLaunchReporter(), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new SkipException($"The metadata server is not reachable: {e.Message}");
        }

        // -------- build the JVM command line from the resolved metadata (no downloads, no JVM).
        var runtimeContext = Context();

        var options = LaunchOptionsFactory.Create(
            instance.Paths,
            profile,
            instance.Name,
            paths.Assets,
            new JavaVersion(21, 0, 0));

        var session = MinecraftAccount.CreateOffline("Steve").CreateSession(wantsOnline: false);
        var command = LaunchCommandBuilder.BuildCommandLine(
            profile, runtimeContext, options, session, LaunchTarget.Choose(null, null));

        var joined = string.Join(" ", command);

        // A real vanilla launch: Mojang's entry point, the chosen player and version, and -- the proof
        // that resolution actually happened -- LWJGL and the client jar on the classpath. An instance
        // that resolved to net.minecraft alone (the bug the resolver-at-creation change fixed) would
        // have neither.
        Assert.Contains("net.minecraft.client.main.Main", command);
        Assert.Contains("--username", command);
        Assert.Contains("Steve", command);
        Assert.Contains("--version", command);
        Assert.Contains(Version, joined);
        Assert.Contains(command, arg => arg.Contains("lwjgl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            command,
            arg => arg.Contains("minecraft", StringComparison.OrdinalIgnoreCase)
                   && arg.Contains("client", StringComparison.OrdinalIgnoreCase));
    }
}
