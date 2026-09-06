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
 * Choosing a Java runtime to install.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class JavaInstallViewModelTests
{
    private static InstallableJava Java(int major, string vendor = "mojang", string uid = "net.minecraft.java")
        => new(
            uid,
            "java" + major,
            new JavaMetadata
            {
                Vendor = vendor,
                Url = $"https://example.invalid/{vendor}-{major}.zip",
                ChecksumHash = $"{vendor}{major}0000000",
                PackageType = "jre",
                RuntimeOS = "windows-x64",
                Version = new JavaVersion(major, 0, 1),
            });

    private sealed class StubService(params InstallableJava[] runtimes) : IJavaInstallService
    {
        public InstallableJava? Installed { get; private set; }

        public Exception? ListThrows { get; set; }

        public Task<IReadOnlyList<InstallableJava>> ListAsync(CancellationToken cancellationToken)
            => ListThrows is not null
                ? Task.FromException<IReadOnlyList<InstallableJava>>(ListThrows)
                : Task.FromResult<IReadOnlyList<InstallableJava>>(runtimes);

        public Task<string> InstallAsync(InstallableJava java, CancellationToken cancellationToken)
        {
            Installed = java;

            return Task.FromResult($"Installed {java.DisplayName}.");
        }
    }

    [Fact]
    public async Task LoadingListsWhatCanBeInstalled()
    {
        var vm = new JavaInstallViewModel(new StubService(Java(21), Java(17)));

        await vm.LoadAsync();

        Assert.Equal(2, vm.Runtimes.Count);
        Assert.Contains("runtimes available", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMajorVersionsAreOfferedNewestFirst()
    {
        // The only number that decides compatibility, and what somebody actually filters by.
        var vm = new JavaInstallViewModel(new StubService(Java(17), Java(21), Java(8)));

        await vm.LoadAsync();

        Assert.Equal([JavaInstallViewModel.AllMajors, "21", "17", "8"], vm.Majors);
    }

    [Fact]
    public async Task FilteringToAMajorNarrowsTheList()
    {
        var vm = new JavaInstallViewModel(new StubService(
            Java(21),
            Java(21, "azul", "com.azul.java"),
            Java(17)));

        await vm.LoadAsync();

        vm.SelectedMajor = "21";

        Assert.Equal(2, vm.Runtimes.Count);
        Assert.All(vm.Runtimes, j => Assert.Equal(21, j.Major));
    }

    [Fact]
    public async Task SomethingIsSelectedSoInstallIsImmediatelyPossible()
    {
        // Opening a picker and having to click a row before anything is offered is a wasted step.
        var vm = new JavaInstallViewModel(new StubService(Java(21), Java(17)));

        await vm.LoadAsync();

        Assert.NotNull(vm.Selected);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task TheSelectionSurvivesAFilterChangeWhereItStillExists()
    {
        var vm = new JavaInstallViewModel(new StubService(Java(21), Java(17)));

        await vm.LoadAsync();

        vm.Selected = vm.Runtimes.Single(j => j.Major == 17);
        vm.SelectedMajor = "17";

        Assert.Equal(17, vm.Selected?.Major);
    }

    [Fact]
    public async Task InstallingPassesTheChosenRuntimeThrough()
    {
        var service = new StubService(Java(21), Java(17));

        var vm = new JavaInstallViewModel(service);

        await vm.LoadAsync();

        vm.Selected = vm.Runtimes.Single(j => j.Major == 17);

        await vm.InstallAsync();

        Assert.Equal(17, service.Installed?.Major);
        Assert.Contains("Installed", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedFetchSaysWhyRatherThanShowingAnEmptyList()
    {
        // An empty list reads as "there is no Java for this machine", which is a much more alarming
        // claim than "the metadata server is unreachable".
        var service = new StubService { ListThrows = new HttpRequestException("meta.invalid unreachable") };

        var vm = new JavaInstallViewModel(service);

        await vm.LoadAsync();

        Assert.Empty(vm.Runtimes);
        Assert.Contains("Could not fetch", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AnEmptyResultIsExplained()
    {
        var vm = new JavaInstallViewModel(new StubService());

        await vm.LoadAsync();

        Assert.Contains("No runtimes were found", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.CanInstall);
    }

    [Fact]
    public void WithNoServiceNothingIsOffered()
    {
        var vm = new JavaInstallViewModel();

        Assert.False(vm.CanLoad);
        Assert.False(vm.CanInstall);
    }

    [Fact]
    public async Task SelectingAnnouncesThatInstallShouldTurnOn()
    {
        // Bound to IsEnabled; a value assertion cannot see a missing notification.
        var vm = new JavaInstallViewModel(new StubService(Java(21)));

        await vm.LoadAsync();

        var announced = false;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanInstall))
            {
                announced = true;
            }
        };

        vm.Selected = null;

        Assert.True(announced);
    }
}
