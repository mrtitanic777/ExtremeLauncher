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
 * Choosing what kind of export to make.
 */

using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ExportViewModelTests
{
    private sealed class StubRunner(string target = "C:/out/pack.mrpack") : IExportRunner
    {
        public ExportKind? RanKind { get; private set; }

        public string RanName { get; private set; } = string.Empty;

        public string RanTarget { get; private set; } = string.Empty;

        public string SuggestedSeen { get; private set; } = string.Empty;

        public Task<string> PickTargetAsync(ExportKind kind, string suggestedFileName)
        {
            SuggestedSeen = suggestedFileName;

            return Task.FromResult(target);
        }

        public Task<string> RunAsync(ExportKind kind, string targetPath, string name, string version, string summary)
        {
            RanKind = kind;
            RanName = name;
            RanTarget = targetPath;

            return Task.FromResult("Exported 3 files.");
        }
    }

    [Fact]
    public void ThePackNameStartsAsTheInstanceName()
    {
        // Nobody wants to retype it, and it is right far more often than it is wrong.
        var vm = new ExportViewModel("My Instance", new StubRunner());

        Assert.Equal("My Instance", vm.Name);
    }

    [Fact]
    public void TheSuggestedFileNameFollowsTheKind()
    {
        var vm = new ExportViewModel("My Instance", new StubRunner());

        Assert.Equal("My Instance.mrpack", vm.SuggestedFileName);

        vm.Kind = ExportKind.InstanceZip;

        Assert.Equal("My Instance.zip", vm.SuggestedFileName);
    }

    [Fact]
    public void AnInstanceNameThatIsNotAValidFileNameIsMadeIntoOne()
    {
        // "1.20.1 / Fabric" is an entirely ordinary instance name and not an acceptable file name.
        var vm = new ExportViewModel("1.20.1 / Fabric", new StubRunner()) { Kind = ExportKind.InstanceZip };

        Assert.DoesNotContain('/', vm.SuggestedFileName);
        Assert.EndsWith(".zip", vm.SuggestedFileName, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackFieldsAreHiddenForAPlainZip()
    {
        // A zip has no manifest to put a name, version or summary in.
        var vm = new ExportViewModel("My Instance", new StubRunner());

        Assert.True(vm.ShowsPackFields);

        vm.Kind = ExportKind.InstanceZip;

        Assert.False(vm.ShowsPackFields);
    }

    [Fact]
    public void APackWithNoNameCannotBeExported()
    {
        var vm = new ExportViewModel("My Instance", new StubRunner()) { Name = "   " };

        Assert.False(vm.CanExport);
    }

    [Fact]
    public void AZipNeedsNoNameSoItIsAlwaysAllowed()
    {
        var vm = new ExportViewModel("My Instance", new StubRunner())
        {
            Name = string.Empty,
            Kind = ExportKind.InstanceZip,
        };

        Assert.True(vm.CanExport);
    }

    [Fact]
    public async Task ExportingPassesTheChoicesThrough()
    {
        var runner = new StubRunner();

        var vm = new ExportViewModel("My Instance", runner) { Name = "  Trimmed  " };

        await vm.ExportAsync();

        Assert.Equal(ExportKind.ModrinthPack, runner.RanKind);

        // Trimmed: a leading space in a pack name is invisible and travels to everybody who installs it.
        Assert.Equal("Trimmed", runner.RanName);
        Assert.Equal("C:/out/pack.mrpack", runner.RanTarget);
    }

    [Fact]
    public async Task CancellingTheSaveDialogExportsNothing()
    {
        var runner = new StubRunner(target: string.Empty);

        var vm = new ExportViewModel("My Instance", runner);

        await vm.ExportAsync();

        Assert.Null(runner.RanKind);
        Assert.False(vm.IsDone);
    }

    [Fact]
    public async Task TheResultIsShownAndTheDialogStaysOpen()
    {
        /*
         * An export produces a file somewhere the user then has to find. A window that closes on
         * success takes the only statement of where it went with it.
         */
        var vm = new ExportViewModel("My Instance", new StubRunner());

        await vm.ExportAsync();

        Assert.True(vm.IsDone);
        Assert.Equal("Exported 3 files.", vm.Status);
        Assert.False(vm.IsExporting);
    }

    [Fact]
    public void WithNoRunnerNothingCanBeExported()
    {
        var vm = new ExportViewModel("My Instance");

        Assert.False(vm.CanExport);
    }

    [Fact]
    public void ChangingTheKindAnnouncesThatTheButtonMayChange()
    {
        // Bound to IsEnabled; a value assertion cannot see a missing notification.
        var vm = new ExportViewModel("My Instance", new StubRunner()) { Name = string.Empty };

        var announced = false;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanExport))
            {
                announced = true;
            }
        };

        vm.Kind = ExportKind.InstanceZip;

        Assert.True(announced);
    }

    // ================================================================== CurseForge kind

    [Fact]
    public void ACurseForgePackSuggestsAZipNamedAfterThePack()
    {
        var vm = new ExportViewModel("My Instance", new StubRunner()) { Kind = ExportKind.CurseForgePack, Name = "Cool Pack" };

        Assert.EndsWith(".zip", vm.SuggestedFileName, StringComparison.Ordinal);
        Assert.StartsWith("Cool Pack", vm.SuggestedFileName, StringComparison.Ordinal);
    }

    [Fact]
    public void ACurseForgePackShowsTheNameFieldsAndNeedsAName()
    {
        var vm = new ExportViewModel("My Instance", new StubRunner()) { Kind = ExportKind.CurseForgePack, Name = string.Empty };

        Assert.True(vm.ShowsPackFields);

        // Like the Modrinth pack, a nameless CurseForge pack cannot be exported.
        Assert.False(vm.CanExport);

        vm.Name = "Cool Pack";
        Assert.True(vm.CanExport);
    }

    [Fact]
    public void TheDescriptionSaysWhatCurseForgeCanAndCannotLink()
    {
        // The distinction that trips people up: only CurseForge mods are linked, the rest are carried.
        var vm = new ExportViewModel("My Instance", new StubRunner()) { Kind = ExportKind.CurseForgePack };

        Assert.Contains("CurseForge", vm.KindDescription, StringComparison.Ordinal);
        Assert.Contains("bundled", vm.KindDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportingACurseForgePackPassesTheKindThrough()
    {
        var runner = new StubRunner();

        var vm = new ExportViewModel("My Instance", runner) { Kind = ExportKind.CurseForgePack, Name = "Cool Pack" };

        await vm.ExportAsync();

        Assert.Equal(ExportKind.CurseForgePack, runner.RanKind);
    }
}
