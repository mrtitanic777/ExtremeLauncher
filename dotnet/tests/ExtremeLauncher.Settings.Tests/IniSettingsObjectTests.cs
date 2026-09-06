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
 * Characterization tests for the on-disk settings object. Upstream has no Qt test for
 * INISettingsObject.
 *
 * The multi-path constructor is the part worth pinning: it is a MIGRATION, not a fallback chain, and
 * an earlier draft of this port had it as the latter — which reads a user's old config forever and
 * writes their changes back to it, so an upgrade never actually takes effect.
 */

using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Settings.Tests;

public sealed class IniSettingsObjectTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-iniobj-" + Guid.NewGuid().ToString("N"));

    public IniSettingsObjectTests() => Directory.CreateDirectory(_temp);

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

    /// <summary>Writes a settings file the way the launcher would.</summary>
    private string WriteConfig(string name, string key, string value)
    {
        var path = Path_(name);
        var file = new IniFile();

        file.Set(key, value);
        file.SaveFile(path);

        return path;
    }

    // ================================================================== reading and writing

    [Fact]
    public void ASettingRoundTripsThroughDisk()
    {
        var path = Path_("instance.cfg");

        var settings = new IniSettingsObject(path);
        settings.RegisterSetting("name", "Untitled");
        settings.Set("name", "My Instance");

        // Written through on every change, so a crash does not lose the last thing the user typed.
        var reloaded = new IniSettingsObject(path);
        reloaded.RegisterSetting("name", "Untitled");

        Assert.Equal("My Instance", reloaded.Get("name"));
    }

    [Fact]
    public void AnUnsetSettingReadsItsDefault()
    {
        var settings = new IniSettingsObject(Path_("empty.cfg"));
        settings.RegisterSetting("name", "Untitled");

        Assert.Equal("Untitled", settings.Get("name"));
    }

    [Fact]
    public void OnlyTheCanonicalSpellingSurvivesAWrite()
    {
        var path = Path_("synonyms.cfg");

        var file = new IniFile();
        file.Set("OldName", "from the old key");
        file.SaveFile(path);

        var settings = new IniSettingsObject(path);
        settings.RegisterSetting(["NewName", "OldName"], string.Empty);

        // The synonym is read...
        Assert.Equal("from the old key", settings.Get("NewName"));

        settings.Set("NewName", "updated");

        var raw = new IniFile();
        raw.LoadFile(path);

        // ...and then removed, so a later version reading only the canonical key sees the right value
        // and there is no second copy to drift out of sync.
        Assert.Equal("updated", raw.Get("NewName"));
        Assert.False(raw.Contains("OldName"));
    }

    [Fact]
    public void SuspendingSaveBatchesWritesIntoOne()
    {
        var path = Path_("batched.cfg");

        var settings = new IniSettingsObject(path);
        settings.RegisterSetting("a", string.Empty);
        settings.RegisterSetting("b", string.Empty);

        settings.SuspendSave();
        settings.Set("a", "1");
        settings.Set("b", "2");

        settings.ResumeSave();

        var raw = new IniFile();
        raw.LoadFile(path);

        Assert.Equal("1", raw.Get("a"));
        Assert.Equal("2", raw.Get("b"));
    }

    [Fact]
    public void ResettingRemovesTheKeyEntirely()
    {
        var path = Path_("reset.cfg");

        var settings = new IniSettingsObject(path);
        settings.RegisterSetting("name", "Untitled");
        settings.Set("name", "My Instance");

        settings.Reset("name");

        var raw = new IniFile();
        raw.LoadFile(path);

        // Removed rather than written as the default, so a later change to the default reaches
        // everyone who never set it.
        Assert.False(raw.Contains("name"));
        Assert.Equal("Untitled", settings.Get("name"));
    }

    // ================================================================== migration

    [Fact]
    public void AnOlderConfigIsMigratedToThePreferredPath()
    {
        var old = WriteConfig("old.cfg", "name", "From The Old Path");
        var preferred = Path_("new.cfg");

        var settings = new IniSettingsObject([preferred, old]);
        settings.RegisterSetting("name", "Untitled");

        // The value comes across...
        Assert.Equal("From The Old Path", settings.Get("name"));

        // ...and the file is now at the preferred path, so the next run does not consult the old one.
        Assert.True(File.Exists(preferred));
        Assert.Equal(preferred, settings.FilePath);
    }

    [Fact]
    public void ChangesAfterAMigrationGoToThePreferredPath()
    {
        var old = WriteConfig("old.cfg", "name", "From The Old Path");
        var preferred = Path_("new.cfg");

        var settings = new IniSettingsObject([preferred, old]);
        settings.RegisterSetting("name", "Untitled");
        settings.Set("name", "Changed");

        var atOld = new IniFile();
        atOld.LoadFile(old);

        // THE POINT OF THE MIGRATION: a fallback chain would write back to the old file forever, so
        // the user's upgrade would never take effect.
        Assert.Equal("From The Old Path", atOld.Get("name"));

        var atNew = new IniFile();
        atNew.LoadFile(preferred);

        Assert.Equal("Changed", atNew.Get("name"));
    }

    [Fact]
    public void AnExistingPreferredConfigIsNeverOverwritten()
    {
        var preferred = WriteConfig("new.cfg", "name", "Current");
        var old = WriteConfig("old.cfg", "name", "Abandoned");

        var settings = new IniSettingsObject([preferred, old]);
        settings.RegisterSetting("name", "Untitled");

        // A real config at the current path wins over a leftover at an old one.
        Assert.Equal("Current", settings.Get("name"));
    }

    [Fact]
    public void TheFirstPathIsUsedWhenNothingExistsYet()
    {
        var preferred = Path_("new.cfg");

        var settings = new IniSettingsObject([preferred, Path_("old.cfg")]);
        settings.RegisterSetting("name", "Untitled");
        settings.Set("name", "Fresh");

        Assert.Equal(preferred, settings.FilePath);
        Assert.True(File.Exists(preferred));
    }

    [Fact]
    public void OnlyTheFirstOlderConfigFoundIsMigrated()
    {
        var preferred = Path_("new.cfg");
        var older = WriteConfig("older.cfg", "name", "Middle");
        var oldest = WriteConfig("oldest.cfg", "name", "Ancient");

        var settings = new IniSettingsObject([preferred, older, oldest]);
        settings.RegisterSetting("name", "Untitled");

        // The list is in preference order, so the nearest ancestor wins.
        Assert.Equal("Middle", settings.Get("name"));
    }

    [Fact]
    public void AnEmptyPathListIsNotACrash()
    {
        var settings = new IniSettingsObject([]);

        Assert.Equal(string.Empty, settings.FilePath);
    }
}
