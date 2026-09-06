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
 * Ported in behaviour from launcher/ui/pages/global/*.cpp -- JavaPage, MinecraftPage, LauncherPage,
 * CustomCommandsPage, EnvironmentVariablesPage and APIPage.
 *
 * THE DEFAULTS EVERY INSTANCE INHERITS. All 39 global settings were registered, defaulted and tested
 * several waves ago, and **nothing could edit any of them** -- the only way to change a global was to
 * hand-edit extremelauncher.cfg. The instance settings page has offered per-instance overrides this
 * whole time, overriding values the user had no way to set.
 *
 * IT IS THE SAME EDITORS AS THE INSTANCE PAGE, deliberately, minus the override boxes. Two independent
 * implementations of "edit JvmArgs" is two chances for the global and the instance copy to disagree
 * about what a setting means -- and this window is precisely where somebody compares them.
 *
 * WHAT IS NOT HERE, and why:
 *
 *   Language      no translation machinery is ported yet, so a picker would list one language.
 *   External tools MultiMC-era JProfiler/JVisualVM/MCEdit integration, none of it ported.
 *   API keys      supplied at runtime now (see BuildConfigOverrides), not editable here on purpose --
 *                 a key typed into a text box would have to be stored somewhere, and the whole point
 *                 of the runtime scheme is that this launcher never writes one down.
 *
 * The rest of upstream's API page IS here: the paste service a log gets uploaded to, and the metadata
 * server, both of which are ordinary settings with nothing secret about them.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Net;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.ViewModels;

/// <summary>A titled block of settings. No gate: globals are always editable.</summary>
public sealed partial class GlobalSettingsSectionViewModel : ObservableObject
{
    public GlobalSettingsSectionViewModel(string title, string description, params SettingViewModel[] settings)
    {
        Title = title;
        Description = description;

        foreach (var setting in settings)
        {
            // Always on. The instance page uses this to grey out a group nobody has ticked; here
            // there is nothing to tick, and a permanently-disabled box would be nonsense.
            setting.IsEditable = true;

            Settings.Add(setting);
        }
    }

    public string Title { get; }

    /// <summary>A sentence under the heading, or empty. Used where a group is genuinely obscure.</summary>
    public string Description { get; }

    public bool HasDescription => Description.Length != 0;

    public ObservableCollection<SettingViewModel> Settings { get; } = [];

    public bool IsDirty => Settings.Any(s => s.IsDirty);

    public void Read(SettingsObject settings)
    {
        foreach (var setting in Settings)
        {
            setting.Read(settings);
        }
    }

    public void Write(SettingsObject settings)
    {
        foreach (var setting in Settings)
        {
            setting.Write(settings);
        }
    }
}

public sealed partial class GlobalSettingsViewModel : ObservableObject
{
    private readonly SettingsObject _settings;

    private readonly IJavaInstallUi? _java;

    /// <param name="java">
    /// Opens the Java download dialog. Offered from HERE because this is where the Java path setting
    /// lives, and "the game wants Java 17" is discovered while looking at exactly that box.
    /// </param>
    public GlobalSettingsViewModel(SettingsObject settings, IJavaInstallUi? java = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _java = java;

        Build();
        Reload();
    }

    [ObservableProperty]
    private string _status = string.Empty;

    public bool CanInstallJava => _java is not null;

    /// <summary>Opens the Java download dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallJava))]
    public async Task InstallJavaAsync()
    {
        if (_java is not null)
        {
            await _java.OpenAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Registered settings that deliberately have no editor.</summary>
    /// <remarks>
    /// AN EXEMPTION LIST, NOT AN ESCAPE HATCH. The coverage guard walks the registered keys precisely
    /// so a setting added later and forgotten here fails a test rather than quietly having no UI, and
    /// weakening it would throw that away. So a key may only appear here with a reason, and the guard
    /// still fails for anything not on the list.
    ///
    /// PastebinURL is the only entry: it is a LEGACY key that exists to be read once at startup and
    /// migrated away (see GlobalSettings.RegisterPasteSettings). Showing a box for a setting whose
    /// entire purpose is to be erased would invite somebody to fill it in.
    /// </remarks>
    public static readonly IReadOnlySet<string> NotEditableOnPurpose =
        new HashSet<string>(StringComparer.Ordinal) { "PastebinURL" };

    public ObservableCollection<GlobalSettingsSectionViewModel> Sections { get; } = [];

    public bool HasUnsavedChanges => Sections.Any(s => s.IsDirty);

    /// <summary>Writes every section back and flushes the file.</summary>
    /// <returns>False when the file could not be written, which is when the window must stay open.</returns>
    public bool Save()
    {
        try
        {
            foreach (var section in Sections)
            {
                section.Write(_settings);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A read-only data directory, or a config open elsewhere. Said out loud rather than
            // swallowed: closing the window on a failed save loses everything the user just typed.
            Status = $"Could not save: {e.Message}";

            return false;
        }

        Reload();

        Status = "Saved.";

        return true;
    }

    /// <summary>Throws away unsaved edits and re-reads the file.</summary>
    [RelayCommand]
    public void Revert()
    {
        Reload();

        Status = "Reverted to the saved settings.";
    }

    private void Reload()
    {
        foreach (var section in Sections)
        {
            section.Read(_settings);
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void Build()
    {
        /*
         * The grouping is upstream's pages, collapsed into sections of one scrolling window rather
         * than a list-and-stack dialog. Upstream needs the page machinery because it also hosts the
         * per-instance pages in the same widget; nothing here does.
         */
        Sections.Add(new GlobalSettingsSectionViewModel(
            "Appearance",
            "Follows the desktop unless told otherwise. Takes effect immediately.",
            new ChoiceSettingViewModel(
                "ApplicationTheme",
                "Theme",
                ("system", "Follow the system"),
                ("light", "Light"),
                ("dark", "Dark"))));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Java",
            string.Empty,
            new TextSettingViewModel("JavaPath", "Java path"),
            new TextSettingViewModel("JvmArgs", "JVM arguments"),
            new ToggleSettingViewModel("IgnoreJavaCompatibility", "Ignore compatibility checks"),
            new ToggleSettingViewModel("AutomaticJavaSwitch", "Pick a compatible Java automatically"),
            new ToggleSettingViewModel("AutomaticJavaDownload", "Download Java when one is missing")));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Memory",
            "The maximum defaults to a share of this machine's RAM rather than a fixed number: 4 GiB "
            + "on a 4 GiB laptop is a swap storm.",
            new NumberSettingViewModel("MinMemAlloc", "Minimum (MiB)"),
            new NumberSettingViewModel("MaxMemAlloc", "Maximum (MiB)"),
            new NumberSettingViewModel("PermGen", "PermGen (MiB)")));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Game window",
            string.Empty,
            new ToggleSettingViewModel("LaunchMaximized", "Start maximised"),
            new NumberSettingViewModel("MinecraftWinWidth", "Width"),
            new NumberSettingViewModel("MinecraftWinHeight", "Height")));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Console",
            string.Empty,
            new ToggleSettingViewModel("ShowConsole", "Show the console when the game starts"),
            new ToggleSettingViewModel("AutoCloseConsole", "Close it when the game exits cleanly"),
            new ToggleSettingViewModel("ShowConsoleOnError", "Show it when the game crashes"),
            new ToggleSettingViewModel("LogPrePostOutput", "Include pre-launch and post-exit output"),
            new NumberSettingViewModel("ConsoleMaxLines", "Lines to keep"),
            new ToggleSettingViewModel("ConsoleOverflowStop", "Stop the game if it floods the console")));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Custom commands",
            "Run around every launch. A wrapper receives the whole Java command line as its arguments.",
            new TextSettingViewModel("PreLaunchCommand", "Before launch"),
            new TextSettingViewModel("WrapperCommand", "Wrapper"),
            new TextSettingViewModel("PostExitCommand", "After exit")));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Environment",
            "One NAME=value per line, applied to the game's process.",
            new TextSettingViewModel("Env", "Extra environment variables")));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Native workarounds",
            "For distributions whose bundled OpenAL or GLFW does not work. Leave off unless the game "
            + "has actually failed without them.",
            new ToggleSettingViewModel("UseNativeOpenAL", "Use the system OpenAL"),
            new TextSettingViewModel("CustomOpenALPath", "OpenAL path"),
            new ToggleSettingViewModel("UseNativeGLFW", "Use the system GLFW"),
            new TextSettingViewModel("CustomGLFWPath", "GLFW path")));

        /*
         * Linux-only in practice, and shown on every platform anyway -- as upstream does, and for the
         * same reason the instance page does: a profile configured on Linux and opened on Windows must
         * not silently lose settings, and hiding the boxes is how that happens.
         */
        Sections.Add(new GlobalSettingsSectionViewModel(
            "Performance",
            "Linux-only in effect. Shown everywhere so settings made on Linux survive being opened "
            + "on another platform.",
            new ToggleSettingViewModel("EnableFeralGamemode", "Enable Feral GameMode"),
            new ToggleSettingViewModel("EnableMangoHud", "Show MangoHud"),
            new ToggleSettingViewModel("UseDiscreteGpu", "Use the discrete GPU"),
            new ToggleSettingViewModel("UseZink", "Use Zink")));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Launcher behaviour",
            string.Empty,
            new ToggleSettingViewModel("CloseAfterLaunch", "Close the launcher after starting the game"),
            new ToggleSettingViewModel("QuitAfterGameStop", "Quit the launcher when the game stops"),
            new ToggleSettingViewModel("ShowGameTime", "Show how long each instance has been played"),
            new ToggleSettingViewModel("RecordGameTime", "Record play time"),
            new ToggleSettingViewModel("OnlineFixes", "Apply fixes for old versions' broken online services")));

        /*
         * PROXY. Three things upstream knows and only says in passing, all of them said on screen
         * here because each is something somebody would otherwise lose an afternoon to:
         *
         *   - it applies to THE LAUNCHER ONLY. Minecraft takes no proxy settings, so the game's own
         *     traffic ignores every one of these boxes. Upstream puts this at the top of the page.
         *   - the password lands in extremelauncher.cfg IN PLAIN TEXT. Upstream says so under the
         *     field; a user typing a corporate credential into a Minecraft launcher deserves to know
         *     where it ends up.
         *   - it is read once at startup, so an edit here applies from the next start. Upstream
         *     re-applies live because Qt has an application-wide proxy; a live HttpClient cannot
         *     have its proxy changed, so this is a divergence and the window admits it rather than
         *     leaving somebody to wonder why nothing happened.
         */
        Sections.Add(new GlobalSettingsSectionViewModel(
            "Proxy",
            "Applies to the launcher only, from the next start -- Minecraft itself does not accept "
            + "proxy settings. The user name and password are stored in plain text in the launcher's "
            + "configuration file.",
            new ChoiceSettingViewModel("ProxyType", "Proxy", [.. ProxyFactory.Types]),
            new TextSettingViewModel("ProxyAddr", "Address"),
            new NumberSettingViewModel("ProxyPort", "Port"),
            new TextSettingViewModel("ProxyUser", "User name (optional)"),
            new TextSettingViewModel("ProxyPass", "Password (optional)", masked: true)));

        /*
         * THE PASTE SERVICE a log gets uploaded to. Upstream keeps this on its API page beside the
         * key fields; here it stands alone, because the key fields are deliberately absent (see
         * above) and a page with one setting on it is not a page.
         *
         * The stored value is an INTEGER -- the numeric value of upstream's enum -- so the choice ids
         * below are "0".."3" rather than names. That is shared on-disk format, not a design choice.
         */
        Sections.Add(new GlobalSettingsSectionViewModel(
            "Log uploads",
            "Where the Upload button on a log sends it. mclo.gs understands Minecraft logs and folds "
            + "the stack traces, which makes it the most useful place to send one. Leave the address "
            + "blank unless you run your own instance of the service.",
            new ChoiceSettingViewModel(
                "PastebinType",
                "Service",
                [.. PasteUpload.PasteTypes.Select(t => (((int)t.Type).ToString(CultureInfo.InvariantCulture), t.Label))]),
            new TextSettingViewModel("PastebinCustomAPIBase", "Custom address (optional)")));

        Sections.Add(new GlobalSettingsSectionViewModel(
            "Metadata server",
            "Where version information is fetched from. Leave blank to use the built-in default. "
            + "A value that is not an http or https URL is ignored at startup.",
            new TextSettingViewModel("MetaURLOverride", "Metadata URL")));

        foreach (var section in Sections)
        {
            foreach (var setting in section.Settings)
            {
                setting.PropertyChanged += (_, _) =>
                {
                    OnPropertyChanged(nameof(HasUnsavedChanges));

                    // Cleared as soon as anything is touched, so "Saved." cannot sit above edits
                    // that are not saved.
                    if (Status.Length != 0)
                    {
                        Status = string.Empty;
                    }
                };
            }
        }
    }
}
