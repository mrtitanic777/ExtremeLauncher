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
 * What the About window says.
 *
 * Worth testing at all because its whole job is to be ACCURATE: every line here ends up pasted into a
 * bug report, and a version string that quietly drops the channel sends somebody looking at the wrong
 * source tree.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class AboutViewModelTests
{
    private static BuildConfig Config(string channel = "develop", string commit = "") => new()
    {
        LauncherDisplayName = "Extreme Launcher",
        VersionMajor = 5,
        VersionMinor = 1,
        VersionChannel = channel,
        GitCommit = commit,
        BuildPlatform = "custom",
        Copyright = "Extreme Launcher Contributors",
    };

    [Fact]
    public void TheChannelIsShownWhenItIsNotARelease()
    {
        /*
         * "5.1" from a develop build and "5.1" from a release are different software, and only one of
         * them is something a user could have downloaded. Dropping the channel sends whoever reads the
         * bug report to the wrong source tree.
         */
        Assert.Equal("5.1 (develop)", new AboutViewModel(Config()).Version);
    }

    [Fact]
    public void AReleaseIsJustTheVersion()
    {
        // No noise where none is due.
        Assert.Equal("5.1", new AboutViewModel(Config(channel: "stable")).Version);
    }

    [Fact]
    public void TheCommitIsHiddenWhenTheBuildDidNotRecordOne()
    {
        // An empty row labelled "Commit" is worse than no row: it reads as a build with no commit.
        var without = new AboutViewModel(Config());

        Assert.False(without.HasCommit);

        var with = new AboutViewModel(Config(commit: "f9ad75bd8"));

        Assert.True(with.HasCommit);
        Assert.Equal("f9ad75bd8", with.Commit);
    }

    [Fact]
    public void TheRuntimeIsTheRealOneRatherThanTheBuildString()
    {
        /*
         * This port runs ONE build on three desktops, so BuildPlatform cannot answer "which OS is
         * this". The runtime line can, and it is the question a crash report actually turns on.
         */
        var vm = new AboutViewModel(Config());

        Assert.Contains(".NET", vm.Runtime, StringComparison.Ordinal);
        Assert.NotEqual(vm.Platform, vm.Runtime);
    }

    [Fact]
    public void TheDataDirectoryIsShownWhenThereIsOne()
    {
        // The other thing support always asks, and it can be moved with --dir.
        var vm = new AboutViewModel(Config(), dataDirectory: "C:/games/launcher");

        Assert.True(vm.HasDataDirectory);
        Assert.Equal("C:/games/launcher", vm.DataDirectory);

        Assert.False(new AboutViewModel(Config()).HasDataDirectory);
    }

    [Fact]
    public void TheLicenceIsStatedRatherThanLinked()
    {
        // This is GPL-3.0 software and the licence requires the user be told. A version number and a
        // URL do not do that.
        var licence = new AboutViewModel(Config()).License;

        Assert.Contains("GNU General Public License", licence, StringComparison.Ordinal);
        Assert.Contains("version 3", licence, StringComparison.Ordinal);
        Assert.Contains("WITHOUT ANY WARRANTY", licence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSupportSummaryCarriesEverythingWorthPasting()
    {
        /*
         * The reason the dialog has a copy button. Asking somebody to retype a commit hash is how bug
         * reports arrive with the wrong one.
         */
        var vm = new AboutViewModel(Config(commit: "f9ad75bd8"), dataDirectory: "C:/games/launcher");

        var summary = vm.SupportSummary;

        Assert.Contains("Extreme Launcher 5.1 (develop)", summary, StringComparison.Ordinal);
        Assert.Contains("f9ad75bd8", summary, StringComparison.Ordinal);
        Assert.Contains("C:/games/launcher", summary, StringComparison.Ordinal);
        Assert.Contains(".NET", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryOmitsWhatIsNotKnownRatherThanShowingBlanks()
    {
        // A pasted report with "Commit:" and nothing after it wastes a round trip asking about it.
        var summary = new AboutViewModel(Config()).SupportSummary;

        Assert.DoesNotContain("Commit:", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Data directory:", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoConfigItDescribesTheRunningLauncher()
    {
        // What the window actually does.
        var vm = new AboutViewModel();

        Assert.Equal(BuildConfig.Instance.LauncherDisplayName, vm.Name);
    }
}
