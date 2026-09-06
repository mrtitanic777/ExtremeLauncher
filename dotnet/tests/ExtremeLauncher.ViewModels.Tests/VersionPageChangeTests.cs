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
 * Changing a component's version, and adding a loader to an instance that has none.
 *
 * ASSERTED ON mmc-pack.json where the question is "did that stick". The file is a compatibility
 * surface -- an existing Prism or MultiMC instance has exactly this shape -- so reading the change
 * back through the same PackProfile that made it would prove only that the pair agree.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class VersionPageChangeTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "el-vpc-" + Guid.NewGuid().ToString("N"));

    public VersionPageChangeTests() => Directory.CreateDirectory(_folder);

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

    private string PackPath => Path.Combine(_folder, "mmc-pack.json");

    /// <summary>Answers with a fixed version, and records what it was asked.</summary>
    private sealed class StubChooser(string answer) : IVersionChooser
    {
        public string? AskedUid { get; private set; }

        public string? AskedMinecraftVersion { get; private set; }

        public Task<string> ChooseAsync(string uid, string title, string minecraftVersion)
        {
            AskedUid = uid;
            AskedMinecraftVersion = minecraftVersion;

            return Task.FromResult(answer);
        }
    }

    private PackProfile NewProfile(string minecraft = "1.20.1")
    {
        var profile = new PackProfile(LauncherService.CurrentRuntimeContext());

        profile.SetComponentVersion("net.minecraft", minecraft, important: true);

        return profile;
    }

    [Fact]
    public async Task ChangingTheMinecraftVersionWritesItToThePackFile()
    {
        var chooser = new StubChooser("1.20.4");
        var page = new VersionPageViewModel(chooser);
        var profile = NewProfile();

        page.Load(profile, PackPath);
        page.Select("net.minecraft");

        Assert.True(page.CanPickVersion);

        await page.ChangeVersionAsync();

        Assert.True(page.HasUnsavedChanges);
        Assert.True(page.Save());

        Assert.Contains("1.20.4", File.ReadAllText(PackPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MinecraftIsNotFilteredAgainstItsOwnVersion()
    {
        /*
         * Minecraft requires nothing, so passing the instance's current version as a filter would
         * leave a list holding only the version already set -- a "change version" dialog offering
         * exactly one choice, the one you have.
         */
        var chooser = new StubChooser("1.20.4");
        var page = new VersionPageViewModel(chooser);

        page.Load(NewProfile(), PackPath);
        page.Select("net.minecraft");

        await page.ChangeVersionAsync();

        Assert.Equal(string.Empty, chooser.AskedMinecraftVersion);
    }

    [Fact]
    public async Task ALoaderIsOfferedOnlyBuildsForThisInstancesMinecraftVersion()
    {
        var chooser = new StubChooser("47.1.0");
        var page = new VersionPageViewModel(chooser);

        page.Load(NewProfile("1.20.1"), PackPath);

        Assert.True(page.CanAddLoader);

        await page.AddLoaderAsync("net.minecraftforge");

        Assert.Equal("net.minecraftforge", chooser.AskedUid);
        Assert.Equal("1.20.1", chooser.AskedMinecraftVersion);
    }

    [Fact]
    public async Task AddingALoaderPutsItInThePackFile()
    {
        var page = new VersionPageViewModel(new StubChooser("0.15.7"));
        var profile = NewProfile();

        page.Load(profile, PackPath);

        await page.AddLoaderAsync("net.fabricmc.fabric-loader");

        Assert.True(page.Save());

        var written = File.ReadAllText(PackPath);

        Assert.Contains("net.fabricmc.fabric-loader", written, StringComparison.Ordinal);
        Assert.Contains("0.15.7", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallingALoaderOverAnExistingOneReplacesItRatherThanAddingASecond()
    {
        /*
         * SetComponentVersion both adds and replaces, which is why upstream's InstallLoaderDialog does
         * not remove the old one first. Two Fabric components in one profile would resolve to
         * whichever came last, silently.
         */
        var page = new VersionPageViewModel(new StubChooser("0.15.7"));

        page.Load(NewProfile(), PackPath);

        await page.AddLoaderAsync("net.fabricmc.fabric-loader");

        var afterFirst = page.Components.Count;

        page = new VersionPageViewModel(new StubChooser("0.16.0"));
        page.Load(NewProfile(), PackPath);

        await page.AddLoaderAsync("net.fabricmc.fabric-loader");
        await page.AddLoaderAsync("net.fabricmc.fabric-loader");

        Assert.Equal(afterFirst, page.Components.Count);
        Assert.Single(page.Components, c => c.Uid == "net.fabricmc.fabric-loader");
    }

    [Fact]
    public async Task CancellingTheDialogChangesNothing()
    {
        // An empty answer is what the dialog returns when it is closed without choosing.
        var page = new VersionPageViewModel(new StubChooser(string.Empty));

        page.Load(NewProfile(), PackPath);
        page.Select("net.minecraft");

        await page.ChangeVersionAsync();

        Assert.False(page.HasUnsavedChanges);
    }

    [Fact]
    public void WithNoChooserTheButtonsStayDisabledRatherThanDoingNothing()
    {
        var page = new VersionPageViewModel();

        page.Load(NewProfile(), PackPath);
        page.Select("net.minecraft");

        /*
         * CanChangeVersion is upstream's rule -- a version list already in memory -- and is false here
         * because nothing has walked the metadata index. CanPickVersion deliberately does not use it
         * (see its remarks); what makes it false here is having no chooser to pick with.
         */
        Assert.False(page.CanChangeVersion);
        Assert.False(page.CanPickVersion);
        Assert.False(page.CanAddLoader);
    }

    [Fact]
    public void SelectingAComponentAnnouncesThatTheButtonShouldTurnOn()
    {
        /*
         * NOT the same as asserting CanPickVersion is true -- reading it recomputes. The XAML binds
         * IsEnabled to it, so without the notification the button never wakes up. This is the third
         * time this exact bug class has come up in this port; it gets a test each time now.
         */
        var page = new VersionPageViewModel(new StubChooser("1.20.4"));

        page.Load(NewProfile(), PackPath);

        var announced = false;

        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(page.CanPickVersion))
            {
                announced = true;
            }
        };

        page.Select("net.minecraft");

        Assert.True(announced);
    }

    [Fact]
    public void AnInstanceWithNoMinecraftComponentCannotHaveALoaderAdded()
    {
        // There would be nothing to filter the loader list against, so every build would be offered.
        var page = new VersionPageViewModel(new StubChooser("0.15.7"));

        page.Load(new PackProfile(LauncherService.CurrentRuntimeContext()), PackPath);

        Assert.False(page.CanAddLoader);
    }
}
