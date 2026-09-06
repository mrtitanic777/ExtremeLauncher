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
 * The proxy section of the settings window.
 *
 * ASSERTED ON extremelauncher.cfg wherever the value matters, because upstream reads the same file:
 * the four type values are a shared format, not a label this port is free to reword.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ProxySettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "el-proxyui-" + Guid.NewGuid().ToString("N"));

    public ProxySettingsTests() => Directory.CreateDirectory(_folder);

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
    public void EveryProxySettingIsEditable()
    {
        // Four of the five are useless without the fifth, so a missing one is a form that cannot work.
        var vm = new GlobalSettingsViewModel(NewSettings());

        foreach (var key in new[] { "ProxyType", "ProxyAddr", "ProxyPort", "ProxyUser", "ProxyPass" })
        {
            Assert.NotNull(Find(vm, key));
        }
    }

    [Fact]
    public void TheTypeIsAChoiceOfUpstreamsFourValues()
    {
        /*
         * Not a text box. "sock5" typed into one is a setting that silently connects some other way,
         * and the launcher has no way to tell the user so.
         */
        var type = Assert.IsType<ChoiceSettingViewModel>(Find(new GlobalSettingsViewModel(NewSettings()), "ProxyType"));

        Assert.Equal(["Default", "None", "HTTP", "SOCKS5"], type.Choices.Select(c => c.Id));
    }

    [Fact]
    public void UpstreamsExactSpellingIsWhatLandsInTheFile()
    {
        /*
         * THE COMPATIBILITY SURFACE. Upstream compares this string case-sensitively against "SOCKS5",
         * so writing "socks5" would leave a config that this launcher honours and Prism silently
         * ignores -- the two share the file.
         */
        var vm = new GlobalSettingsViewModel(NewSettings());
        var type = (ChoiceSettingViewModel)Find(vm, "ProxyType");

        type.Selected = type.Choices.Single(c => c.Id == "SOCKS5");

        Assert.True(vm.Save());
        Assert.Contains("ProxyType=SOCKS5", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePasswordFieldIsMaskedAndTheUserNameIsNot()
    {
        /*
         * Upstream's ProxyPage.ui sets QLineEdit::Password on the password field only. It buys
         * nothing against anybody holding the config file -- the value is plain text in there either
         * way -- but that is not who it is for: it is for the person standing behind you while you
         * type a work credential into a Minecraft launcher.
         */
        var vm = new GlobalSettingsViewModel(NewSettings());

        Assert.NotEqual('\0', ((TextSettingViewModel)Find(vm, "ProxyPass")).MaskCharacter);
        Assert.Equal('\0', ((TextSettingViewModel)Find(vm, "ProxyUser")).MaskCharacter);
        Assert.Equal('\0', ((TextSettingViewModel)Find(vm, "ProxyAddr")).MaskCharacter);
    }

    [Fact]
    public void TheSectionSaysTheGameDoesNotUseIt()
    {
        /*
         * The single most useful sentence on the page, and the one upstream puts at the very top of
         * it: somebody sets a proxy to get the GAME onto a network, and none of this touches the
         * game. Asserted because a description is the only place that can be said.
         */
        var section = new GlobalSettingsViewModel(NewSettings()).Sections.Single(s => s.Title == "Proxy");

        Assert.Contains("Minecraft", section.Description, StringComparison.Ordinal);
        Assert.Contains("plain text", section.Description, StringComparison.Ordinal);
        Assert.Contains("next start", section.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatWasChosenIsWhatTheHandlerUses()
    {
        /*
         * The join between the two halves of this wave. The settings window and the factory read the
         * same keys, and nothing else asserts they agree -- a renamed key would leave a form that
         * saves happily and changes nothing.
         */
        var settings = NewSettings();
        var vm = new GlobalSettingsViewModel(settings);
        var type = (ChoiceSettingViewModel)Find(vm, "ProxyType");

        type.Selected = type.Choices.Single(c => c.Id == "HTTP");
        ((TextSettingViewModel)Find(vm, "ProxyAddr")).Value = "proxy.example.invalid";
        ((NumberSettingViewModel)Find(vm, "ProxyPort")).Value = 3128;

        Assert.True(vm.Save());

        Assert.Contains("proxy.example.invalid:3128", ProxyFactory.Describe(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshInstallShowsNoProxySelected()
    {
        // Upstream's registered default, and the one somebody behind a corporate proxy has to change.
        var type = (ChoiceSettingViewModel)Find(new GlobalSettingsViewModel(NewSettings()), "ProxyType");

        Assert.Equal("None", type.Value);
    }
}
