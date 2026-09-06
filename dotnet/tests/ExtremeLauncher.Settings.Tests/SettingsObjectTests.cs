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
 * Characterization tests for the settings layer. There is no upstream Qt test for SettingsObject.
 */

using Xunit;

namespace ExtremeLauncher.Settings.Tests;

public sealed class SettingsObjectTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsObjectTests() => Directory.CreateDirectory(_temp);

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

    private IniSettingsObject NewStore(string name = "settings.cfg")
        => new(Path.Combine(_temp, name));

    [Fact]
    public void UnsetSettingsReadTheirDefault()
    {
        var store = NewStore();
        store.RegisterSetting("MaxMemAlloc", 4096);

        Assert.Equal(4096, store.Get("MaxMemAlloc"));
    }

    [Fact]
    public void SetValuesRoundTripThroughDisk()
    {
        var path = Path.Combine(_temp, "roundtrip.cfg");

        var store = new IniSettingsObject(path);
        store.RegisterSetting("Name", "default");
        Assert.True(store.Set("Name", "custom"));

        // A fresh object over the same file must see it.
        var reloaded = new IniSettingsObject(path);
        reloaded.RegisterSetting("Name", "default");

        Assert.Equal("custom", reloaded.GetString("Name"));
    }

    [Fact]
    public void ResetRestoresTheDefaultAndRemovesTheKey()
    {
        var store = NewStore();
        store.RegisterSetting("Name", "default");

        store.Set("Name", "custom");
        Assert.Equal("custom", store.GetString("Name"));

        store.Reset("Name");
        Assert.Equal("default", store.GetString("Name"));
    }

    [Fact]
    public void SettingANullValueUnsetsIt()
    {
        var store = NewStore();
        store.RegisterSetting("Name", "default");

        store.Set("Name", "custom");
        store.Set("Name", null);

        Assert.Equal("default", store.GetString("Name"));
    }

    [Fact]
    public void DuplicateRegistrationIsRejected()
    {
        var store = NewStore();

        Assert.NotNull(store.RegisterSetting("Name", "a"));
        Assert.Null(store.RegisterSetting("Name", "b"));
    }

    [Fact]
    public void UnknownSettingsAreInert()
    {
        var store = NewStore();

        Assert.False(store.Set("Nope", "x"));
        Assert.Null(store.Get("Nope"));
        Assert.False(store.Contains("Nope"));
    }

    [Fact]
    public void SynonymsAreReadableAndCollapseOnWrite()
    {
        var path = Path.Combine(_temp, "synonyms.cfg");

        // An old file written under the previous key name.
        var seed = new IniFile();
        seed.Set("OldName", "value");
        seed.SaveFile(path);

        var store = new IniSettingsObject(path);
        store.RegisterSetting(["NewName", "OldName"], "default");

        // Reads accept either spelling.
        Assert.Equal("value", store.GetString("NewName"));

        // Writing migrates to the canonical key and drops the synonym.
        store.Set("NewName", "updated");

        var onDisk = new IniFile();
        onDisk.LoadFile(path);

        Assert.Equal("updated", onDisk.GetString("NewName"));
        Assert.False(onDisk.Contains("OldName"));
    }

    [Fact]
    public void ChangeEventsFire()
    {
        var store = NewStore();
        store.RegisterSetting("Name", "default");

        var changes = new List<(string Id, object? Value)>();
        store.SettingChanged += (_, e) => changes.Add((e.Setting.Id, e.Value));

        store.Set("Name", "one");
        store.Set("Name", "two");

        Assert.Equal([("Name", "one"), ("Name", "two")], changes);
    }

    [Fact]
    public void ResetEventsFire()
    {
        var store = NewStore();
        store.RegisterSetting("Name", "default");

        var resets = 0;
        store.SettingReset += (_, _) => resets++;

        store.Reset("Name");

        Assert.Equal(1, resets);
    }

    [Fact]
    public void SuspendSaveBatchesWritesUntilResume()
    {
        var path = Path.Combine(_temp, "batched.cfg");

        var store = new IniSettingsObject(path);
        store.RegisterSetting("A", "x");
        store.RegisterSetting("B", "y");

        store.SuspendSave();
        store.Set("A", "1");
        store.Set("B", "2");

        // Nothing has hit disk yet.
        var duringSuspend = new IniFile();
        duringSuspend.LoadFile(path);
        Assert.NotEqual("1", duringSuspend.GetString("A"));

        store.ResumeSave();

        var afterResume = new IniFile();
        afterResume.LoadFile(path);
        Assert.Equal("1", afterResume.GetString("A"));
        Assert.Equal("2", afterResume.GetString("B"));
    }

    [Fact]
    public void TypedAccessorsCoerceFromStrings()
    {
        var path = Path.Combine(_temp, "typed.cfg");

        var seed = new IniFile();
        seed.Set("Flag", "true");
        seed.Set("Count", "42");
        seed.SaveFile(path);

        var store = new IniSettingsObject(path);
        store.RegisterSetting("Flag", false);
        store.RegisterSetting("Count", 0);

        // Everything comes back from the INI as a string, so the accessors have to coerce.
        Assert.True(store.GetBool("Flag"));
        Assert.Equal(42, store.GetInt("Count"));
    }

    // ================================================================== override / passthrough

    [Fact]
    public void OverrideDefersToTheGlobalWhileTheGateIsOff()
    {
        var globals = NewStore("global.cfg");
        var global = globals.RegisterSetting("JavaPath", "/usr/bin/java")!;

        var instance = NewStore("instance.cfg");
        var gate = instance.RegisterSetting("OverrideJava", false)!;
        instance.RegisterOverride(global, gate);

        // Gate off: the global value shows through.
        Assert.Equal("/usr/bin/java", instance.GetString("JavaPath"));

        // Writing while the gate is off STORES the value but does not surface it -- Get() defers to
        // the underlying setting unconditionally. This is what lets the UI keep a per-instance value
        // parked behind an unchecked "override" box without it taking effect.
        instance.Set("JavaPath", "/opt/jdk/bin/java");
        Assert.Equal("/usr/bin/java", instance.GetString("JavaPath"));

        // The global is never written to either way.
        Assert.Equal("/usr/bin/java", globals.GetString("JavaPath"));

        // Flipping the gate reveals the parked value.
        instance.Set("OverrideJava", true);
        Assert.Equal("/opt/jdk/bin/java", instance.GetString("JavaPath"));
    }

    [Fact]
    public void OverrideUsesItsOwnValueWhenTheGateIsOn()
    {
        var globals = NewStore("global2.cfg");
        var global = globals.RegisterSetting("JavaPath", "/usr/bin/java")!;

        var instance = NewStore("instance2.cfg");
        var gate = instance.RegisterSetting("OverrideJava", false)!;
        instance.RegisterOverride(global, gate);

        instance.Set("JavaPath", "/opt/jdk/bin/java");
        instance.Set("OverrideJava", true);

        Assert.Equal("/opt/jdk/bin/java", instance.GetString("JavaPath"));

        // Turning the gate back off falls through to the global again.
        instance.Set("OverrideJava", false);
        Assert.Equal("/usr/bin/java", instance.GetString("JavaPath"));
    }

    [Fact]
    public void PassthroughWritesReachTheUnderlyingSetting()
    {
        var globals = NewStore("global3.cfg");
        var global = globals.RegisterSetting("Theme", "dark")!;

        var instance = NewStore("instance3.cfg");
        instance.RegisterPassthrough(global, gate: null);

        instance.Set("Theme", "light");

        // Unlike an override, the write propagates to the global.
        Assert.Equal("light", globals.GetString("Theme"));
        Assert.Equal("light", instance.GetString("Theme"));
    }

    [Fact]
    public void PassthroughResetReachesTheUnderlyingSetting()
    {
        var globals = NewStore("global4.cfg");
        var global = globals.RegisterSetting("Theme", "dark")!;

        var instance = NewStore("instance4.cfg");
        instance.RegisterPassthrough(global, gate: null);

        instance.Set("Theme", "light");
        instance.Reset("Theme");

        Assert.Equal("dark", globals.GetString("Theme"));
    }

    [Fact]
    public void UnattachedSettingsReturnTheirDefault()
    {
        // No SettingsObject, so there is nowhere to read from.
        var orphan = new Setting("Loose", "fallback");

        Assert.Equal("fallback", orphan.Get());
    }
}
