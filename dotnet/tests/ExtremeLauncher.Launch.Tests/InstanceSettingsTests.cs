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
 * Characterization tests for an instance's settings. Upstream has no Qt test for BaseInstance.
 *
 * Every key here is one users already have in an instance.cfg on disk, so these assertions are a
 * compatibility surface: a renamed key or a changed default silently resets whatever it used to hold.
 */

using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstanceSettingsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-inst-" + Guid.NewGuid().ToString("N"));

    public InstanceSettingsTests() => Directory.CreateDirectory(_temp);

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

    private string Path_(string name) => Path.Combine(_temp, name);

    private SettingsObject NewGlobals() => GlobalSettings.Create(Path_("launcher.cfg"));

    private InstanceSettings NewInstance(string name = "instance.cfg", SettingsObject? globals = null)
        => new(new IniSettingsObject(Path_(name)), globals ?? NewGlobals());

    // ================================================================== defaults

    [Fact]
    public void AFreshInstanceHasTheDocumentedDefaults()
    {
        var instance = NewInstance();

        // These strings appear in every instance.cfg the launcher has ever written.
        Assert.Equal("Unnamed Instance", instance.Name);
        Assert.Equal("default", instance.IconKey);
        Assert.Equal(string.Empty, instance.Notes);
        Assert.Equal(0, instance.TotalTimePlayed);
    }

    [Fact]
    public void NameAndNotesRoundTripThroughDisk()
    {
        var path = Path_("instance.cfg");
        var globals = NewGlobals();

        var instance = new InstanceSettings(new IniSettingsObject(path), globals);
        instance.Name = "My Modpack";
        instance.Notes = "Testing 1.20.1";

        var reopened = new InstanceSettings(new IniSettingsObject(path), NewGlobals());

        Assert.Equal("My Modpack", reopened.Name);
        Assert.Equal("Testing 1.20.1", reopened.Notes);
    }

    // ================================================================== the override pattern

    [Fact]
    public void WithoutAGateTheGlobalValueApplies()
    {
        var globals = NewGlobals();
        globals.Set("MaxMemAlloc", 8192);

        var instance = NewInstance(globals: globals);

        // The instance has no opinion, so it follows the launcher-wide setting.
        Assert.Equal(8192, instance.MaxMemAlloc);
    }

    [Fact]
    public void TurningTheGateOnLetsTheInstanceDecide()
    {
        var globals = NewGlobals();
        globals.Set("MaxMemAlloc", 8192);

        var instance = NewInstance(globals: globals);
        instance.SetOverride("OverrideMemory", true);
        instance.Settings.Set("MaxMemAlloc", 2048);

        Assert.Equal(2048, instance.MaxMemAlloc);
    }

    [Fact]
    public void TurningTheGateOffRestoresTheGlobalWithoutLosingTheInstanceValue()
    {
        var globals = NewGlobals();
        globals.Set("MaxMemAlloc", 8192);

        var instance = NewInstance(globals: globals);
        instance.SetOverride("OverrideMemory", true);
        instance.Settings.Set("MaxMemAlloc", 2048);

        instance.SetOverride("OverrideMemory", false);
        Assert.Equal(8192, instance.MaxMemAlloc);

        // The point of a gate rather than a delete: the user can toggle back and forth while deciding.
        instance.SetOverride("OverrideMemory", true);
        Assert.Equal(2048, instance.MaxMemAlloc);
    }

    [Fact]
    public void OneGateCoversAWholeGroup()
    {
        var globals = NewGlobals();
        globals.Set("MinMemAlloc", 1024);
        globals.Set("MaxMemAlloc", 8192);
        globals.Set("PermGen", 256);

        var instance = NewInstance(globals: globals);
        instance.SetOverride("OverrideMemory", true);

        instance.Settings.Set("MinMemAlloc", 256);
        instance.Settings.Set("MaxMemAlloc", 2048);
        instance.Settings.Set("PermGen", 64);

        // "Override memory" means all three together — a UI cannot offer them separately without a
        // gate each, and upstream chose the group.
        Assert.Equal(256, instance.MinMemAlloc);
        Assert.Equal(2048, instance.MaxMemAlloc);
        Assert.Equal(64, instance.PermGen);

        instance.SetOverride("OverrideMemory", false);

        Assert.Equal(1024, instance.MinMemAlloc);
        Assert.Equal(8192, instance.MaxMemAlloc);
        Assert.Equal(256, instance.PermGen);
    }

    [Fact]
    public void TheJavaLocationGateAlsoCoversCompatibilityChecking()
    {
        var globals = NewGlobals();

        var instance = NewInstance(globals: globals);
        instance.SetOverride("OverrideJavaLocation", true);
        instance.Settings.Set("JavaPath", "/opt/jdk8/bin/java");
        instance.Settings.Set("IgnoreJavaCompatibility", true);

        // INHERITED and worth knowing: an instance pinned to a specific JVM is exactly the case where
        // the user also wants to say "yes, I know, run it anyway".
        Assert.Equal("/opt/jdk8/bin/java", instance.JavaPath);
        Assert.True(instance.IgnoreJavaCompatibility);

        instance.SetOverride("OverrideJavaLocation", false);

        Assert.False(instance.IgnoreJavaCompatibility);
    }

    [Fact]
    public void JavaArgumentsHaveTheirOwnGate()
    {
        var globals = NewGlobals();
        globals.Set("JvmArgs", "-XX:+UseG1GC");

        var instance = NewInstance(globals: globals);

        instance.SetOverride("OverrideJavaLocation", true);
        instance.Settings.Set("JvmArgs", "-Xss2M");

        // Separate from the location gate: changing where Java lives is not the same decision as
        // changing how it is invoked.
        Assert.Equal("-XX:+UseG1GC", instance.JvmArgs);

        instance.SetOverride("OverrideJavaArgs", true);
        Assert.Equal("-Xss2M", instance.JvmArgs);
    }

    [Fact]
    public void TheCommandGateAcceptsBothSpellings()
    {
        var path = Path_("legacy.cfg");

        // An older instance.cfg carries "OverrideLaunchCmd"; the key was renamed later.
        var raw = new IniFile();
        raw.Set("OverrideLaunchCmd", true);
        raw.Set("WrapperCommand", "gamemoderun");
        raw.SaveFile(path);

        var instance = new InstanceSettings(new IniSettingsObject(path), NewGlobals());

        Assert.Equal("gamemoderun", instance.WrapperCommand);
    }

    [Fact]
    public void APassthroughFollowsTheGlobalWithNoGateAtAll()
    {
        var globals = NewGlobals();
        globals.Set("ConsoleMaxLines", 5000);

        var instance = NewInstance(globals: globals);

        // An instance cannot have its own console scrollback limit; pretending otherwise would need a
        // gate the UI has nowhere to show.
        Assert.Equal(5000, instance.Settings.Get("ConsoleMaxLines"));
    }

    // ================================================================== repairs

    [Fact]
    public void ANegativePlaytimeIsRepairedOnOpen()
    {
        var path = Path_("broken.cfg");

        // A bug in an older version could write this, and a negative playtime renders as nonsense
        // forever after.
        var raw = new IniFile();
        raw.Set("totalTimePlayed", -5000);
        raw.SaveFile(path);

        var instance = new InstanceSettings(new IniSettingsObject(path), NewGlobals());

        Assert.Equal(0, instance.TotalTimePlayed);
    }

    [Fact]
    public void APositivePlaytimeIsLeftAlone()
    {
        var path = Path_("fine.cfg");

        var raw = new IniFile();
        raw.Set("totalTimePlayed", 123_456);
        raw.SaveFile(path);

        var instance = new InstanceSettings(new IniSettingsObject(path), NewGlobals());

        Assert.Equal(123_456, instance.TotalTimePlayed);
    }

    // ================================================================== globals

    [Fact]
    public void TheDefaultHeapComesFromTheMachine()
    {
        var globals = NewGlobals();

        // 4 GiB on a 4 GiB laptop is a swap storm, so the default is derived rather than fixed.
        var max = globals.Get("MaxMemAlloc");

        Assert.NotNull(max);
        Assert.InRange(Convert.ToInt32(max, System.Globalization.CultureInfo.InvariantCulture), 1, 4096);
    }

    [Fact]
    public void EveryGatedGlobalIsRegistered()
    {
        var globals = NewGlobals();

        // A missing global means its override silently never binds, and the instance value is ignored
        // with no error anywhere.
        foreach (var key in new[]
                 {
                     "JavaPath", "JvmArgs", "IgnoreJavaCompatibility",
                     "MinMemAlloc", "MaxMemAlloc", "PermGen",
                     "LaunchMaximized", "MinecraftWinWidth", "MinecraftWinHeight",
                     "PreLaunchCommand", "WrapperCommand", "PostExitCommand",
                     "ShowConsole", "AutoCloseConsole", "ShowConsoleOnError", "LogPrePostOutput",
                     "ConsoleMaxLines", "ConsoleOverflowStop",
                     "UseNativeOpenAL", "CustomOpenALPath", "UseNativeGLFW", "CustomGLFWPath",
                     "EnableFeralGamemode", "EnableMangoHud", "UseDiscreteGpu", "UseZink",
                     "CloseAfterLaunch", "QuitAfterGameStop", "OnlineFixes", "Env",
                     "ShowGameTime", "RecordGameTime",
                 })
        {
            Assert.True(globals.Contains(key), $"global setting '{key}' is not registered");
        }
    }

    [Fact]
    public void OpeningFromAnInstanceDirectoryFindsItsConfig()
    {
        var root = Path_("MyInstance");
        Directory.CreateDirectory(root);

        var paths = new InstancePaths(root);

        var instance = InstanceSettings.Open(paths, NewGlobals());
        instance.Name = "From Disk";

        // instance.cfg sits at the instance root, beside mmc-pack.json.
        Assert.True(File.Exists(paths.ConfigPath));

        var reopened = InstanceSettings.Open(paths, NewGlobals());
        Assert.Equal("From Disk", reopened.Name);
    }

    // ================================================================== the metadata server override

    /*
     * Ported from the "Meta URL" block in Application.cpp: an override that is not an http(s) URL is
     * RESET at startup rather than left to fail on every download. A typo in a settings file would
     * otherwise break the launcher in a way that looks exactly like the network being down.
     */
    [Theory]
    [InlineData("https://meta.prismlauncher.org/v1/", true)]
    [InlineData("http://localhost:8080/v1/", true)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    [InlineData("meta.example.com/v1/", false)]
    [InlineData("file:///C:/meta/", false)]
    [InlineData("ftp://example.com/meta/", false)]
    public void OnlyHttpAndHttpsOverridesAreUsable(string url, bool usable)
        => Assert.Equal(usable, GlobalSettings.IsUsableMetaUrl(url));

    [Fact]
    public void AnUnusableOverrideFallsBackToTheBuildDefault()
    {
        var settings = NewGlobals();

        settings.Set("MetaURLOverride", "nonsense");

        Assert.Equal(Core.BuildConfig.Instance.MetaUrl, GlobalSettings.ResolveMetaUrl(settings));
    }

    [Fact]
    public void AUsableOverrideWins()
    {
        var settings = NewGlobals();

        settings.Set("MetaURLOverride", "https://meta.prismlauncher.org/v1/");

        Assert.Equal("https://meta.prismlauncher.org/v1/", GlobalSettings.ResolveMetaUrl(settings));
    }

    /*
     * A nonsense override ALREADY ON DISK is cleared when the settings are registered -- upstream
     * resets it rather than leaving it to fail on every download, where it looks like the network
     * being down rather than a typo in a config file.
     */
    [Fact]
    public void RegisteringResetsAnInvalidOverrideThatWasAlreadyThere()
    {
        var path = Path_("launcher.cfg");

        File.WriteAllText(path, "MetaURLOverride=gopher://example.com" + Environment.NewLine);

        var settings = GlobalSettings.Create(path);

        Assert.Equal(string.Empty, settings.Get("MetaURLOverride"));
    }
}
