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
 * Choosing a theme, and the choice editor it is the first user of.
 *
 * ASSERTED ON extremelauncher.cfg for the value, because what goes in the file is a compatibility
 * surface -- an id, not the wording of a label that may be reworded tomorrow.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ThemeSettingTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "el-theme-" + Guid.NewGuid().ToString("N"));

    public ThemeSettingTests() => Directory.CreateDirectory(_folder);

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

    private static ChoiceSettingViewModel ThemeIn(GlobalSettingsViewModel vm)
        => vm.Sections
            .SelectMany(s => s.Settings)
            .OfType<ChoiceSettingViewModel>()
            .Single(s => s.Key == "ApplicationTheme");

    [Fact]
    public void FollowingTheSystemIsTheDefault()
    {
        // What somebody who has never opened the settings should get.
        Assert.Equal("system", NewSettings().GetString("ApplicationTheme"));
    }

    [Fact]
    public void TheThemeIsOfferedAsAChoiceRatherThanATextBox()
    {
        /*
         * The reason the choice editor exists. "drak" typed into a text box is a setting that
         * silently does nothing, and the launcher has no way to tell the user so.
         */
        var theme = ThemeIn(new GlobalSettingsViewModel(NewSettings()));

        Assert.Equal(3, theme.Choices.Count);
        Assert.Contains(theme.Choices, c => c.Id == "system");
        Assert.Contains(theme.Choices, c => c.Id == "light");
        Assert.Contains(theme.Choices, c => c.Id == "dark");
    }

    [Fact]
    public void TheIdIsStoredRatherThanTheLabel()
    {
        /*
         * They differ on purpose: what goes in the config file is a compatibility surface and must
         * not change when the wording does. "Follow the system" is a label; "system" is the value.
         */
        var settings = NewSettings();
        var vm = new GlobalSettingsViewModel(settings);
        var theme = ThemeIn(vm);

        theme.Selected = theme.Choices.Single(c => c.Id == "dark");

        Assert.True(vm.Save());

        var raw = File.ReadAllText(ConfigPath);

        Assert.Contains("ApplicationTheme=dark", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Dark</", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStoredValueIsSelectedWhenTheWindowOpens()
    {
        var settings = NewSettings();

        settings.Set("ApplicationTheme", "light");

        Assert.Equal("light", ThemeIn(new GlobalSettingsViewModel(settings)).Value);
    }

    [Fact]
    public void AValueNobodyRecognisesFallsBackRatherThanSelectingNothing()
    {
        /*
         * A config edited by hand, or written by a newer version with more themes, must still show
         * something -- an empty combo box reads as a broken settings window.
         */
        var settings = NewSettings();

        settings.Set("ApplicationTheme", "solarized-midnight");

        var theme = ThemeIn(new GlobalSettingsViewModel(settings));

        Assert.NotNull(theme.Selected);
        Assert.Equal("system", theme.Value);
    }

    [Fact]
    public void ChangingTheThemeMarksTheWindowDirty()
    {
        var vm = new GlobalSettingsViewModel(NewSettings());
        var theme = ThemeIn(vm);

        Assert.False(vm.HasUnsavedChanges);

        theme.Selected = theme.Choices.Single(c => c.Id == "dark");

        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void ChoosingTheSameThemeAgainIsNotAChange()
    {
        // Otherwise the window nags about unsaved changes for an edit that undid itself.
        var vm = new GlobalSettingsViewModel(NewSettings());
        var theme = ThemeIn(vm);

        var original = theme.Selected;

        theme.Selected = theme.Choices.Single(c => c.Id == "dark");
        theme.Selected = original;

        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void TheThemeIsStillCoveredByTheEverySettingIsEditableGuard()
    {
        /*
         * The guard from wave 16 walks the REGISTERED keys, so adding a setting without a UI fails
         * it. This asserts the new setting went in through that door rather than around it.
         */
        var settings = NewSettings();
        var vm = new GlobalSettingsViewModel(settings);

        var shown = vm.Sections.SelectMany(s => s.Settings).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ApplicationTheme", shown);

        var missing = settings.Settings
            .Select(x => x.Id)
            .Where(k => !shown.Contains(k))
            .Where(k => !GlobalSettingsViewModel.NotEditableOnPurpose.Contains(k))
            .ToArray();

        Assert.True(missing.Length == 0, "not editable anywhere: " + string.Join(", ", missing));
    }
}
