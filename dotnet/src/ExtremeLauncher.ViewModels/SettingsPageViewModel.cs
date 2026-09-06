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
 * Ported from ui/pages/instance/InstanceSettingsPage.cpp.
 *
 * EVERY SETTING HERE IS INHERITED UNTIL A BOX IS TICKED. That is the whole shape of this page and the
 * only genuinely subtle thing about it: an instance setting is a gate plus a value, and while the gate
 * is off the value the launcher uses is the GLOBAL one. `OverrideSetting.Get()` already implements
 * that, and its DefaultValue is the global value -- so ticking a box starts from what the instance was
 * already using rather than from zero, which is what makes ticking one harmless.
 *
 * The page therefore shows, for an unticked group, the values the instance is actually running with
 * and shows them as not editable. Showing blanks would suggest the instance had no memory limit.
 *
 * THE GROUPS ARE UPSTREAM'S GATES, not a layout choice. "OverrideJavaLocation" governs the Java path
 * AND IgnoreJavaCompatibility, which is not a location -- see the note in InstanceSettings. Grouping
 * them differently on screen would let a user tick something that silently does not apply.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.ViewModels;

/// <summary>One editable setting.</summary>
public abstract partial class SettingViewModel : ObservableObject
{
    protected SettingViewModel(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }

    public string Label { get; }

    /// <summary>Whether the group's override box is ticked.</summary>
    [ObservableProperty]
    private bool _isEditable;

    /*
     * A SettingsObject, NOT an InstanceSettings. These same editors are what the global settings
     * window is built from, and the only thing they ever touched was `settings.Settings` -- so taking
     * the settings object directly is what lets one set of tested editors serve both. The gating and
     * the reset-on-untick, which ARE instance-specific, stay on the group below.
     */

    /// <summary>Reads the current value out of the settings.</summary>
    public abstract void Read(SettingsObject settings);

    /// <summary>Writes it back. For a group, only called while it is overriding.</summary>
    public abstract void Write(SettingsObject settings);

    /// <summary>Whether the value differs from the one last read.</summary>
    public abstract bool IsDirty { get; }
}

public sealed partial class TextSettingViewModel(string key, string label, bool masked = false)
    : SettingViewModel(key, label)
{
    private string _saved = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>The character a masked field shows, or nul for a field that shows its text.</summary>
    /// <remarks>
    /// FOR THE PROXY PASSWORD, which upstream's ProxyPage.ui masks with QLineEdit::Password. Masking
    /// it buys nothing against anybody with the config file -- it is stored there in plain text
    /// either way -- but it is not aimed at them: it is aimed at the person standing behind you while
    /// you type a work credential into a Minecraft launcher. Bound rather than set on the control so
    /// there is one text editor template rather than two.
    /// </remarks>
    public char MaskCharacter { get; } = masked ? '•' : '\0';

    public override bool IsDirty => !string.Equals(Value, _saved, StringComparison.Ordinal);

    public override void Read(SettingsObject settings)
    {
        _saved = settings.Get(Key)?.ToString() ?? string.Empty;
        Value = _saved;
    }

    public override void Write(SettingsObject settings)
    {
        settings.Set(Key, Value);
        _saved = Value;
    }

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(IsDirty));
}

public sealed partial class NumberSettingViewModel(string key, string label) : SettingViewModel(key, label)
{
    private int _saved;

    [ObservableProperty]
    private int _value;

    public override bool IsDirty => Value != _saved;

    public override void Read(SettingsObject settings)
    {
        _saved = Convert.ToInt32(settings.Get(Key) ?? 0, CultureInfo.InvariantCulture);
        Value = _saved;
    }

    public override void Write(SettingsObject settings)
    {
        settings.Set(Key, Value);
        _saved = Value;
    }

    partial void OnValueChanged(int value) => OnPropertyChanged(nameof(IsDirty));
}

public sealed partial class ToggleSettingViewModel(string key, string label) : SettingViewModel(key, label)
{
    private bool _saved;

    [ObservableProperty]
    private bool _value;

    public override bool IsDirty => Value != _saved;

    public override void Read(SettingsObject settings)
    {
        _saved = Convert.ToBoolean(settings.Get(Key) ?? false, CultureInfo.InvariantCulture);
        Value = _saved;
    }

    public override void Write(SettingsObject settings)
    {
        settings.Set(Key, Value);
        _saved = Value;
    }

    partial void OnValueChanged(bool value) => OnPropertyChanged(nameof(IsDirty));
}

/// <summary>One choice among a fixed set.</summary>
/// <remarks>
/// The fourth editor type, and the first that is not a free-form value. A theme is not a string the
/// user should be able to mistype: "drak" in a text box is a setting that silently does nothing, and
/// the launcher has no way to tell them so.
///
/// The VALUE stored is the id; the LABEL is what the picker shows. They differ on purpose -- what
/// goes in the config file is a compatibility surface and must not change when the wording does.
/// </remarks>
public sealed partial class ChoiceSettingViewModel : SettingViewModel
{
    public ChoiceSettingViewModel(string key, string label, params (string Id, string Label)[] choices)
        : base(key, label)
    {
        foreach (var choice in choices)
        {
            Choices.Add(new SettingChoice(choice.Id, choice.Label));
        }
    }

    private string _saved = string.Empty;

    public ObservableCollection<SettingChoice> Choices { get; } = [];

    [ObservableProperty]
    private SettingChoice? _selected;

    /// <summary>The id of the chosen option, which is what goes in the file.</summary>
    public string Value => Selected?.Id ?? string.Empty;

    public override bool IsDirty => !string.Equals(Value, _saved, StringComparison.Ordinal);

    public override void Read(SettingsObject settings)
    {
        _saved = settings.Get(Key)?.ToString() ?? string.Empty;

        // An unrecognised value falls back to the first choice rather than leaving nothing selected:
        // a config edited by hand, or written by a newer version, must still show something.
        Selected = Choices.FirstOrDefault(c => string.Equals(c.Id, _saved, StringComparison.Ordinal))
            ?? Choices.FirstOrDefault();
    }

    public override void Write(SettingsObject settings)
    {
        settings.Set(Key, Value);
        _saved = Value;
    }

    partial void OnSelectedChanged(SettingChoice? value) => OnPropertyChanged(nameof(IsDirty));
}

/// <summary>One option in a choice setting.</summary>
public sealed record SettingChoice(string Id, string Label);

/// <summary>A group of settings behind one "override the global setting" box.</summary>
public sealed partial class SettingsGroupViewModel : ObservableObject
{
    public SettingsGroupViewModel(string title, string gateKey, params SettingViewModel[] settings)
    {
        Title = title;
        GateKey = gateKey;

        foreach (var setting in settings)
        {
            Settings.Add(setting);
        }
    }

    public string Title { get; }

    /// <summary>The "OverrideX" setting that turns this group on.</summary>
    public string GateKey { get; }

    public ObservableCollection<SettingViewModel> Settings { get; } = [];

    private bool _savedGate;

    /// <summary>Whether this instance overrides the global settings for this group.</summary>
    [ObservableProperty]
    private bool _isOverriding;

    public bool IsDirty => IsOverriding != _savedGate || (IsOverriding && Settings.Any(s => s.IsDirty));

    public void Read(InstanceSettings settings)
    {
        _savedGate = Convert.ToBoolean(settings.Settings.Get(GateKey) ?? false, CultureInfo.InvariantCulture);
        IsOverriding = _savedGate;

        foreach (var setting in Settings)
        {
            setting.Read(settings.Settings);
            setting.IsEditable = IsOverriding;
        }
    }

    public void Write(InstanceSettings settings)
    {
        settings.SetOverride(GateKey, IsOverriding);

        /*
         * WRITTEN WHILE OVERRIDING, AND RESET WHEN NOT -- both halves, as upstream's applySettings does
         * (`else { m_settings->reset(...) }` for every key in the group).
         *
         * Skipping the write is NOT enough, which is what this port did first: an instance that had
         * been overriding still has the old value in its instance.cfg, so unticking the box left a key
         * that looks exactly like an override behind. Reading it back through OverrideSetting returns
         * the global either way, so nothing in the launcher noticed -- but the file is a compatibility
         * surface, and the next launcher to read it may well treat that key as an override.
         */
        if (IsOverriding)
        {
            foreach (var setting in Settings)
            {
                setting.Write(settings.Settings);
            }
        }
        else
        {
            foreach (var setting in Settings)
            {
                settings.Settings.Reset(setting.Key);
            }
        }

        _savedGate = IsOverriding;

        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnIsOverridingChanged(bool value)
    {
        foreach (var setting in Settings)
        {
            setting.IsEditable = value;
        }

        OnPropertyChanged(nameof(IsDirty));
    }
}

public sealed partial class SettingsPageViewModel : ObservableObject, IInstancePage
{
    private InstanceSettings? _settings;

    public string Title => "Settings";

    public ObservableCollection<SettingsGroupViewModel> Groups { get; } = [];

    public bool HasUnsavedChanges => Groups.Any(g => g.IsDirty);

    /// <summary>Reads an instance's settings into the page.</summary>
    public void Load(InstanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;

        Groups.Clear();

        /*
         * The gates are upstream's, exactly. "OverrideJavaLocation" governing IgnoreJavaCompatibility
         * looks wrong until you read the note on it: an instance pinned to a specific JVM is precisely
         * the case where the user also wants to say "yes, I know, run it anyway".
         */
        Groups.Add(new SettingsGroupViewModel(
            "Java",
            "OverrideJavaLocation",
            new TextSettingViewModel("JavaPath", "Java path"),
            new ToggleSettingViewModel("IgnoreJavaCompatibility", "Ignore compatibility checks")));

        Groups.Add(new SettingsGroupViewModel(
            "Java arguments",
            "OverrideJavaArgs",
            new TextSettingViewModel("JvmArgs", "JVM arguments")));

        Groups.Add(new SettingsGroupViewModel(
            "Memory",
            "OverrideMemory",
            new NumberSettingViewModel("MinMemAlloc", "Minimum (MiB)"),
            new NumberSettingViewModel("MaxMemAlloc", "Maximum (MiB)"),
            new NumberSettingViewModel("PermGen", "PermGen (MiB)")));

        Groups.Add(new SettingsGroupViewModel(
            "Window",
            "OverrideWindow",
            new ToggleSettingViewModel("LaunchMaximized", "Start maximised"),
            new NumberSettingViewModel("MinecraftWinWidth", "Width"),
            new NumberSettingViewModel("MinecraftWinHeight", "Height")));

        Groups.Add(new SettingsGroupViewModel(
            "Custom commands",
            "OverrideCommands",
            new TextSettingViewModel("PreLaunchCommand", "Before launch"),
            new TextSettingViewModel("WrapperCommand", "Wrapper"),
            new TextSettingViewModel("PostExitCommand", "After exit")));

        Groups.Add(new SettingsGroupViewModel(
            "Console",
            "OverrideConsole",
            new ToggleSettingViewModel("ShowConsole", "Show the console when the game starts"),
            new ToggleSettingViewModel("AutoCloseConsole", "Close it when the game exits cleanly"),
            new ToggleSettingViewModel("ShowConsoleOnError", "Show it when the game crashes"),
            new ToggleSettingViewModel("LogPrePostOutput", "Include pre-launch and post-exit output")));

        Groups.Add(new SettingsGroupViewModel(
            "Miscellaneous",
            "OverrideMiscellaneous",
            new ToggleSettingViewModel("CloseAfterLaunch", "Close the launcher after starting the game"),
            new ToggleSettingViewModel("QuitAfterGameStop", "Quit the launcher when the game stops")));

        Groups.Add(new SettingsGroupViewModel(
            "Native workarounds",
            "OverrideNativeWorkarounds",
            new ToggleSettingViewModel("UseNativeOpenAL", "Use the system OpenAL"),
            new TextSettingViewModel("CustomOpenALPath", "OpenAL path"),
            new ToggleSettingViewModel("UseNativeGLFW", "Use the system GLFW"),
            new TextSettingViewModel("CustomGLFWPath", "GLFW path")));

        /*
         * Every one of these is Linux-only in practice -- Feral GameMode, MangoHud, the discrete-GPU
         * switch and Zink. They are shown on every platform anyway, as upstream does: an instance
         * configured on Linux and opened on Windows must not silently lose its settings, and hiding
         * the boxes is how that happens.
         */
        Groups.Add(new SettingsGroupViewModel(
            "Performance",
            "OverridePerformance",
            new ToggleSettingViewModel("EnableFeralGamemode", "Enable Feral GameMode"),
            new ToggleSettingViewModel("EnableMangoHud", "Show MangoHud"),
            new ToggleSettingViewModel("UseDiscreteGpu", "Use the discrete GPU"),
            new ToggleSettingViewModel("UseZink", "Use Zink")));

        Groups.Add(new SettingsGroupViewModel(
            "Environment",
            "OverrideEnv",
            new TextSettingViewModel("Env", "Extra environment variables")));

        foreach (var group in Groups)
        {
            group.Read(settings);
            group.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HasUnsavedChanges));

            foreach (var setting in group.Settings)
            {
                setting.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HasUnsavedChanges));
            }
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    public bool Save()
    {
        if (_settings is null)
        {
            return !HasUnsavedChanges;
        }

        try
        {
            foreach (var group in Groups)
            {
                group.Write(_settings);
            }
        }
        catch (IOException)
        {
            // A read-only instance folder. The window refuses to close on this.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        /*
         * Re-read after writing. Unticking a group makes its values revert to the global ones, and the
         * boxes must show what the instance is now actually using rather than the numbers the user was
         * looking at a moment ago.
         */
        foreach (var group in Groups)
        {
            group.Read(_settings);
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));

        return true;
    }
}
