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
 * A SUCCESSFUL launch needs a metadata server, a JVM and several hundred megabytes of downloads, and
 * the CLI proves that end to end by actually starting Minecraft. None of it belongs in a unit test.
 *
 * WHAT IS CHECKED HERE IS EVERY WAY IT REFUSES. Those are the paths a user actually meets -- a typo in
 * an instance name, an instance the launcher cannot understand, a half-copied directory, no network --
 * and each one has to arrive as a sentence a person can act on rather than a NullReferenceException or
 * a silent hang. They are also the paths that never get exercised by hand, because testing them by
 * hand means deliberately breaking an install.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LauncherServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "el-svc-" + Guid.NewGuid().ToString("N"));

    private readonly LauncherPaths _paths;

    public LauncherServiceTests()
    {
        _paths = new LauncherPaths(_root);
        _paths.EnsureExists();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>An offline service: no client, so nothing can be fetched.</summary>
    private LauncherService Service() => new(_paths, client: null);

    private string MakeInstance(string id, string type = "OneSix", string? packJson = null)
    {
        var path = Path.Combine(_paths.Instances, id);
        Directory.CreateDirectory(path);

        File.WriteAllText(Path.Combine(path, "instance.cfg"), $"name={id}\nInstanceType={type}\n");

        if (packJson is not null)
        {
            File.WriteAllText(Path.Combine(path, "mmc-pack.json"), packJson);
        }

        return path;
    }

    private static LaunchRequest Request(string id) => new() { InstanceId = id, Offline = true };

    // ================================================================== refusing early

    [Fact]
    public async Task AnEmptyInstanceIdIsRefused()
    {
        var error = await Assert.ThrowsAsync<LauncherException>(
            () => Service().ResolveAsync(Request(string.Empty), NullLaunchReporter.Instance)).ConfigureAwait(true);

        Assert.Contains("No instance given", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The name the user typed is echoed back — "not found" alone does not help them fix it.</summary>
    [Fact]
    public async Task AnUnknownInstanceIsRefusedByName()
    {
        MakeInstance("RealPack");

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => Service().ResolveAsync(Request("Typo"), NullLaunchReporter.Instance)).ConfigureAwait(true);

        Assert.Contains("Typo", error.Message, StringComparison.Ordinal);
    }

    /*
     * REFUSED BEFORE ANYTHING IS DOWNLOADED. An unsupported instance is one the launcher can read
     * enough of to list and not enough to run; the useful moment to say so is before several hundred
     * megabytes have been fetched on its behalf.
     */
    [Fact]
    public async Task AnUnsupportedInstanceIsRefusedAndNamesItsType()
    {
        MakeInstance("Ancient", type: "OneSixFTB");

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => Service().ResolveAsync(Request("Ancient"), NullLaunchReporter.Instance)).ConfigureAwait(true);

        Assert.Contains("OneSixFTB", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A directory with no mmc-pack.json — a half-copied instance, which is a real state.</summary>
    [Fact]
    public async Task AnInstanceWithNoPackFileIsRefusedAndNamesTheFile()
    {
        MakeInstance("Broken");

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => Service().ResolveAsync(Request("Broken"), NullLaunchReporter.Instance)).ConfigureAwait(true);

        Assert.Contains("mmc-pack.json", error.Message, StringComparison.Ordinal);
    }

    // ================================================================== offline

    /*
     * WITH NO NETWORK AND A COLD CACHE, THIS MUST FAIL AND SAY SO. The failure that would matter is a
     * hang: a resolve that waits forever on metadata it can never get is indistinguishable, from the
     * outside, from a launcher that has crashed.
     */
    [Fact]
    public async Task AnOfflineResolveWithNothingCachedFailsRatherThanHanging()
    {
        MakeInstance(
            "Offline",
            packJson: """
                {
                    "formatVersion": 1,
                    "components": [ { "uid": "net.minecraft", "version": "1.20.1" } ]
                }
                """);

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => Service().ResolveAsync(Request("Offline"), NullLaunchReporter.Instance)).ConfigureAwait(true);

        Assert.Contains("resolve", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== the instance list

    [Fact]
    public void OpeningTheInstancesCreatesTheLayoutAndFindsThem()
    {
        MakeInstance("One");
        MakeInstance("Two");

        var list = Service().OpenInstances();

        Assert.Equal(2, list.Count);
        Assert.NotNull(list.GetInstanceById("One"));
        Assert.True(Directory.Exists(_paths.Libraries));
    }

    // ================================================================== choosing a JVM

    /*
     * WITH NO STATED REQUIREMENT, the first candidate is taken unprobed -- there is nothing to check it
     * against, and probing every JVM on the machine to learn nothing is a slow way to start.
     */
    [Fact]
    public async Task WithNoJavaRequirementTheFirstCandidateIsTaken()
    {
        var profile = new LaunchProfile();

        var chosen = await LauncherService
            .FindUsableJavaAsync(profile, candidates: ["/first/javaw", "/second/javaw"])
            .ConfigureAwait(true);

        Assert.Equal("/first/javaw", chosen);
    }

    [Fact]
    public async Task WithNoCandidatesAtAllNothingIsChosen()
    {
        var chosen = await LauncherService
            .FindUsableJavaAsync(new LaunchProfile(), candidates: [])
            .ConfigureAwait(true);

        Assert.Equal(string.Empty, chosen);
    }

    /// <summary>Nothing acceptable and no permission to ignore that: refused, so the caller can say why.</summary>
    [Fact]
    public async Task AnIncompatibleJavaIsRefusedByDefault()
    {
        var profile = new LaunchProfile();
        profile.ApplyCompatibleJavaMajors([17]);

        // Paths that cannot be probed stand in for JVMs that fail the check, which is the same branch.
        var chosen = await LauncherService
            .FindUsableJavaAsync(profile, candidates: ["/nonexistent/javaw"])
            .ConfigureAwait(true);

        Assert.Equal(string.Empty, chosen);
    }

    /*
     * THE SETTING HAS TO REACH SELECTION, not only VerifyJavaInstall. It did not, at first: a machine
     * with Java 21 and 25 and a pack asking for 17 refused to start even with IgnoreJavaCompatibility
     * set, because selection gave up before the step that honours the setting ever ran.
     */
    [Fact]
    public async Task IgnoringCompatibilityFallsBackToTheFirstCandidate()
    {
        var profile = new LaunchProfile();
        profile.ApplyCompatibleJavaMajors([17]);

        var chosen = await LauncherService
            .FindUsableJavaAsync(profile, allowIncompatible: true, candidates: ["/first/javaw", "/second/javaw"])
            .ConfigureAwait(true);

        Assert.Equal("/first/javaw", chosen);
    }

    // ================================================================== the runtime it reports

    /// <summary>The rules speak in "64"/"32" while native classifiers want the real name.</summary>
    [Fact]
    public void TheRuntimeContextCarriesBothArchitectureSpellings()
    {
        var context = LauncherService.CurrentRuntimeContext();

        Assert.True(context.JavaArchitecture is "64" or "32", context.JavaArchitecture);
        Assert.Equal(SysInfo.CurrentArchitecture(), context.JavaRealArchitecture);
        Assert.Equal(SysInfo.CurrentSystem(), context.System);
    }
}
