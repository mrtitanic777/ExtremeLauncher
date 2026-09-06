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
 * Characterization tests for PackProfile. There is no upstream Qt test for this file, but the
 * mmc-pack.json format is on every existing instance's disk, so the shape is pinned closely.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class PackProfileTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-pack-" + Guid.NewGuid().ToString("N"));

    public PackProfileTests() => Directory.CreateDirectory(_temp);

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

    private static PackProfile NewProfile() => new(Context());

    private static Component Backed(string uid, string version, Action<VersionFile>? configure = null)
    {
        var file = new VersionFile { Uid = uid, Version = version, Name = uid };
        configure?.Invoke(file);

        return new Component(uid)
        {
            Version = version,
            MetaVersion = new MetaVersion(uid, version) { Data = file },
        };
    }

    // ================================================================== ordering

    [Fact]
    public void ComponentsApplyInOrder()
    {
        var profile = NewProfile();

        profile.AppendComponent(Backed("net.minecraft", "1.20.1", f =>
        {
            f.MainClass = "net.minecraft.client.main.Main";
            f.Type = "release";
        }));

        profile.AppendComponent(Backed("net.minecraftforge", "47.1.0", f =>
            f.MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher"));

        var launch = profile.GetProfile();

        // Later wins: Forge overrides Minecraft's main class without either knowing about the other.
        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", launch.MainClass);
        Assert.Equal("1.20.1", launch.MinecraftVersion);
    }

    [Fact]
    public void MovingAComponentChangesWhichOneWins()
    {
        var profile = NewProfile();
        profile.AppendComponent(Backed("a", "1", f => f.MainClass = "First"));
        profile.AppendComponent(Backed("b", "1", f => f.MainClass = "Second"));

        Assert.Equal("Second", profile.GetProfile().MainClass);

        profile.Move(1, PackProfile.MoveDirection.Up);

        Assert.Equal("First", profile.GetProfile().MainClass);
    }

    /*
     * UPSTREAM BUG #19, NOT REPRODUCED -- this port stops at the ends instead.
     *
     * PackProfile::move clamps its target index in a way that WRAPS in one direction only:
     *
     *     if (theirIndex >= rowCount()) theirIndex = rowCount() - 1;   // down from the last: no-op
     *     if (theirIndex == -1)         theirIndex = rowCount() - 1;   // up from the first: LAST ROW
     *
     * So Move Up on the top component swaps it with the BOTTOM one, while Move Down on the bottom does
     * nothing at all. It is reachable: Component::isMoveable() is a hardcoded `true` under its own
     * "HACK, FIXME", so the button is enabled on the first row. Component order decides which patch
     * wins, so sending Minecraft from the top to the bottom is not cosmetic.
     *
     * Three components, so wrapping is unmistakable -- with two, a wrap and an ordinary swap look
     * identical.
     */
    [Fact]
    public void MovingPastEitherEndDoesNothingRatherThanWrapping()
    {
        var profile = NewProfile();
        profile.AppendComponent(Backed("a", "1"));
        profile.AppendComponent(Backed("b", "1"));
        profile.AppendComponent(Backed("c", "1"));

        // Upstream would swap "a" with "c" here.
        profile.Move(0, PackProfile.MoveDirection.Up);

        Assert.Equal(["a", "b", "c"], profile.Components.Select(c => c.Uid));

        profile.Move(2, PackProfile.MoveDirection.Down);
        profile.Move(99, PackProfile.MoveDirection.Up);

        Assert.Equal(["a", "b", "c"], profile.Components.Select(c => c.Uid));
    }

    [Fact]
    public void TheLaunchProfileIsCachedUntilInvalidated()
    {
        var profile = NewProfile();
        profile.AppendComponent(Backed("a", "1", f => f.MainClass = "First"));

        var first = profile.GetProfile();
        Assert.Same(first, profile.GetProfile());

        // Any mutation drops the cache.
        profile.AppendComponent(Backed("b", "1", f => f.MainClass = "Second"));
        Assert.NotSame(first, profile.GetProfile());
    }

    // ================================================================== membership

    [Fact]
    public void DuplicateUidsAreRejected()
    {
        var profile = NewProfile();

        Assert.Equal(0, profile.InsertComponent(0, Backed("a", "1")));
        Assert.Equal(-1, profile.InsertComponent(0, Backed("a", "2")));
        Assert.Equal(1, profile.Count);
    }

    [Fact]
    public void ImportantComponentsCannotBeRemoved()
    {
        var profile = NewProfile();

        var minecraft = Backed("net.minecraft", "1.20.1");
        minecraft.IsImportant = true;
        profile.AppendComponent(minecraft);
        profile.AppendComponent(Backed("some.mod", "1.0"));

        Assert.False(profile.Remove("net.minecraft"));
        Assert.True(profile.Remove("some.mod"));
        Assert.Equal(1, profile.Count);
    }

    [Fact]
    public void SettingAVersionAddsTheComponentWhenMissing()
    {
        var profile = NewProfile();

        Assert.True(profile.SetComponentVersion("net.minecraft", "1.20.1", important: true));
        Assert.Equal("1.20.1", profile.GetComponentVersion("net.minecraft"));
        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);

        Assert.True(profile.SetComponentVersion("net.minecraft", "1.21"));
        Assert.Equal("1.21", profile.GetComponentVersion("net.minecraft"));
        Assert.Equal(1, profile.Count);
    }

    [Fact]
    public void AnUnknownComponentVersionIsEmpty()
        => Assert.Equal(string.Empty, NewProfile().GetComponentVersion("nope"));

    [Fact]
    public void DisabledComponentsAreSkippedWhenBuildingTheProfile()
    {
        var profile = NewProfile();

        profile.AppendComponent(Backed("a", "1", f => f.MainClass = "Kept"));

        var disabled = Backed("b", "1", f => f.MainClass = "Skipped");
        disabled.IsDisabled = true;
        profile.AppendComponent(disabled);

        Assert.Equal("Kept", profile.GetProfile().MainClass);
    }

    // ================================================================== persistence

    [Fact]
    public void RoundTripsThroughMmcPackJson()
    {
        var path = Path.Combine(_temp, "mmc-pack.json");

        var profile = NewProfile();

        var minecraft = Backed("net.minecraft", "1.20.1");
        minecraft.IsImportant = true;
        minecraft.UpdateCachedData();
        profile.AppendComponent(minecraft);

        var loader = Backed("net.fabricmc.fabric-loader", "0.14.21", f =>
        {
            f.Requires.Add(new Require("net.minecraft", "1.20.1"));
            f.IsVolatile = true;
        });
        loader.IsDependencyOnly = true;
        loader.UpdateCachedData();
        profile.AppendComponent(loader);

        Assert.True(profile.Save(path));

        var reloaded = NewProfile();
        Assert.True(reloaded.Load(path));

        Assert.Equal(2, reloaded.Count);
        Assert.Equal(["net.minecraft", "net.fabricmc.fabric-loader"], reloaded.Components.Select(c => c.Uid));

        var restoredMinecraft = reloaded.GetComponent("net.minecraft")!;
        Assert.Equal("1.20.1", restoredMinecraft.Version);
        Assert.True(restoredMinecraft.IsImportant);

        var restoredLoader = reloaded.GetComponent("net.fabricmc.fabric-loader")!;
        Assert.True(restoredLoader.IsDependencyOnly);
        Assert.Single(restoredLoader.CachedRequires);
        Assert.Equal("1.20.1", restoredLoader.CachedRequires.First().EqualsVersion);
    }

    /// <remarks>
    /// UPSTREAM BUG. componentToJsonV1 writes "cachedVolatile" but componentFromJsonV1 reads
    /// "volatile", so the flag never survived a reload and volatile components were never cleaned up.
    /// Both keys are read here.
    /// </remarks>
    [Fact]
    public void TheVolatileFlagSurvivesAReload()
    {
        var path = Path.Combine(_temp, "volatile.json");

        var profile = NewProfile();
        var component = Backed("net.fabricmc.intermediary", "1.20.1", f => f.IsVolatile = true);
        component.UpdateCachedData();
        profile.AppendComponent(component);

        Assert.True(component.CachedVolatile);
        Assert.True(profile.Save(path));

        var reloaded = NewProfile();
        Assert.True(reloaded.Load(path));

        Assert.True(reloaded.GetComponent("net.fabricmc.intermediary")!.CachedVolatile);
    }

    [Fact]
    public void TheLegacyVolatileKeyIsStillHonoured()
    {
        // Files written before the fix used the "volatile" spelling on read; accept both.
        var profile = NewProfile();

        Assert.True(profile.LoadFromJson(Json.RequireObject(Json.RequireDocument("""
            { "formatVersion": 1, "components": [ { "uid": "a", "volatile": true } ] }
            """))));

        Assert.True(profile.GetComponent("a")!.CachedVolatile);
    }

    [Fact]
    public void DefaultValuesAreOmittedFromTheFile()
    {
        var profile = NewProfile();
        profile.AppendComponent(new Component("plain") { Version = "1.0" });

        var written = profile.ToJson();
        var component = Json.RequireArray(written, "components")[0]!.AsObject();

        Assert.True(component.ContainsKey("uid"));
        Assert.True(component.ContainsKey("version"));
        Assert.False(component.ContainsKey("important"));
        Assert.False(component.ContainsKey("disabled"));
        Assert.False(component.ContainsKey("dependencyOnly"));
        Assert.False(component.ContainsKey("cachedVolatile"));
    }

    [Fact]
    public void AWrongFormatVersionIsRejected()
    {
        var profile = NewProfile();

        Assert.False(profile.LoadFromJson(Json.RequireObject(Json.RequireDocument("""
            { "formatVersion": 99, "components": [ { "uid": "a" } ] }
            """))));

        // A rejected file leaves an empty profile, never a half-loaded one.
        Assert.Equal(0, profile.Count);
    }

    [Fact]
    public void AMalformedComponentAbandonsTheWholeLoad()
    {
        var profile = NewProfile();

        Assert.False(profile.LoadFromJson(Json.RequireObject(Json.RequireDocument("""
            { "formatVersion": 1, "components": [ { "uid": "good" }, { "no-uid": true } ] }
            """))));

        Assert.Equal(0, profile.Count);
    }

    [Fact]
    public void LoadingAMissingFileFails()
        => Assert.False(NewProfile().Load(Path.Combine(_temp, "does-not-exist.json")));

    [Fact]
    public void PatchFilePathsFollowTheUid()
        => Assert.EndsWith(
            "net.minecraft.json",
            PackProfile.PatchFilePathForUid("/instances/foo/patches", "net.minecraft"),
            StringComparison.Ordinal);

    // ================================================================== install empty

    [Fact]
    public void InstallEmptyAddsACustomComponentAndWritesItsPatch()
    {
        var patches = Path.Combine(_temp, "patches");
        var profile = NewProfile();

        Assert.True(profile.InstallEmpty("com.example.tweaks", "My Tweaks", patches));

        var component = profile.GetComponent("com.example.tweaks");

        Assert.NotNull(component);
        Assert.True(component!.IsCustom);
        Assert.Equal("My Tweaks", component.Name);

        // The patch is on disk, named for the uid, and reads back as a real version file.
        var patch = Path.Combine(patches, "com.example.tweaks.json");
        Assert.True(File.Exists(patch));
        Assert.Contains("com.example.tweaks", File.ReadAllText(patch), StringComparison.Ordinal);
    }

    [Fact]
    public void InstallEmptyRefusesADuplicateUid()
    {
        var patches = Path.Combine(_temp, "patches");
        var profile = NewProfile();
        profile.AppendComponent(new Component("net.minecraft") { Version = "1.20.1" });

        // net.minecraft is already there; a second component with that uid is refused.
        Assert.False(profile.InstallEmpty("net.minecraft", "Impostor", patches));
        Assert.Single(profile.Components);
    }
}
