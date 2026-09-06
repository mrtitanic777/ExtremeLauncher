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
 * The global settings window through its real visual tree.
 *
 * The view model tests pin the behaviour. These exist for what those cannot see: whether the three
 * editor templates actually match the three view model types, and whether Save and Cancel are wired
 * to anything. A template that matches nothing renders an empty row and every unit test still passes.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class GlobalSettingsWindowTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-gsui-" + Guid.NewGuid().ToString("N"));

    private readonly List<Window> _windows = [];

    public GlobalSettingsWindowTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        // See MainWindowTests: a window left showing breaks a later test, not this one.
        foreach (var window in _windows)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ConfigPath => Path.Combine(_temp, "extremelauncher.cfg");

    private (GlobalSettingsWindow Window, GlobalSettingsViewModel Model, SettingsObject Settings) Show()
    {
        var settings = GlobalSettings.Create(ConfigPath);
        var model = new GlobalSettingsViewModel(settings);
        var window = new GlobalSettingsWindow(model);

        _windows.Add(window);

        window.Show();

        Settle(window);

        return (window, model, settings);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Button Button(Visual root, string content)
        => root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content);

    private static SettingViewModel Find(GlobalSettingsViewModel vm, string key)
        => vm.Sections.SelectMany(s => s.Settings).Single(s => s.Key == key);

    [AvaloniaFact]
    public void EverySettingGetsARealEditorControl()
    {
        /*
         * THE ONE THING ONLY A UI TEST CAN SEE. A DataTemplate that matches no view model type renders
         * an empty row: the section headings still appear, the list still has the right number of
         * items, and every view model assertion still passes -- but the setting cannot be edited.
         *
         * Counted against the model rather than a hardcoded number, so adding a setting cannot make
         * this quietly weaker.
         */
        var (window, model, _) = Show();

        var expected = model.Sections.SelectMany(s => s.Settings).ToArray();

        // A NumericUpDown contains its own TextBox, so those are discounted -- otherwise every
        // number setting would be counted twice and the totals would never balance.
        var textBoxes = window.GetLogicalDescendants().OfType<TextBox>()
            .Count(t => t.FindAncestorOfType<NumericUpDown>() is null);
        var spinners = window.GetLogicalDescendants().OfType<NumericUpDown>().Count();

        /*
         * COMBO BOXES TOO, and this line was added late. When the choice editor arrived in wave 31
         * this test still passed without it -- a ChoiceSettingViewModel is none of the three types
         * counted below, so nothing asserted it had an editor at all. A coverage test that only
         * covers the types that existed when it was written stops being a coverage test the moment a
         * fourth is added.
         */
        var pickers = window.GetLogicalDescendants().OfType<ComboBox>().Count();

        // Checkboxes: the settings ones only -- this window has no other checkboxes, unlike the
        // instance page where the override gates are also checkboxes.
        var checkBoxes = window.GetLogicalDescendants().OfType<CheckBox>().Count();

        Assert.Equal(expected.OfType<TextSettingViewModel>().Count(), textBoxes);
        Assert.Equal(expected.OfType<NumberSettingViewModel>().Count(), spinners);
        Assert.Equal(expected.OfType<ToggleSettingViewModel>().Count(), checkBoxes);
        Assert.Equal(expected.OfType<ChoiceSettingViewModel>().Count(), pickers);

        // And nothing is left over: every setting is one of the four kinds the window can draw.
        Assert.Equal(expected.Length, textBoxes + spinners + checkBoxes + pickers);
    }

    [AvaloniaFact]
    public void TheMaskedFieldIsActuallyMaskedOnScreen()
    {
        /*
         * ANOTHER ONE ONLY A UI TEST CAN SEE. The view model exposing MaskCharacter proves nothing --
         * if the template does not bind it, the proxy password is typed in the clear and every view
         * model assertion still passes.
         *
         * Counted against the model rather than looking for one particular box, so a second masked
         * setting cannot quietly go unmasked.
         */
        var (window, model, _) = Show();

        var expected = model.Sections
            .SelectMany(s => s.Settings)
            .OfType<TextSettingViewModel>()
            .Count(s => s.MaskCharacter != '\0');

        var masked = window.GetLogicalDescendants()
            .OfType<TextBox>()
            .Count(t => t.PasswordChar != '\0');

        Assert.True(expected > 0, "nothing is masked, so this test is not testing anything");
        Assert.Equal(expected, masked);
    }

    [AvaloniaFact]
    public void EverySectionHeadingIsOnScreen()
    {
        var (window, model, _) = Show();

        var texts = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(model.Sections, section => Assert.Contains(section.Title, texts));
    }

    [AvaloniaFact]
    public void TypingIntoABoxAndPressingSaveWritesTheFile()
    {
        // The whole path: control -> binding -> view model -> settings object -> file on disk.
        var (window, model, _) = Show();

        ((TextSettingViewModel)Find(model, "JvmArgs")).Value = "-XX:+UseZGC";

        Settle(window);

        Button(window, "Save").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert.True(window.Saved);
        Assert.True(File.Exists(ConfigPath));
        Assert.Contains("-XX:+UseZGC", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void CancelDiscardsTheEditsInsteadOfLeavingThemLive()
    {
        /*
         * The settings object OUTLIVES this window -- it is the one the running launcher reads. So
         * Cancel has to put the values back, not merely close: otherwise an edit somebody explicitly
         * cancelled would still apply to the next launch, and there would be nothing on screen saying
         * so. Asserted through the shared settings object, which is what a launch would consult.
         */
        var (window, model, settings) = Show();

        settings.Set("JvmArgs", "-Xmx1G");

        model.Revert();

        ((TextSettingViewModel)Find(model, "JvmArgs")).Value = "-Xmx8G";

        Settle(window);

        Button(window, "Cancel").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert.False(window.Saved);
        Assert.Equal("-Xmx1G", settings.Get("JvmArgs")?.ToString());
    }

    [AvaloniaFact]
    public void RevertPutsTheBoxesBackWithoutClosing()
    {
        var (window, model, settings) = Show();

        settings.Set("JvmArgs", "-Xmx1G");

        model.Revert();

        ((TextSettingViewModel)Find(model, "JvmArgs")).Value = "-Xmx8G";

        Settle(window);

        Button(window, "Revert").Command!.Execute(null);

        Settle(window);

        Assert.Equal("-Xmx1G", ((TextSettingViewModel)Find(model, "JvmArgs")).Value);
        Assert.False(model.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void CancelIsTheDefaultButtonSoReturnDoesNotCommitSettings()
    {
        // Same rule as every other dialog here: a stray Return must not save a page being read.
        var (window, _, _) = Show();

        Assert.True(Button(window, "Cancel").IsDefault);
        Assert.False(Button(window, "Save").IsDefault);
    }
}
