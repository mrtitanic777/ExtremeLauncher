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
 * The copy dialog's view model: a name and the checkboxes that decide what comes across.
 */

using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class CopyInstanceViewModelTests
{
    [Fact]
    public void ItStartsFromTheSourceNameWithEverythingChecked()
    {
        // A copy that quietly left worlds behind would be the opposite of what it is for, so the safe
        // default is to bring everything.
        var vm = new CopyInstanceViewModel("My Pack");

        Assert.Equal("My Pack", vm.Name);

        var prefs = vm.ToChoice().Prefs;

        Assert.True(prefs.CopySaves);
        Assert.True(prefs.CopyMods);
        Assert.True(prefs.CopyResourcePacks);
        Assert.True(prefs.CopyShaderPacks);
        Assert.True(prefs.CopyServers);
        Assert.True(prefs.CopyScreenshots);
        Assert.True(prefs.CopyGameOptions);
        Assert.True(prefs.KeepPlaytime);
    }

    [Fact]
    public void APackWithNoNameCannotBeCopied()
    {
        Assert.False(new CopyInstanceViewModel("   ").CanCopy);
        Assert.True(new CopyInstanceViewModel("Named").CanCopy);
    }

    [Fact]
    public void ChangingTheNameAnnouncesTheButtonMayChange()
    {
        // Bound to the Copy button's IsEnabled; a value assertion cannot see a missing notification.
        var vm = new CopyInstanceViewModel(string.Empty);

        var announced = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanCopy))
            {
                announced = true;
            }
        };

        vm.Name = "Now Named";

        Assert.True(announced);
        Assert.True(vm.CanCopy);
    }

    [Fact]
    public void UncheckingABoxIsCarriedIntoThePrefs()
    {
        var vm = new CopyInstanceViewModel("Slim") { CopyMods = false, CopyScreenshots = false };

        var choice = vm.ToChoice();

        Assert.Equal("Slim", choice.Name);
        Assert.False(choice.Prefs.CopyMods);
        Assert.False(choice.Prefs.CopyScreenshots);

        // The ones left checked are still on.
        Assert.True(choice.Prefs.CopySaves);
    }

    [Fact]
    public void TheNameIsTrimmedIntoTheChoice()
    {
        // A leading space would become a leading space in a directory name; trim it as the copy does.
        Assert.Equal("Trimmed", new CopyInstanceViewModel("  Trimmed  ").ToChoice().Name);
    }

    // ================================================================== copy mode

    [Fact]
    public void AFullCopyIsTheDefaultAndSetsNoLinkFlags()
    {
        var prefs = new CopyInstanceViewModel("P").ToChoice().Prefs;

        Assert.False(prefs.UseClone);
        Assert.False(prefs.UseHardLinks);
        Assert.False(prefs.UseSymLinks);
    }

    [Fact]
    public void CloneModeSetsOnlyTheCloneFlag()
    {
        var prefs = new CopyInstanceViewModel("P") { Mode = InstanceCopyMode.Clone }.ToChoice().Prefs;

        Assert.True(prefs.UseClone);
        Assert.False(prefs.UseHardLinks);
        Assert.False(prefs.UseSymLinks);
    }

    [Fact]
    public void HardLinkModeLinksAndRecurses()
    {
        // A directory cannot be hard-linked, so the hard-link mode must recurse to link each file.
        var prefs = new CopyInstanceViewModel("P") { Mode = InstanceCopyMode.HardLink }.ToChoice().Prefs;

        Assert.True(prefs.UseHardLinks);
        Assert.True(prefs.LinkRecursively);
        Assert.False(prefs.UseSymLinks);
        Assert.False(prefs.UseClone);
    }

    [Fact]
    public void SymLinkModePointsAtWholeFolders()
    {
        // Symbolic links can point at the top-level folders whole, so they do not recurse.
        var prefs = new CopyInstanceViewModel("P") { Mode = InstanceCopyMode.SymLink }.ToChoice().Prefs;

        Assert.True(prefs.UseSymLinks);
        Assert.False(prefs.LinkRecursively);
        Assert.False(prefs.UseHardLinks);
        Assert.False(prefs.UseClone);
    }

    [Fact]
    public void CloneIsOfferedOnlyWhenTheFilesystemSupportsIt()
    {
        Assert.False(new CopyInstanceViewModel("P", cloneAvailable: false).CloneAvailable);
        Assert.True(new CopyInstanceViewModel("P", cloneAvailable: true).CloneAvailable);
    }

    [Fact]
    public void PickingAModeRadioAnnouncesTheOthersChanged()
    {
        // The radios bind to IsModeX bools; selecting one must un-announce the rest or the group shows
        // two filled at once.
        var vm = new CopyInstanceViewModel("P");

        var announcedCopyOff = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsModeCopy))
            {
                announcedCopyOff = true;
            }
        };

        vm.IsModeHardLink = true;

        Assert.True(announcedCopyOff);
        Assert.False(vm.IsModeCopy);
        Assert.True(vm.IsModeHardLink);
        Assert.Equal(InstanceCopyMode.HardLink, vm.Mode);
    }
}
