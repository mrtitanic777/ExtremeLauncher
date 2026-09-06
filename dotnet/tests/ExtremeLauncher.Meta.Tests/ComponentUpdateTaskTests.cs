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
 * Characterization tests for component loading. Upstream has no Qt test for ComponentUpdateTask.
 *
 * The two behaviours worth being certain about are that a LOCAL PATCH FILE BEATS the meta server —
 * which is the entire point of the patches folder — and that Launch mode never rewrites versions
 * under someone who is trying to play.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class ComponentUpdateTaskTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-cut-" + Guid.NewGuid().ToString("N"));

    public ComponentUpdateTaskTests() => Directory.CreateDirectory(_temp);

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

    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    /// <summary>A meta version that is already cached, so nothing has to be fetched for it.</summary>
    private static MetaVersion LoadedVersion(string uid, string version, VersionFile? data = null)
    {
        var meta = new MetaVersion(uid, version)
        {
            Type = "release",
            RawTime = 1_700_000_000,

            // With no expected hash, a remote load is what marks an entity current.
            Status = MetaEntity.LoadStatus.Remote,
        };

        meta.Data = data ?? new VersionFile { Uid = uid, Version = version, Name = uid };

        return meta;
    }

    private static Index IndexWith(params MetaVersion[] versions)
    {
        var lists = versions
            .GroupBy(v => v.Uid, StringComparer.Ordinal)
            .Select(group =>
            {
                var list = new VersionList(group.Key);
                list.SetVersions(group);

                return list;
            });

        return new Index(lists);
    }

    private ComponentUpdateTask Task_(
        PackProfile profile,
        Index index,
        ComponentUpdateMode mode = ComponentUpdateMode.Launch,
        NetMode netMode = NetMode.Online,
        Func<string, string, CancellationToken, Task<bool>>? loadVersion = null)
        => new(profile, index, _temp, mode, netMode, loadVersion);

    private static PackProfile ProfileWith(params Component[] components)
    {
        var profile = new PackProfile(Context());

        foreach (var component in components)
        {
            profile.AppendComponent(component);
        }

        return profile;
    }

    // ================================================================== loading from the index

    [Fact]
    public async Task AnAlreadyCachedComponentNeedsNoFetch()
    {
        var index = IndexWith(LoadedVersion("net.minecraft", "1.20.1"));
        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        var fetched = 0;

        var task = Task_(profile, index, loadVersion: (_, _, _) =>
        {
            fetched++;
            return Task.FromResult(true);
        });

        Assert.True(await task.RunAsync());

        Assert.Equal(0, fetched);
        Assert.True(profile.GetComponent("net.minecraft")!.IsLoaded);
        Assert.Equal("net.minecraft", profile.GetComponent("net.minecraft")!.CachedName);
    }

    [Fact]
    public async Task AnUncachedComponentIsFetched()
    {
        var uncached = new MetaVersion("net.minecraft", "1.20.1") { Type = "release", RawTime = 1 };
        var index = IndexWith(uncached);
        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        var requested = new List<string>();

        var task = Task_(profile, index, loadVersion: (uid, version, _) =>
        {
            requested.Add($"{uid} {version}");

            // Standing in for the network: fill the body the way MetaEntity's load task would.
            uncached.Data = new VersionFile { Uid = uid, Version = version, Name = "Minecraft" };
            uncached.Status = MetaEntity.LoadStatus.Remote;

            return Task.FromResult(true);
        });

        Assert.True(await task.RunAsync());

        Assert.Equal(["net.minecraft 1.20.1"], requested);
        Assert.Equal("Minecraft", profile.GetComponent("net.minecraft")!.CachedName);
    }

    [Fact]
    public async Task EveryFetchIsAwaitedTogether()
    {
        var a = new MetaVersion("a", "1") { Type = "release" };
        var b = new MetaVersion("b", "1") { Type = "release" };

        var profile = ProfileWith(
            new Component("a") { Version = "1" },
            new Component("b") { Version = "1" });

        var started = 0;
        var completed = new TaskCompletionSource();

        var task = Task_(profile, IndexWith(a, b), loadVersion: async (uid, version, _) =>
        {
            // Both are in flight before either finishes — upstream needs a status list and a counter
            // to arrange this; here it is one Task.WhenAll.
            if (Interlocked.Increment(ref started) == 2)
            {
                completed.SetResult();
            }

            await completed.Task;

            var meta = uid == "a" ? a : b;
            meta.Data = new VersionFile { Uid = uid, Version = version };
            meta.Status = MetaEntity.LoadStatus.Remote;

            return true;
        });

        Assert.True(await task.RunAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task AFailedFetchFailsTheTask()
    {
        var index = IndexWith(new MetaVersion("net.minecraft", "1.20.1") { Type = "release" });
        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        var task = Task_(profile, index, loadVersion: (_, _, _) => Task.FromResult(false));

        Assert.False(await task.RunAsync());
        Assert.Contains("metadata load tasks failed", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineWithNothingCachedFails()
    {
        var index = IndexWith(new MetaVersion("net.minecraft", "1.20.1") { Type = "release" });
        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        var task = Task_(profile, index, netMode: NetMode.Offline);

        // Nothing on disk and no way to fetch it.
        Assert.False(await task.RunAsync());
    }

    [Fact]
    public async Task OfflineWithEverythingCachedSucceeds()
    {
        var index = IndexWith(LoadedVersion("net.minecraft", "1.20.1"));
        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        Assert.True(await Task_(profile, index, netMode: NetMode.Offline).RunAsync());
    }

    [Fact]
    public async Task AComponentTheIndexDoesNotKnowIsStillFetched()
    {
        // THE COLD-CACHE PATH, which every new install takes: the index has not been read yet, so
        // nothing is known about anything. The document's URL is derivable from the uid and version
        // alone, so it can be fetched before anything above it is known — an earlier draft of this
        // port treated "not in the index" as fatal and failed every first run.
        var index = IndexWith();
        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        var requested = new List<string>();

        var task = Task_(profile, index, loadVersion: (uid, version, _) =>
        {
            requested.Add($"{uid} {version}");

            var placeholder = index.GetOrCreate(uid, version);
            placeholder.Data = new VersionFile { Uid = uid, Version = version, Name = "Minecraft" };
            placeholder.Status = MetaEntity.LoadStatus.Remote;

            return Task.FromResult(true);
        });

        Assert.True(await task.RunAsync());

        Assert.Equal(["net.minecraft 1.20.1"], requested);
        Assert.Equal("Minecraft", profile.GetComponent("net.minecraft")!.CachedName);
    }

    [Fact]
    public async Task WithNothingCachedAndNoWayToFetchTheReasonIsNamed()
    {
        var profile = ProfileWith(new Component("com.example.nothing") { Version = "1.0" });

        var task = Task_(profile, IndexWith(), netMode: NetMode.Offline);

        Assert.False(await task.RunAsync());

        // Otherwise "offline with a cold cache" and "this component does not exist" look identical.
        Assert.Contains(
            profile.GetComponent("com.example.nothing")!.GetProblems(),
            p => p.Description.Contains("cannot be fetched", StringComparison.Ordinal));
    }

    // ================================================================== local patches

    private void WritePatch(string uid, string version, string name, string? storedUid = null)
    {
        var file = new VersionFile { Uid = storedUid ?? uid, Version = version, Name = name };

        File.WriteAllText(
            Path.Combine(_temp, $"{uid}.json"),
            OneSixVersionFormat.VersionFileToJson(file).ToJsonString());
    }

    [Fact]
    public async Task ALocalPatchBeatsTheMetaServer()
    {
        WritePatch("net.minecraft", "1.20.1", "My Hand-Edited Minecraft");

        var index = IndexWith(LoadedVersion(
            "net.minecraft",
            "1.20.1",
            new VersionFile { Uid = "net.minecraft", Version = "1.20.1", Name = "Mojang's Minecraft" }));

        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        Assert.True(await Task_(profile, index).RunAsync());

        // The whole point of the patches folder: a user who edited this file gets their version.
        var component = profile.GetComponent("net.minecraft")!;

        Assert.True(component.IsCustom);
        Assert.Equal("My Hand-Edited Minecraft", component.CachedName);
    }

    [Fact]
    public async Task APatchWithTheWrongUidIsRepairedOnDisk()
    {
        // Files get copied between instances and renamed by hand; a mismatched uid makes the patch
        // invisible to dependency resolution, so it is corrected rather than reported.
        WritePatch("net.minecraft", "1.20.1", "Minecraft", storedUid: "com.example.copied-from-elsewhere");

        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        Assert.True(await Task_(profile, IndexWith()).RunAsync());

        var rewritten = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_temp, "net.minecraft.json")))!;

        Assert.Equal("net.minecraft", (string?)rewritten["uid"]);
        Assert.True(profile.GetComponent("net.minecraft")!.IsLoaded);
    }

    [Fact]
    public async Task AnUnreadablePatchIsReported()
    {
        await File.WriteAllTextAsync(Path.Combine(_temp, "net.minecraft.json"), "{ this is not json");

        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        Assert.False(await Task_(profile, IndexWith()).RunAsync());

        Assert.Contains(
            profile.GetComponent("net.minecraft")!.GetProblems(),
            p => p.Description.Contains("Could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALocalPatchIsUsedEvenOffline()
    {
        WritePatch("net.minecraft", "1.20.1", "Minecraft");

        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        // Never reaches the index at all, so the network mode is irrelevant.
        Assert.True(await Task_(profile, IndexWith(), netMode: NetMode.Offline).RunAsync());
    }

    // ================================================================== resolution modes

    [Fact]
    public async Task LaunchModeReportsProblemsWithoutChangingAnything()
    {
        var minecraft = LoadedVersion("net.minecraft", "1.20.1");

        var loaderFile = new VersionFile { Uid = "loader", Version = "1.0", Name = "Loader" };
        loaderFile.Requires.Add(new Require("net.minecraft", equalsVersion: "1.19.4"));

        var loader = LoadedVersion("loader", "1.0", loaderFile);

        var profile = ProfileWith(
            new Component("net.minecraft") { Version = "1.20.1" },
            new Component("loader") { Version = "1.0" });

        Assert.True(await Task_(profile, IndexWith(minecraft, loader), ComponentUpdateMode.Launch).RunAsync());

        // The requirement is unmet and said so, but nothing was rewritten under someone about to play.
        Assert.Equal("1.20.1", profile.GetComponent("net.minecraft")!.Version);

        Assert.Contains(
            profile.GetComponent("loader")!.GetProblems(),
            p => p.Description.Contains("not the required version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfflineNeverRewritesVersionsEitherEvenInResolutionMode()
    {
        var minecraft = LoadedVersion("net.minecraft", "1.20.1");

        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        Assert.True(await Task_(
            profile,
            IndexWith(minecraft),
            ComponentUpdateMode.Resolution,
            NetMode.Offline).RunAsync());

        Assert.Equal("1.20.1", profile.GetComponent("net.minecraft")!.Version);
    }

    [Fact]
    public async Task ResolutionIsReportedForInspection()
    {
        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        var task = Task_(profile, IndexWith(LoadedVersion("net.minecraft", "1.20.1")));

        Assert.True(await task.RunAsync());

        // Available afterwards so a caller can show what would change without applying it.
        Assert.NotNull(task.Resolution);
        Assert.True(task.Resolution.Succeeded);
    }

    // ================================================================== problem propagation

    [Fact]
    public async Task AMissingRequirementIsAnError()
    {
        var file = new VersionFile { Uid = "loader", Version = "1.0", Name = "Loader" };
        file.Requires.Add(new Require("net.minecraft", equalsVersion: "1.20.1"));

        var profile = ProfileWith(new Component("loader") { Version = "1.0" });

        Assert.True(await Task_(profile, IndexWith(LoadedVersion("loader", "1.0", file))).RunAsync());

        var problems = profile.GetComponent("loader")!.GetProblems();

        Assert.Contains(problems, p => p.Description.Contains("missing requirement", StringComparison.Ordinal));
        Assert.Equal(ProblemSeverity.Error, profile.GetComponent("loader")!.GetProblemSeverity());
    }

    [Fact]
    public async Task BeingOffTheSuggestedVersionIsOnlyAWarning()
    {
        var minecraft = LoadedVersion("net.minecraft", "1.20.1");

        var file = new VersionFile { Uid = "loader", Version = "1.0", Name = "Loader" };
        file.Requires.Add(new Require("net.minecraft", suggests: "1.19.4"));

        var profile = ProfileWith(
            new Component("net.minecraft") { Version = "1.20.1" },
            new Component("loader") { Version = "1.0" });

        Assert.True(await Task_(profile, IndexWith(minecraft, LoadedVersion("loader", "1.0", file))).RunAsync());

        // A suggestion usually works when ignored; an exact requirement does not. The severities have
        // to differ or every modpack shows a wall of red.
        Assert.Equal(ProblemSeverity.Warning, profile.GetComponent("loader")!.GetProblemSeverity());
    }

    [Fact]
    public async Task ADependencysProblemsPropagateUpward()
    {
        // "loader" needs "lib", and "lib" needs something that is not there at all.
        var libFile = new VersionFile { Uid = "lib", Version = "1.0", Name = "Library" };
        libFile.Requires.Add(new Require("missing", equalsVersion: "1.0"));

        var loaderFile = new VersionFile { Uid = "loader", Version = "1.0", Name = "Loader" };
        loaderFile.Requires.Add(new Require("lib", equalsVersion: "1.0"));

        var profile = ProfileWith(
            new Component("lib") { Version = "1.0" },
            new Component("loader") { Version = "1.0" });

        var index = IndexWith(LoadedVersion("lib", "1.0", libFile), LoadedVersion("loader", "1.0", loaderFile));

        Assert.True(await Task_(profile, index).RunAsync());

        // The list shows which entry to look at, not only the one that ultimately broke.
        Assert.Contains(
            profile.GetComponent("loader")!.GetProblems(),
            p => p.Description.Contains("a dependency of this component, has reported issues", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProblemsAreClearedBetweenRuns()
    {
        var profile = ProfileWith(new Component("com.example.nothing") { Version = "1.0" });
        var index = IndexWith();

        await Task_(profile, index, netMode: NetMode.Offline).RunAsync();
        var first = profile.GetComponent("com.example.nothing")!.GetProblems().Count;

        Assert.NotEqual(0, first);

        await Task_(profile, index, netMode: NetMode.Offline).RunAsync();
        var second = profile.GetComponent("com.example.nothing")!.GetProblems().Count;

        // Otherwise every resolve doubles the list and the user sees the same error four times.
        Assert.Equal(first, second);
    }

    // ================================================================== version selection

    [Fact]
    public void RecommendedForAParentNeedsBothAnExactRequirementAndTheRecommendedFlag()
    {
        var compatible = new MetaVersion("loader", "2.0") { Type = "release", RawTime = 20 };
        compatible.SetRequires([new Require("net.minecraft", equalsVersion: "1.20.1")], []);

        var recommendedButUnrelated = new MetaVersion("loader", "3.0") { Type = "release", RawTime = 30, IsRecommended = true };

        var recommendedAndCompatible = new MetaVersion("loader", "1.5") { Type = "release", RawTime = 15, IsRecommended = true };
        recommendedAndCompatible.SetRequires([new Require("net.minecraft", equalsVersion: "1.20.1")], []);

        var list = new VersionList("loader");
        list.SetVersions([compatible, recommendedButUnrelated, recommendedAndCompatible]);

        Assert.Equal("1.5", list.GetRecommendedForParent("net.minecraft", "1.20.1")?.VersionString);

        // A parent nothing was built for has no recommendation, rather than falling back to any.
        Assert.Null(list.GetRecommendedForParent("net.minecraft", "1.7.10"));
    }

    [Fact]
    public void TheLatestCompatibleVersionPrefersAReleaseOverANewerSnapshot()
    {
        var release = new MetaVersion("loader", "1.0") { Type = "release", RawTime = 10 };
        release.SetRequires([new Require("net.minecraft", equalsVersion: "1.20.1")], []);

        var newerSnapshot = new MetaVersion("loader", "2.0-beta") { Type = "snapshot", RawTime = 99 };
        newerSnapshot.SetRequires([new Require("net.minecraft", equalsVersion: "1.20.1")], []);

        var list = new VersionList("loader");
        list.SetVersions([release, newerSnapshot]);

        // TYPE BEATS RECENCY, so a fresh snapshot does not quietly become the pick for a modloader.
        Assert.Equal("1.0", list.GetLatestForParent("net.minecraft", "1.20.1")?.VersionString);
    }

    [Fact]
    public void TheNewestOfOneTypeWins()
    {
        var older = new MetaVersion("loader", "1.0") { Type = "release", RawTime = 10 };
        older.SetRequires([new Require("net.minecraft", equalsVersion: "1.20.1")], []);

        var newer = new MetaVersion("loader", "2.0") { Type = "release", RawTime = 20 };
        newer.SetRequires([new Require("net.minecraft", equalsVersion: "1.20.1")], []);

        var list = new VersionList("loader");
        list.SetVersions([older, newer]);

        Assert.Equal("2.0", list.GetLatestForParent("net.minecraft", "1.20.1")?.VersionString);
    }

    [Fact]
    public void NothingCompatibleGivesNothing()
    {
        var list = new VersionList("loader");
        list.SetVersions([new MetaVersion("loader", "1.0") { Type = "release" }]);

        Assert.Null(list.GetLatestForParent("net.minecraft", "1.20.1"));
    }

    // ================================================================== update actions

    [Fact]
    public void AnUpdateActionCanBeQueuedAndCleared()
    {
        var component = new Component("loader") { Version = "1.0" };

        Assert.IsType<UpdateAction.None>(component.UpdateAction);

        component.SetUpdateAction(new UpdateAction.ChangeVersion("2.0"));
        Assert.Equal("2.0", Assert.IsType<UpdateAction.ChangeVersion>(component.UpdateAction).TargetVersion);

        component.ClearUpdateAction();
        Assert.IsType<UpdateAction.None>(component.UpdateAction);
    }

    // ================================================================== customise round-trip

    private Index MojangIndex()
        => IndexWith(LoadedVersion(
            "net.minecraft",
            "1.20.1",
            new VersionFile { Uid = "net.minecraft", Version = "1.20.1", Name = "Mojang's Minecraft" }));

    /// <summary>A fresh component list, as reopening the instance (a fresh mmc-pack.json load) gives.</summary>
    private PackProfile FreshProfile() => ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

    [Fact]
    public async Task CustomisingProducesAPatchAFreshResolutionLoadsAsCustom()
    {
        /*
         * THE FEATURE END TO END, through the real resolution path. Resolve to attach the meta
         * version, customise (which writes the patch), then resolve a FRESH profile -- which is what
         * reopening the instance or launching it does, since both load mmc-pack.json into new
         * component objects. The patch Customize wrote is loaded as custom, meta link and all cut.
         */
        var index = MojangIndex();
        var profile = FreshProfile();

        Assert.True(await Task_(profile, index).RunAsync());
        Assert.False(profile.GetComponent("net.minecraft")!.IsCustom);

        Assert.True(profile.Customize(0, _temp));
        Assert.True(File.Exists(Path.Combine(_temp, "net.minecraft.json")));

        // Reopen: a fresh profile resolved against the same index.
        var reopened = FreshProfile();
        Assert.True(await Task_(reopened, index).RunAsync());

        var component = reopened.GetComponent("net.minecraft")!;

        Assert.True(component.IsCustom);
        Assert.Equal("Mojang's Minecraft", component.CachedName);
    }

    [Fact]
    public async Task AnEditToACustomisedPatchSurvivesResolution()
    {
        /*
         * The point of customising: once the file is local, the user's edit to it wins over what the
         * meta server would still say. Before customising, this resolution would re-impose the meta
         * name -- that it does not is the whole feature.
         */
        var index = MojangIndex();
        var profile = FreshProfile();

        Assert.True(await Task_(profile, index).RunAsync());
        Assert.True(profile.Customize(0, _temp));

        // A user edits the patch, then reopens the instance.
        WritePatch("net.minecraft", "1.20.1", "My Own Minecraft");

        var reopened = FreshProfile();
        Assert.True(await Task_(reopened, index).RunAsync());

        Assert.Equal("My Own Minecraft", reopened.GetComponent("net.minecraft")!.CachedName);
    }

    [Fact]
    public async Task RevertingLetsTheMetaServerWinAgain()
    {
        var index = MojangIndex();
        var profile = FreshProfile();

        Assert.True(await Task_(profile, index).RunAsync());
        Assert.True(profile.Customize(0, _temp));

        WritePatch("net.minecraft", "1.20.1", "My Own Minecraft");

        // Revert throws the patch away.
        Assert.True(profile.RevertToBase(0, _temp, index));
        Assert.False(File.Exists(Path.Combine(_temp, "net.minecraft.json")));

        // Reopen: with no patch on disk, the meta server's version takes over again.
        var reopened = FreshProfile();
        Assert.True(await Task_(reopened, index).RunAsync());

        var component = reopened.GetComponent("net.minecraft")!;

        Assert.False(component.IsCustom);
        Assert.Equal("Mojang's Minecraft", component.CachedName);
    }

    // ================================================================== unmet requirements at launch

    /*
     * A REAL-DATA PROBE (wave 66) resolved a Forge instance whose pack omitted org.lwjgl3 -- the shape
     * of a hand-edited or badly-imported instance -- against the live metadata server. The launch
     * reported "Minecraft is missing requirement org.lwjgl3" and carried on, which is upstream's
     * behaviour: Launch mode REPORTS unmet requirements, it does not add the missing component (that is
     * creation's job, ComponentResolution). This pins both halves so neither drifts: the error is
     * raised, and nothing is conjured into the list to paper over it.
     */
    [Fact]
    public async Task ALaunchWithAMissingRequiredComponentReportsAnErrorAndAddsNothing()
    {
        var mc = new VersionFile { Uid = "net.minecraft", Version = "1.20.1", Name = "Minecraft" };
        mc.Requires.Add(new Require("org.lwjgl3", suggests: "3.3.1"));

        var index = IndexWith(LoadedVersion("net.minecraft", "1.20.1", mc));
        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1" });

        Assert.True(await Task_(profile, index, ComponentUpdateMode.Launch).RunAsync());

        // Nothing was conjured into the list to satisfy the requirement.
        Assert.Null(profile.GetComponent("org.lwjgl3"));

        // The gap is an ERROR on Minecraft, naming what is missing -- not a silent warning.
        Assert.Contains(
            profile.GetComponent("net.minecraft")!.GetProblems(),
            p => p.Severity == ProblemSeverity.Error
                && p.Description.Contains("org.lwjgl3", StringComparison.Ordinal));
    }
}
