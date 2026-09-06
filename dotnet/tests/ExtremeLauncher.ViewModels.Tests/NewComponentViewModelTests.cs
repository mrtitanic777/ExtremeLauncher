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
 * The new-component dialog's view model: a uid and a name, with the uid checked for clashes.
 */

using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class NewComponentViewModelTests
{
    [Fact]
    public void BothFieldsAreNeeded()
    {
        var vm = new NewComponentViewModel();

        Assert.False(vm.CanCreate);

        vm.Name = "My Tweaks";
        Assert.False(vm.CanCreate);

        vm.Uid = "com.example.tweaks";
        Assert.True(vm.CanCreate);
    }

    [Fact]
    public void AClashingUidIsRefusedAndFlagged()
    {
        var vm = new NewComponentViewModel(["net.minecraft"]) { Name = "Impostor", Uid = "net.minecraft" };

        Assert.True(vm.UidClashes);
        Assert.False(vm.CanCreate);

        vm.Uid = "com.example.new";
        Assert.False(vm.UidClashes);
        Assert.True(vm.CanCreate);
    }

    [Fact]
    public void TheChoiceIsTrimmed()
    {
        var vm = new NewComponentViewModel { Uid = "  com.example.x  ", Name = "  Nice  " };

        var choice = vm.ToChoice();

        Assert.Equal("com.example.x", choice.Uid);
        Assert.Equal("Nice", choice.Name);
    }

    [Fact]
    public void ChangingAFieldAnnouncesTheButtonMayChange()
    {
        var vm = new NewComponentViewModel();

        var announced = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanCreate))
            {
                announced = true;
            }
        };

        vm.Uid = "com.example.x";

        Assert.True(announced);
    }
}
