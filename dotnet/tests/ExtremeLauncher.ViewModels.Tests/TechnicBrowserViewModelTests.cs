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
 * For TechnicBrowserViewModel: search-driven, so loading runs the trending search and typing + Search
 * runs a query. Selecting a pack enables install, Accept hands it back, and a typed name overrides the
 * pack's own.
 */

using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class TechnicBrowserViewModelTests
{
    /// <summary>Answers with a fixed trending list, and echoes the query as a single result otherwise.</summary>
    private sealed class StubSource : ITechnicPackSource
    {
        public string? LastTerm { get; private set; }

        public Task<IReadOnlyList<TechnicModpack>> SearchAsync(string term, CancellationToken cancellationToken)
        {
            LastTerm = term;

            IReadOnlyList<TechnicModpack> result = term.Length == 0
                ? [Pack("Tekkit"), Pack("Hexxit")]
                : [Pack(term + " result")];

            return Task.FromResult(result);
        }
    }

    private static TechnicModpack Pack(string name)
        => new() { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-') };

    [Fact]
    public void LoadingRunsTheTrendingSearch()
    {
        var source = new StubSource();
        var model = new TechnicBrowserViewModel(source);

        model.LoadAsync().GetAwaiter().GetResult();

        Assert.Equal(string.Empty, source.LastTerm); // empty term = trending
        Assert.Equal(["Tekkit", "Hexxit"], model.Packs.Select(p => p.Name));
    }

    [Fact]
    public void SearchingRunsTheQueryAndReplacesTheResults()
    {
        var model = new TechnicBrowserViewModel(new StubSource());
        model.LoadAsync().GetAwaiter().GetResult();

        model.SearchText = "magic";
        model.SearchAsync().GetAwaiter().GetResult();

        Assert.Equal("magic result", Assert.Single(model.Packs).Name);
    }

    [Fact]
    public void SelectingAPackEnablesInstall()
    {
        var model = new TechnicBrowserViewModel(new StubSource());
        model.LoadAsync().GetAwaiter().GetResult();

        Assert.False(model.CanInstall);

        model.SelectedPack = model.Packs[0];

        Assert.True(model.CanInstall);
        Assert.Equal("Tekkit", model.EffectiveInstanceName);
    }

    [Fact]
    public void AcceptRecordsTheChosenPack()
    {
        var model = new TechnicBrowserViewModel(new StubSource());
        model.LoadAsync().GetAwaiter().GetResult();

        model.SelectedPack = model.Packs.Single(p => p.Name == "Hexxit");
        model.Accept();

        Assert.NotNull(model.Chosen);
        Assert.Equal("Hexxit", model.Chosen!.Name);
    }

    [Fact]
    public void AcceptWithNoSelectionRecordsNothing()
    {
        var model = new TechnicBrowserViewModel(new StubSource());
        model.LoadAsync().GetAwaiter().GetResult();

        model.Accept();

        Assert.Null(model.Chosen);
    }

    [Fact]
    public void ATypedInstanceNameOverridesThePackName()
    {
        var model = new TechnicBrowserViewModel(new StubSource());
        model.LoadAsync().GetAwaiter().GetResult();

        model.SelectedPack = model.Packs[0];
        model.InstanceName = "  My Tekkit  ";

        Assert.Equal("My Tekkit", model.EffectiveInstanceName);
    }
}
