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
 * The global settings window's behaviour.
 *
 * ASSERTED ON extremelauncher.cfg wherever the question is "did that get saved". Reading it back
 * through the same view model that wrote it proves only that the pair agree with each other, and the
 * config file is a compatibility surface -- an existing Prism or MultiMC folder has this exact shape.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class GlobalSettingsViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "el-gset-" + Guid.NewGuid().ToString("N"));

    public GlobalSettingsViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ConfigPath => Path.Combine(_folder, "extremelauncher.cfg");

    private SettingsObject NewSettings() => GlobalSettings.Create(ConfigPath);

    private static SettingViewModel Find(GlobalSettingsViewModel vm, string key)
        => vm.Sections.SelectMany(s => s.Settings).Single(s => s.Key == key);

    [Fact]
    public void EverySettingTheLauncherRegistersIsEditable()
    {
        /*
         * THE POINT OF THE WHOLE WINDOW. Every global was registered, defaulted and tested waves ago
         * and none of them could be edited -- the only way to change one was to hand-write the cfg.
         * This walks the registered keys rather than a list written by hand, so a setting added later
         * and forgotten here fails the test instead of quietly having no UI.
         */
        var settings = NewSettings();
        var vm = new GlobalSettingsViewModel(settings);

        var shown = vm.Sections.SelectMany(s => s.Settings).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var missing = settings.Settings
            .Select(s => s.Id)
            .Where(key => !shown.Contains(key))

            // A short, documented list of keys that exist only to be migrated away.
            .Where(key => !GlobalSettingsViewModel.NotEditableOnPurpose.Contains(key))
            .ToArray();

        Assert.True(missing.Length == 0, "not editable anywhere: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheExemptionListIsExactlyWhatItSaysItIs()
    {
        /*
         * A GUARD ON THE GUARD. The coverage test above is only as strong as this list is short, and
         * the easy way to make a failing coverage test pass is to add a key here. Pinning the
         * contents means doing that is a deliberate, reviewable act rather than a one-word fix.
         */
        Assert.Equal(["PastebinURL"], GlobalSettingsViewModel.NotEditableOnPurpose.Order());
    }

    [Fact]
    public void NoSettingIsShownTwice()
    {
        // Two boxes for one key means whichever is written second wins, silently.
        var vm = new GlobalSettingsViewModel(NewSettings());

        var keys = vm.Sections.SelectMany(s => s.Settings).Select(s => s.Key).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EverySettingStartsEditable()
    {
        // Unlike the instance page, there is no override box to tick: a global is always editable.
        var vm = new GlobalSettingsViewModel(NewSettings());

        Assert.All(vm.Sections.SelectMany(s => s.Settings), s => Assert.True(s.IsEditable));
    }

    [Fact]
    public void TheWindowOpensShowingTheValuesFromTheFile()
    {
        var settings = NewSettings();

        settings.Set("JvmArgs", "-XX:+UseG1GC");
        settings.Set("MaxMemAlloc", 6144);

        var vm = new GlobalSettingsViewModel(settings);

        Assert.Equal("-XX:+UseG1GC", ((TextSettingViewModel)Find(vm, "JvmArgs")).Value);
        Assert.Equal(6144, ((NumberSettingViewModel)Find(vm, "MaxMemAlloc")).Value);
    }

    [Fact]
    public void SavingWritesThroughToTheFile()
    {
        var vm = new GlobalSettingsViewModel(NewSettings());

        ((TextSettingViewModel)Find(vm, "JvmArgs")).Value = "-Xss2M";
        ((NumberSettingViewModel)Find(vm, "MaxMemAlloc")).Value = 8192;
        ((ToggleSettingViewModel)Find(vm, "ShowConsole")).Value = true;

        Assert.True(vm.Save());

        // Read with a FRESH settings object, so nothing in the writer's memory can answer for it.
        var reread = GlobalSettings.Create(ConfigPath);

        Assert.Equal("-Xss2M", reread.Get("JvmArgs")?.ToString());
        Assert.Equal("8192", reread.Get("MaxMemAlloc")?.ToString());

        var raw = File.ReadAllText(ConfigPath);

        Assert.Contains("JvmArgs", raw, StringComparison.Ordinal);
        Assert.Contains("-Xss2M", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingSomethingMarksTheWindowDirty()
    {
        var vm = new GlobalSettingsViewModel(NewSettings());

        Assert.False(vm.HasUnsavedChanges);

        ((TextSettingViewModel)Find(vm, "JvmArgs")).Value = "-Xss2M";

        Assert.True(vm.HasUnsavedChanges);

        vm.Save();

        // Clean again after saving, or the window warns about losing changes that are on disk.
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void RevertingThrowsAwayTheEditsAndPutsTheFileValuesBack()
    {
        var settings = NewSettings();

        settings.Set("JvmArgs", "-Xmx1G");

        var vm = new GlobalSettingsViewModel(settings);

        ((TextSettingViewModel)Find(vm, "JvmArgs")).Value = "something else";

        Assert.True(vm.HasUnsavedChanges);

        vm.Revert();

        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("-Xmx1G", ((TextSettingViewModel)Find(vm, "JvmArgs")).Value);
    }

    [Fact]
    public void SettingAValueBackToWhatItWasIsNotAChange()
    {
        // Otherwise the window nags about unsaved changes for an edit that undid itself.
        var settings = NewSettings();

        settings.Set("JvmArgs", "-Xmx1G");

        var vm = new GlobalSettingsViewModel(settings);
        var field = (TextSettingViewModel)Find(vm, "JvmArgs");

        field.Value = "-Xmx2G";

        Assert.True(vm.HasUnsavedChanges);

        field.Value = "-Xmx1G";

        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void SavedStatusIsClearedAsSoonAsSomethingIsEditedAgain()
    {
        // "Saved." sitting above unsaved edits is worse than no message at all.
        var vm = new GlobalSettingsViewModel(NewSettings());

        ((TextSettingViewModel)Find(vm, "JvmArgs")).Value = "-Xss2M";
        vm.Save();

        Assert.Equal("Saved.", vm.Status);

        ((TextSettingViewModel)Find(vm, "JvmArgs")).Value = "-Xss4M";

        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public void AGlobalChangedHereIsWhatANonOverridingInstanceThenUses()
    {
        /*
         * The end-to-end point of the window: an instance that overrides nothing must pick up the new
         * global. The instance settings page has offered overrides this whole time for values nobody
         * could set, so this is the half that was missing.
         */
        var globals = NewSettings();
        var vm = new GlobalSettingsViewModel(globals);

        ((NumberSettingViewModel)Find(vm, "MaxMemAlloc")).Value = 9001;

        Assert.True(vm.Save());

        var instanceFolder = Path.Combine(_folder, "instance");

        Directory.CreateDirectory(instanceFolder);

        var instance = new InstanceSettings(
            new IniSettingsObject(Path.Combine(instanceFolder, "instance.cfg")),
            globals);

        Assert.Equal(9001, instance.MaxMemAlloc);
    }

    [Fact]
    public void TheMetadataUrlIsEditableBecauseTheBuiltInDefaultDoesNotResolve()
    {
        /*
         * Not a nicety on this fork. BuildConfig points at meta.extremelauncher.net, which does not
         * resolve, so an unconfigured launcher fails every resolve -- and until this window there was
         * no way to change it except --meta on the command line or hand-editing the cfg.
         */
        var settings = NewSettings();
        var vm = new GlobalSettingsViewModel(settings);

        ((TextSettingViewModel)Find(vm, "MetaURLOverride")).Value = "https://meta.prismlauncher.org/";

        Assert.True(vm.Save());

        Assert.Equal(
            "https://meta.prismlauncher.org/",
            GlobalSettings.ResolveMetaUrl(GlobalSettings.Create(ConfigPath)));
    }
}
