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
 */

using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class LegacyFtbBrowserViewModelTests
{
    private sealed class StubSource(LegacyFtbFetchResult result) : ILegacyFtbSource
    {
        public Task<LegacyFtbFetchResult> FetchAsync(CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private static LegacyFtbModpack Pack(
        string name, LegacyFtbPackType type, string[] oldVersions, string current = "", bool broken = false)
        => new()
        {
            Name = name,
            Author = "FTB",
            OldVersions = [.. oldVersions],
            CurrentVersion = current,
            Type = type,
            Broken = broken,
        };

    private static LegacyFtbBrowserViewModel Loaded()
    {
        var result = new LegacyFtbFetchResult(
            [Pack("Direwolf20", LegacyFtbPackType.Public, ["1.0.0", "2.0.0"], current: "2.0.0"),
             Pack("Brokenpack", LegacyFtbPackType.Public, [], broken: true)],
            [Pack("Crash Landing", LegacyFtbPackType.ThirdParty, ["0.9.0"], current: "0.9.0")],
            []);

        var model = new LegacyFtbBrowserViewModel(new StubSource(result));
        model.LoadAsync().GetAwaiter().GetResult();

        return model;
    }

    [Fact]
    public void LoadingPopulatesBothLists()
    {
        var model = Loaded();

        Assert.Equal(3, model.Packs.Count);
        Assert.Contains(model.Packs, r => r.Name == "Crash Landing" && r.SourceLabel == "third-party");
        Assert.Contains(model.Packs, r => r.Name == "Direwolf20" && r.SourceLabel == "public");
    }

    [Fact]
    public void SearchFiltersByNameInMemory()
    {
        var model = Loaded();

        model.SearchText = "crash";

        var row = Assert.Single(model.Packs);
        Assert.Equal("Crash Landing", row.Name);
    }

    [Fact]
    public void SelectingAPackListsItsVersionsNewestFirstAndDefaultsToCurrent()
    {
        var model = Loaded();

        model.SelectedPack = model.Packs.Single(r => r.Name == "Direwolf20");

        Assert.Equal(["2.0.0", "1.0.0"], model.Versions);
        Assert.Equal("2.0.0", model.SelectedVersion);
        Assert.True(model.CanInstall);
        Assert.Equal("Direwolf20", model.EffectiveInstanceName);
    }

    [Fact]
    public void ABrokenPackCannotBeInstalled()
    {
        var model = Loaded();

        model.SelectedPack = model.Packs.Single(r => r.Name == "Brokenpack");

        Assert.Empty(model.Versions);
        Assert.False(model.CanInstall);
    }

    [Fact]
    public void AcceptRecordsTheChosenPackAndVersion()
    {
        var model = Loaded();

        model.SelectedPack = model.Packs.Single(r => r.Name == "Direwolf20");
        model.SelectedVersion = "1.0.0";
        model.Accept();

        Assert.NotNull(model.Chosen);
        Assert.Equal("Direwolf20", model.Chosen!.Value.Pack.Name);
        Assert.Equal("1.0.0", model.Chosen.Value.Version);
    }

    [Fact]
    public void ATypedInstanceNameOverridesThePackName()
    {
        var model = Loaded();

        model.SelectedPack = model.Packs.Single(r => r.Name == "Direwolf20");
        model.InstanceName = "  My DW20  ";

        Assert.Equal("My DW20", model.EffectiveInstanceName);
    }
}
