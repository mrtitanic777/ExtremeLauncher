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
 * Picking, importing and removing an instance icon.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class IconPickerViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "el-iconvm-" + Guid.NewGuid().ToString("N"));

    private readonly string _icons;

    public IconPickerViewModelTests()
    {
        _icons = Path.Combine(_root, "icons");

        Directory.CreateDirectory(_icons);
    }

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

    private static readonly string[] BuiltIn = ["default", "creeper_legacy", "steve_legacy"];

    private string WriteIcon(string name, string folder = "")
    {
        var path = Path.Combine(folder.Length == 0 ? _icons : folder, name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G']);

        return path;
    }

    private IconList NewList() => new(_icons, BuiltIn);

    private sealed class StubPicker(string answer) : IIconFilePicker
    {
        public Task<string> PickAsync() => Task.FromResult(answer);
    }

    [Fact]
    public void TheGridShowsEveryIconOnOffer()
    {
        WriteIcon("mine.png");

        var vm = new IconPickerViewModel(NewList());

        Assert.Equal(4, vm.Icons.Count);
        Assert.Contains(vm.Icons, i => i.Key == "mine" && i.IsUserIcon);
        Assert.Contains(vm.Icons, i => i.Key == "creeper_legacy" && !i.IsUserIcon);
    }

    [Fact]
    public void TheInstancesCurrentIconIsSelectedWhenItOpens()
    {
        // Opening a picker that has not found the thing you already chose is a small insult.
        var vm = new IconPickerViewModel(NewList(), "steve_legacy");

        Assert.Equal("steve_legacy", vm.Selected?.Key);
        Assert.True(vm.Selected!.IsSelected);
    }

    [Fact]
    public void AnUnknownCurrentIconFallsBackToTheDefault()
    {
        var vm = new IconPickerViewModel(NewList(), "deleted-long-ago");

        Assert.Equal("default", vm.Selected?.Key);
    }

    [Fact]
    public void AcceptingRecordsTheChosenKey()
    {
        var vm = new IconPickerViewModel(NewList(), "default");

        vm.Selected = vm.Icons.Single(i => i.Key == "creeper_legacy");
        vm.Accept();

        Assert.Equal("creeper_legacy", vm.ChosenKey);
    }

    [Fact]
    public void SelectingOneIconDeselectsTheLast()
    {
        // Two highlighted tiles is a UI saying two different things at once.
        var vm = new IconPickerViewModel(NewList(), "default");

        var first = vm.Selected!;

        vm.Selected = vm.Icons.Single(i => i.Key == "steve_legacy");

        Assert.False(first.IsSelected);
        Assert.True(vm.Selected.IsSelected);
    }

    [Fact]
    public async Task ImportingAddsTheIconAndSelectsIt()
    {
        // Importing and then having to hunt for it in the grid is a small, avoidable annoyance.
        var outside = WriteIcon("brought.png", Path.Combine(_root, "elsewhere"));

        var vm = new IconPickerViewModel(NewList(), "default", new StubPicker(outside));

        await vm.ImportAsync();

        Assert.Equal("brought", vm.Selected?.Key);
        Assert.Contains(vm.Icons, i => i.Key == "brought");
        Assert.True(File.Exists(Path.Combine(_icons, "brought.png")));
    }

    [Fact]
    public async Task ImportingSomethingThatIsNotAnImageSaysSo()
    {
        var path = Path.Combine(_root, "notes.txt");

        File.WriteAllText(path, "hello");

        var vm = new IconPickerViewModel(NewList(), "default", new StubPicker(path));

        await vm.ImportAsync();

        Assert.NotEqual(string.Empty, vm.Status);
    }

    [Fact]
    public async Task CancellingTheFilePickerChangesNothing()
    {
        var vm = new IconPickerViewModel(NewList(), "default", new StubPicker(string.Empty));

        var before = vm.Icons.Count;

        await vm.ImportAsync();

        Assert.Equal(before, vm.Icons.Count);
        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public void RemovingAUserIconTakesItOutOfTheGrid()
    {
        WriteIcon("mine.png");

        var vm = new IconPickerViewModel(NewList(), "default");

        vm.Selected = vm.Icons.Single(i => i.Key == "mine");

        Assert.True(vm.CanRemove);

        vm.Remove();

        Assert.DoesNotContain(vm.Icons, i => i.Key == "mine");
    }

    [Fact]
    public void RemovingTheOneYouWereUsingLeavesTheDefaultSelected()
    {
        // Rather than an empty grid position, or nothing selected at all.
        WriteIcon("mine.png");

        var vm = new IconPickerViewModel(NewList(), "mine");

        Assert.Equal("mine", vm.Selected?.Key);

        vm.Remove();

        Assert.Equal("default", vm.Selected?.Key);
    }

    [Fact]
    public void ABuiltInIconOffersNoRemoveButton()
    {
        var vm = new IconPickerViewModel(NewList(), "steve_legacy");

        Assert.False(vm.CanRemove);
    }

    [Fact]
    public void WithNoFilePickerImportIsNotOffered()
    {
        var vm = new IconPickerViewModel(NewList(), "default");

        Assert.False(vm.CanImport);
    }

    [Fact]
    public void SelectingAnnouncesThatTheRemoveButtonShouldChange()
    {
        /*
         * NOT the same as asserting CanRemove -- reading it recomputes. The XAML binds IsEnabled to
         * it, so without the notification the button is stuck. Fourth appearance of this bug class in
         * this port; it gets a subscription test every time now.
         */
        WriteIcon("mine.png");

        var vm = new IconPickerViewModel(NewList(), "default");

        var announced = false;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanRemove))
            {
                announced = true;
            }
        };

        vm.Selected = vm.Icons.Single(i => i.Key == "mine");

        Assert.True(announced);
    }
}
