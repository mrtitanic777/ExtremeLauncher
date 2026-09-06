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
 * That the automatic-Java settings actually reach an instance.
 *
 * THESE EXIST BECAUSE OF A BUG A REAL LAUNCH FOUND. AutomaticJavaDownload was registered globally,
 * shown in the settings window, defaulted to true -- and never registered on the instance settings
 * object at all. Reading it returned null, GetBool turned that into false, and a launch that could
 * not find Java 17 refused to fetch one while the tickbox sat there ticked.
 *
 * Nothing caught it: the global tests asserted the global, the settings-page tests asserted the page,
 * and no test had ever asked an INSTANCE what it thought the value was.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AutomaticJavaSettingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "el-autojava-" + Guid.NewGuid().ToString("N"));

    public AutomaticJavaSettingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InstanceSettings NewInstance(out SettingsObject globals)
    {
        globals = GlobalSettings.Create(Path.Combine(_root, "extremelauncher.cfg"));

        var folder = Path.Combine(_root, "instance");

        Directory.CreateDirectory(folder);

        return new InstanceSettings(
            new IniSettingsObject(Path.Combine(folder, "instance.cfg")),
            globals);
    }

    [Fact]
    public void AnInstanceSeesTheGlobalAutomaticDownloadSetting()
    {
        // The default is true, and an instance that reads false would never fetch a Java.
        var instance = NewInstance(out _);

        Assert.True(instance.AutomaticJavaDownload);
    }

    [Fact]
    public void TurningItOffGloballyTurnsItOffForEveryInstance()
    {
        /*
         * The whole point of a passthrough: there is no per-instance gate for this one, so the global
         * IS the answer. Without the registration the instance would keep reporting false whatever
         * the global said -- which is the bug, in the direction that happens to look harmless.
         */
        var instance = NewInstance(out var globals);

        globals.Set("AutomaticJavaDownload", false);

        Assert.False(instance.AutomaticJavaDownload);

        globals.Set("AutomaticJavaDownload", true);

        Assert.True(instance.AutomaticJavaDownload);
    }

    [Fact]
    public void EverySettingTheLaunchPathReadsIsRegisteredOnTheInstance()
    {
        /*
         * The general form of the bug, checked once rather than one property at a time: a global that
         * the launch path reads through InstanceSettings must be REGISTERED there, or it silently
         * reads as a default nobody chose.
         *
         * Asserted on the settings object, not on the C# property, because the property is exactly
         * what would go on quietly returning false.
         */
        var instance = NewInstance(out _);

        foreach (var key in new[]
        {
            "AutomaticJavaDownload",
            "AutomaticJavaSwitch",
            "JavaPath",
            "JvmArgs",
            "MaxMemAlloc",
            "ConsoleMaxLines",
        })
        {
            Assert.True(
                instance.Settings.Contains(key),
                $"{key} is not registered on the instance, so reading it returns a default nobody chose.");
        }
    }
}
