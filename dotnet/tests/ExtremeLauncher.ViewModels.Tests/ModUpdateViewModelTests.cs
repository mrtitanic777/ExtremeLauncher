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
 * The update dialog's behaviour.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ModUpdateViewModelTests
{
    private static ModUpdate Update(string name) => new(
        Name: name,
        CurrentFileName: name + "-1.jar",
        NewFileName: name + "-2.jar",
        NewVersionName: name + " 2.0",
        DownloadUrl: "https://cdn.invalid/" + name + "-2.jar",
        Sha512: "abc",
        ProjectId: "pid",
        VersionId: "vid");

    private sealed class StubService(params ModUpdate[] updates) : IModUpdateService
    {
        public IReadOnlyList<ModUpdate>? Applied { get; private set; }

        public Exception? CheckThrows { get; set; }

        /// <summary>How many jars Modrinth did not recognise, for the tests that care.</summary>
        public int Unrecognised { get; set; }

        public Task<ModUpdateReport> CheckAsync(CancellationToken cancellationToken)
            => CheckThrows is not null
                ? Task.FromException<ModUpdateReport>(CheckThrows)
                : Task.FromResult(new ModUpdateReport(updates, updates.Length + Unrecognised, Unrecognised));

        public Task<string> ApplyAsync(IReadOnlyList<ModUpdate> updates, CancellationToken cancellationToken)
        {
            Applied = updates;

            return Task.FromResult($"Updated {updates.Count} mods.");
        }
    }

    private static ModUpdateViewModel New(StubService service) => new(service, "Fabric");

    [Fact]
    public async Task CheckingListsWhatCanBeUpdated()
    {
        var vm = New(new StubService(Update("sodium"), Update("lithium")));

        await vm.CheckAsync();

        Assert.Equal(2, vm.Updates.Count);
        Assert.Equal("2 mods can be updated.", vm.Status);
    }

    [Fact]
    public async Task EverythingIsTickedToBeginWith()
    {
        // "Update all" is what pressing the button means.
        var vm = New(new StubService(Update("sodium"), Update("lithium")));

        await vm.CheckAsync();

        Assert.All(vm.Updates, u => Assert.True(u.IsChosen));
        Assert.Equal("Update 2 mods", vm.ApplyLabel);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task NothingToUpdateSaysSoRatherThanShowingAnEmptyList()
    {
        var vm = New(new StubService());

        await vm.CheckAsync();

        Assert.Empty(vm.Updates);
        Assert.Equal("Everything is up to date.", vm.Status);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task AFailedCheckIsNotReportedAsBeingUpToDate()
    {
        /*
         * "Could not check" and "no updates" are different answers, and only one of them means the
         * instance is current. Conflating them is how somebody runs an outdated instance for a month.
         */
        var service = new StubService { CheckThrows = new LauncherException("Could not check for updates: offline") };

        var vm = New(service);

        await vm.CheckAsync();

        Assert.Contains("Could not check", vm.Status, StringComparison.Ordinal);
        Assert.NotEqual("Everything is up to date.", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AVanillaInstanceIsToldWhyRatherThanCheckedPointlessly()
    {
        // Modrinth's filter needs a loader; without one the check comes back empty and reads as
        // "everything is current", which is wrong.
        var vm = new ModUpdateViewModel(new StubService(Update("sodium")), loader: string.Empty);

        await vm.CheckAsync();

        Assert.Empty(vm.Updates);
        Assert.Contains("no mod loader", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UntickingOneLeavesItOutOfTheUpdate()
    {
        var service = new StubService(Update("sodium"), Update("lithium"));

        var vm = New(service);

        await vm.CheckAsync();

        vm.Updates.First(u => u.Name == "lithium").IsChosen = false;

        Assert.Equal(1, vm.ChosenCount);
        Assert.Equal("Update 1 mod", vm.ApplyLabel);

        await vm.ApplyAsync();

        Assert.Single(service.Applied!);
        Assert.Equal("sodium", service.Applied![0].Name);
    }

    [Fact]
    public async Task UntickingEverythingDisablesTheButton()
    {
        var vm = New(new StubService(Update("sodium")));

        await vm.CheckAsync();

        vm.SelectNone();

        Assert.False(vm.CanApply);
        Assert.Equal(0, vm.ChosenCount);

        vm.SelectAll();

        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task TheResultIsShownAndTheDialogStaysOpen()
    {
        /*
         * Some mods may have failed. A window that closes on completion takes the only account of
         * which ones with it.
         */
        var vm = New(new StubService(Update("sodium")));

        await vm.CheckAsync();
        await vm.ApplyAsync();

        Assert.True(vm.IsDone);
        Assert.Equal("Updated 1 mods.", vm.Status);

        // And it stops offering, so the same update cannot be applied twice.
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task TickingAModAnnouncesThatTheButtonShouldChange()
    {
        // Bound to IsEnabled and to a label; a value assertion cannot see a missing notification.
        var vm = New(new StubService(Update("sodium")));

        await vm.CheckAsync();

        var announced = false;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanApply))
            {
                announced = true;
            }
        };

        vm.Updates[0].IsChosen = false;

        Assert.True(announced);
    }

    [Fact]
    public async Task ModsTheServiceCouldNotCheckAreNamedRatherThanCountedAsCurrent()
    {
        /*
         * Only Modrinth is asked. A CurseForge mod is absent from the answer, which is
         * indistinguishable from "already current" -- so a folder full of them would report
         * "Everything is up to date", which is wrong in the way somebody acts on.
         */
        var vm = New(new StubService { Unrecognised = 4 });

        await vm.CheckAsync();

        Assert.Contains("4 mods were not recognised", vm.Status, StringComparison.Ordinal);
        Assert.Contains("CurseForge", vm.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Everything is up to date.", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNothingSkippedThePlainerSentenceIsUsed()
    {
        // No caveat where none is due: qualifying a complete answer makes every answer look doubtful.
        var vm = New(new StubService());

        await vm.CheckAsync();

        Assert.Equal("Everything is up to date.", vm.Status);
    }

    [Fact]
    public void WithNoServiceNothingIsOffered()
    {
        var vm = new ModUpdateViewModel();

        Assert.False(vm.CanCheck);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task ARowSaysWhatWillChange()
    {
        var vm = New(new StubService(Update("sodium")));

        await vm.CheckAsync();

        Assert.Equal("sodium-1.jar  →  sodium-2.jar", vm.Updates[0].Change);
        Assert.Equal("sodium 2.0", vm.Updates[0].NewVersionName);
    }
}
