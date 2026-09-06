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
 * EVERY SETTING HERE IS INHERITED UNTIL A BOX IS TICKED, and almost every test below is about that one
 * fact: what an unticked group shows, what ticking one starts from, and what unticking one leaves
 * behind on disk.
 *
 * The last of those is the one that bites. Unticking a box must leave the instance inheriting again --
 * if the values are written anyway, instance.cfg ends up full of keys that look like overrides, and
 * the next launcher to read the file may well treat them as such.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class SettingsPageViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-set-" + Guid.NewGuid().ToString("N"));

    private readonly string _instanceCfg;

    private readonly string _launcherCfg;

    public SettingsPageViewModelTests()
    {
        Directory.CreateDirectory(_temp);

        _instanceCfg = Path.Combine(_temp, "instance.cfg");
        _launcherCfg = Path.Combine(_temp, "launcher.cfg");

        File.WriteAllText(_instanceCfg, "name=My Pack\nInstanceType=OneSix\n");
    }

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

    private SettingsObject Globals(params (string Key, object Value)[] values)
    {
        var globals = GlobalSettings.Create(_launcherCfg);

        foreach (var (key, value) in values)
        {
            globals.Set(key, value);
        }

        return globals;
    }

    private InstanceSettings NewInstanceSettings(SettingsObject globals)
        => new(new IniSettingsObject(_instanceCfg), globals);

    private SettingsPageViewModel Load(SettingsObject? globals = null)
    {
        var page = new SettingsPageViewModel();

        page.Load(NewInstanceSettings(globals ?? Globals()));

        return page;
    }

    private static SettingsGroupViewModel Group(SettingsPageViewModel page, string title)
        => page.Groups.Single(g => g.Title == title);

    private static T Setting<T>(SettingsGroupViewModel group, string key)
        where T : SettingViewModel
        => (T)group.Settings.Single(s => s.Key == key);

    // ================================================================== inheriting

    /*
     * AN UNTICKED GROUP SHOWS THE VALUES THE INSTANCE IS ACTUALLY RUNNING WITH, not blanks. Blanks
     * would suggest the instance had no memory limit at all.
     */
    [Fact]
    public void AnInheritingGroupShowsTheGlobalValues()
    {
        var page = Load(Globals(("MaxMemAlloc", 4096), ("MinMemAlloc", 512)));

        var memory = Group(page, "Memory");

        Assert.False(memory.IsOverriding);
        Assert.Equal(4096, Setting<NumberSettingViewModel>(memory, "MaxMemAlloc").Value);
        Assert.Equal(512, Setting<NumberSettingViewModel>(memory, "MinMemAlloc").Value);
    }

    /// <summary>...and shows them as not editable, so nobody types into a field that will be ignored.</summary>
    [Fact]
    public void AnInheritingGroupsFieldsAreNotEditable()
    {
        var page = Load(Globals(("MaxMemAlloc", 4096)));

        Assert.All(Group(page, "Memory").Settings, s => Assert.False(s.IsEditable));
    }

    [Fact]
    public void TickingAGroupMakesItsFieldsEditable()
    {
        var page = Load();

        var memory = Group(page, "Memory");
        memory.IsOverriding = true;

        Assert.All(memory.Settings, s => Assert.True(s.IsEditable));
    }

    /*
     * TICKING A BOX STARTS FROM WHAT THE INSTANCE WAS ALREADY USING. OverrideSetting's DefaultValue is
     * the global value, which is what makes ticking one harmless -- a user who ticks "Memory" and
     * saves has not silently changed how much memory the game gets.
     */
    [Fact]
    public void TickingAGroupAndSavingKeepsTheInheritedValues()
    {
        var globals = Globals(("MaxMemAlloc", 4096));
        var page = Load(globals);

        Group(page, "Memory").IsOverriding = true;

        Assert.True(page.Save());

        // Read the file back through a fresh settings object: what is on disk is what launches.
        var reread = NewInstanceSettings(globals);

        Assert.Equal(4096, reread.MaxMemAlloc);
    }

    // ================================================================== overriding

    [Fact]
    public void AnOverriddenValueIsWrittenAndSurvivesAReload()
    {
        var globals = Globals(("MaxMemAlloc", 4096));
        var page = Load(globals);

        var memory = Group(page, "Memory");

        memory.IsOverriding = true;
        Setting<NumberSettingViewModel>(memory, "MaxMemAlloc").Value = 8192;

        Assert.True(page.Save());

        Assert.Equal(8192, NewInstanceSettings(globals).MaxMemAlloc);

        // The global is untouched: this is an instance override, not a change to everyone's default.
        Assert.Equal(4096, Convert.ToInt32(globals.Get("MaxMemAlloc"), null));
    }

    [Fact]
    public void TextAndToggleSettingsRoundTrip()
    {
        var globals = Globals();
        var page = Load(globals);

        var java = Group(page, "Java");

        java.IsOverriding = true;
        Setting<TextSettingViewModel>(java, "JavaPath").Value = "C:/Java/jdk-21/bin/javaw.exe";
        Setting<ToggleSettingViewModel>(java, "IgnoreJavaCompatibility").Value = true;

        Assert.True(page.Save());

        var reread = NewInstanceSettings(globals);

        Assert.Equal("C:/Java/jdk-21/bin/javaw.exe", reread.JavaPath);
        Assert.True(reread.IgnoreJavaCompatibility);
    }

    /*
     * THE GATE FOR IgnoreJavaCompatibility IS "OverrideJavaLocation", which is not a location. That is
     * upstream's grouping and it is deliberate -- an instance pinned to a specific JVM is exactly the
     * case where the user also wants to say "run it anyway". Grouping them apart on screen would let
     * someone tick a box that silently does not apply.
     */
    [Fact]
    public void IgnoreCompatibilityLivesBehindTheJavaLocationGate()
    {
        var page = Load();

        var java = Group(page, "Java");

        Assert.Equal("OverrideJavaLocation", java.GateKey);
        Assert.Contains(java.Settings, s => s.Key == "IgnoreJavaCompatibility");
    }

    // ================================================================== stopping overriding

    /*
     * THE ONE THAT BITES. Unticking must leave the instance INHERITING again -- and the values must not
     * be written anyway, or instance.cfg fills with keys that look like overrides and the next launcher
     * to read it may treat them as such.
     */
    [Fact]
    public async Task UntickingAGroupReturnsTheInstanceToTheGlobalValue()
    {
        var globals = Globals(("MaxMemAlloc", 4096));

        // Start from an instance that really is overriding.
        var first = Load(globals);
        var memory = Group(first, "Memory");

        memory.IsOverriding = true;
        Setting<NumberSettingViewModel>(memory, "MaxMemAlloc").Value = 8192;

        Assert.True(first.Save());
        Assert.Equal(8192, NewInstanceSettings(globals).MaxMemAlloc);

        // Now untick it.
        var second = Load(globals);
        Group(second, "Memory").IsOverriding = false;

        Assert.True(second.Save());

        await Task.CompletedTask.ConfigureAwait(true);

        // Back to the global value, and following it if it changes again.
        var reread = NewInstanceSettings(globals);

        Assert.Equal(4096, reread.MaxMemAlloc);

        globals.Set("MaxMemAlloc", 2048);

        Assert.Equal(2048, NewInstanceSettings(globals).MaxMemAlloc);

        /*
         * AND THE STALE VALUE IS NOT LEFT IN THE FILE. This is the assertion that actually pins the
         * "only write while overriding" rule: reading through OverrideSetting returns the global
         * either way, so a version that wrote the values regardless passed every other check here
         * while leaving instance.cfg full of keys that look like overrides -- which the next launcher
         * to read the file may well treat as such. Verified by removing the guard and watching this
         * line, and only this line, fail.
         */
        Assert.DoesNotContain("8192", File.ReadAllText(_instanceCfg), StringComparison.Ordinal);
    }

    /// <summary>Unticking shows the global values again, not the numbers that were on screen.</summary>
    [Fact]
    public void SavingAfterUntickingShowsWhatTheInstanceNowUses()
    {
        var globals = Globals(("MaxMemAlloc", 4096));
        var page = Load(globals);

        var memory = Group(page, "Memory");

        memory.IsOverriding = true;
        Setting<NumberSettingViewModel>(memory, "MaxMemAlloc").Value = 8192;
        memory.IsOverriding = false;

        Assert.True(page.Save());

        Assert.Equal(4096, Setting<NumberSettingViewModel>(memory, "MaxMemAlloc").Value);
    }

    // ================================================================== the page contract

    [Fact]
    public void AFreshlyLoadedPageHasNothingToSave()
        => Assert.False(Load(Globals(("MaxMemAlloc", 4096))).HasUnsavedChanges);

    [Fact]
    public void TickingABoxCountsAsAChange()
    {
        var page = Load();

        Group(page, "Memory").IsOverriding = true;

        Assert.True(page.HasUnsavedChanges);
    }

    [Fact]
    public void EditingAValueInAnOverridingGroupCountsAsAChange()
    {
        var page = Load();

        var memory = Group(page, "Memory");

        memory.IsOverriding = true;

        Assert.True(page.Save());
        Assert.False(page.HasUnsavedChanges);

        Setting<NumberSettingViewModel>(memory, "MaxMemAlloc").Value = 9999;

        Assert.True(page.HasUnsavedChanges);
    }

    /*
     * Editing a field in an INHERITING group is not a change to save. The field is not editable in the
     * UI, but the view model must agree -- otherwise the window would prompt about unsaved changes that
     * would not be written.
     */
    [Fact]
    public void EditingAnInheritingGroupsFieldIsNotAChange()
    {
        var page = Load(Globals(("MaxMemAlloc", 4096)));

        Setting<NumberSettingViewModel>(Group(page, "Memory"), "MaxMemAlloc").Value = 9999;

        Assert.False(page.HasUnsavedChanges);
    }

    [Fact]
    public void SavingClearsTheUnsavedFlag()
    {
        var page = Load();

        Group(page, "Memory").IsOverriding = true;

        Assert.True(page.Save());
        Assert.False(page.HasUnsavedChanges);
    }
}
