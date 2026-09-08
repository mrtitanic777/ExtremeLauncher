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
 * For AtlBrowserViewModel: fetch the catalogue once, filter in memory, pick a pack and version, hand
 * back the choice. System packs are hidden, a versionless pack cannot be installed, and a typed name
 * overrides the pack's own — the same contract the legacy-FTB browser has.
 */

using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class AtlBrowserViewModelTests
{
    private sealed class StubSource(IReadOnlyList<AtlIndexedPack> packs) : IAtlPackSource
    {
        public Task<IReadOnlyList<AtlIndexedPack>> FetchAsync(CancellationToken cancellationToken)
            => Task.FromResult(packs);
    }

    private static AtlIndexedPack Pack(
        string name, string[] versions, AtlPackType type = AtlPackType.Public, bool system = false)
    {
        var pack = new AtlIndexedPack { Name = name, Type = type, System = system, Description = name + " desc" };

        foreach (var v in versions)
        {
            pack.Versions.Add(new AtlIndexedVersion { Version = v, Minecraft = "1.12.2" });
        }

        return pack;
    }

    private static AtlBrowserViewModel Loaded()
    {
        var model = new AtlBrowserViewModel(new StubSource(
        [
            Pack("Sky Factory 4", ["4.2.2", "4.2.1"]),
            Pack("All the Mods 3", ["1.0.0"], AtlPackType.Private),
            Pack("Vanilla", []), // no versions — not installable
            Pack("System Pack", ["1.0"], system: true),
        ]));

        model.LoadAsync().GetAwaiter().GetResult();

        return model;
    }

    [Fact]
    public void LoadingPopulatesTheListAndHidesSystemPacks()
    {
        var model = Loaded();

        Assert.Equal(3, model.Packs.Count); // the system pack is dropped
        Assert.DoesNotContain(model.Packs, r => r.Name == "System Pack");
        Assert.Contains(model.Packs, r => r.Name == "All the Mods 3" && r.SourceLabel == "private");
        Assert.Contains(model.Packs, r => r.Name == "Sky Factory 4" && r.SourceLabel == "public");
    }

    [Fact]
    public void SearchFiltersByNameInMemory()
    {
        var model = Loaded();

        model.SearchText = "sky";

        Assert.Equal("Sky Factory 4", Assert.Single(model.Packs).Name);
    }

    [Fact]
    public void SelectingAPackListsItsVersionsAndDefaultsToTheFirst()
    {
        var model = Loaded();

        model.SelectedPack = model.Packs.Single(r => r.Name == "Sky Factory 4");

        Assert.Equal(["4.2.2", "4.2.1"], model.Versions);
        Assert.Equal("4.2.2", model.SelectedVersion);
        Assert.True(model.CanInstall);
        Assert.Equal("Sky Factory 4", model.EffectiveInstanceName);
    }

    [Fact]
    public void AVersionlessPackCannotBeInstalled()
    {
        var model = Loaded();

        model.SelectedPack = model.Packs.Single(r => r.Name == "Vanilla");

        Assert.Empty(model.Versions);
        Assert.False(model.CanInstall);
    }

    [Fact]
    public void AcceptRecordsTheChosenPackAndVersion()
    {
        var model = Loaded();

        model.SelectedPack = model.Packs.Single(r => r.Name == "Sky Factory 4");
        model.SelectedVersion = "4.2.1";
        model.Accept();

        Assert.NotNull(model.Chosen);
        Assert.Equal("Sky Factory 4", model.Chosen!.Value.Pack.Name);
        Assert.Equal("4.2.1", model.Chosen.Value.Version);
    }

    [Fact]
    public void ATypedInstanceNameOverridesThePackName()
    {
        var model = Loaded();

        model.SelectedPack = model.Packs.Single(r => r.Name == "Sky Factory 4");
        model.InstanceName = "  My SF4  ";

        Assert.Equal("My SF4", model.EffectiveInstanceName);
    }
}
