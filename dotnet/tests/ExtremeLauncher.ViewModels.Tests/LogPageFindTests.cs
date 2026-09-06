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
 * Finding text in the launch log: next/previous with wrap-around, over the live line collection.
 */

using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class LogPageFindTests
{
    private sealed class IdleLauncher : IInstanceLauncher
    {
        public Task LaunchAsync(string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
            => Task.CompletedTask;
    }

    private static LogPageViewModel PageWith(params string[] lines)
    {
        var coordinator = new LaunchCoordinator(new IdleLauncher());

        foreach (var line in lines)
        {
            ((IProgressSink)coordinator).Log(line);
        }

        return new LogPageViewModel(coordinator);
    }

    [Fact]
    public void WithNoSearchNothingIsFindable()
    {
        var page = PageWith("alpha", "beta");

        Assert.False(page.CanFind);
        Assert.Equal(string.Empty, page.MatchSummary);
        Assert.Equal(-1, page.CurrentMatchIndex);
    }

    [Fact]
    public void FindNextWalksTheMatchesAndWrapsAround()
    {
        var page = PageWith("alpha", "beta match", "gamma", "delta match", "epsilon");
        page.SearchText = "match";

        Assert.True(page.CanFind);

        page.FindNextCommand.Execute(null);
        Assert.Equal(1, page.CurrentMatchIndex);
        Assert.Equal("1 of 2", page.MatchSummary);

        page.FindNextCommand.Execute(null);
        Assert.Equal(3, page.CurrentMatchIndex);
        Assert.Equal("2 of 2", page.MatchSummary);

        // Past the last, back to the top.
        page.FindNextCommand.Execute(null);
        Assert.Equal(1, page.CurrentMatchIndex);
        Assert.Equal("1 of 2", page.MatchSummary);
    }

    [Fact]
    public void FindPreviousWrapsToTheBottom()
    {
        var page = PageWith("a match", "b", "c match");
        page.SearchText = "match";

        // From nothing selected, Previous lands on the last match.
        page.FindPreviousCommand.Execute(null);
        Assert.Equal(2, page.CurrentMatchIndex);

        page.FindPreviousCommand.Execute(null);
        Assert.Equal(0, page.CurrentMatchIndex);

        // Before the first, back to the bottom.
        page.FindPreviousCommand.Execute(null);
        Assert.Equal(2, page.CurrentMatchIndex);
    }

    [Fact]
    public void TheSearchIsCaseInsensitive()
    {
        var page = PageWith("Loading MODS", "done");
        page.SearchText = "mods";

        page.FindNextCommand.Execute(null);

        Assert.Equal(0, page.CurrentMatchIndex);
    }

    [Fact]
    public void AQueryThatMatchesNothingSaysSo()
    {
        var page = PageWith("alpha", "beta");
        page.SearchText = "zzz";

        page.FindNextCommand.Execute(null);

        Assert.Equal(-1, page.CurrentMatchIndex);
        Assert.Equal("No matches", page.MatchSummary);
    }

    [Fact]
    public void ChangingTheQueryStartsTheWalkOver()
    {
        var page = PageWith("a match", "b match");
        page.SearchText = "match";
        page.FindNextCommand.Execute(null);
        Assert.Equal(0, page.CurrentMatchIndex);

        // A new query resets: the next Find starts from the top again.
        page.SearchText = "b match";
        Assert.Equal(-1, page.CurrentMatchIndex);

        page.FindNextCommand.Execute(null);
        Assert.Equal(1, page.CurrentMatchIndex);
    }
}
