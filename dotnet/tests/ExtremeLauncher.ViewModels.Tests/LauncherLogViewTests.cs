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
 * Reading the launcher's OWN log.
 *
 * The reader itself is the instance page's, already tested. What is new is WHICH FOLDER it is pointed
 * at, and that turns out to be the whole design: the data root contains every instance's game logs as
 * well, so aiming it one level too high buries five useful files under several hundred.
 */

using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class LauncherLogViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "el-ownlog-" + Guid.NewGuid().ToString("N"));

    private readonly List<OtherLogsPageViewModel> _models = [];

    public LauncherLogViewTests()
    {
        // A data folder shaped like a real one: the launcher's logs, and an instance with its own.
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        Directory.CreateDirectory(Path.Combine(_root, "instances", "Vanilla", "minecraft", "logs"));

        File.WriteAllText(Path.Combine(_root, "logs", "ExtremeLauncher-0.log"), "[INFO] this run");
        File.WriteAllText(Path.Combine(_root, "logs", "ExtremeLauncher-1.log"), "[INFO] the run before");

        File.WriteAllText(
            Path.Combine(_root, "instances", "Vanilla", "minecraft", "logs", "latest.log"),
            "[Render thread/INFO] the game");
    }

    public void Dispose()
    {
        foreach (var model in _models)
        {
            model.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private OtherLogsPageViewModel Viewing(string folder)
    {
        var model = new OtherLogsPageViewModel();

        _models.Add(model);

        model.Load(folder);

        return model;
    }

    [Fact]
    public void PointedAtTheLogsFolderItFindsTheLaunchersOwnLogs()
    {
        var model = Viewing(Path.Combine(_root, "logs"));

        Assert.Equal(
            ["ExtremeLauncher-0.log", "ExtremeLauncher-1.log"],
            model.Files.Select(f => f.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ItDoesNotDragInEveryInstancesGameLogs()
    {
        /*
         * THE REASON IT IS POINTED AT <root>/logs AND NOT <root>. The reader searches recursively,
         * and a data folder with twenty instances in it holds hundreds of Minecraft logs -- which
         * would bury the five files this window exists to show.
         */
        var model = Viewing(Path.Combine(_root, "logs"));

        Assert.DoesNotContain(model.Files, f => f.Name.Contains("latest", StringComparison.Ordinal));
    }

    [Fact]
    public void PointingItAtTheRootWouldHaveDoneExactlyThat()
    {
        // Asserted rather than assumed, because it is the mistake the comment is warning about and
        // a reader should be able to see that it was real.
        var model = Viewing(_root);

        Assert.Contains(model.Files, f => f.Name.Contains("latest", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLogsAreActuallyReadable()
    {
        var model = Viewing(Path.Combine(_root, "logs"));

        model.Select(model.Files.Single(f => f.Name == "ExtremeLauncher-0.log").Path);

        Assert.Contains("this run", model.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshInstallWithNoLogsYetShowsAnEmptyListRatherThanFailing()
    {
        // The very first start writes its log as it goes, so this is a real moment.
        var model = Viewing(Path.Combine(_root, "no-such-folder"));

        Assert.Empty(model.Files);
        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void TheMenuEntryIsOfferedOnlyWhenSomethingCanShowIt()
    {
        Assert.False(new MainWindowViewModel().CanShowLauncherLog);
    }

    [Fact]
    public async Task ShowingItGoesThroughToTheViewer()
    {
        var viewer = new StubViewer();

        var vm = new MainWindowViewModel(launcherLog: viewer);

        Assert.True(vm.CanShowLauncherLog);

        await vm.ShowLauncherLogAsync();

        Assert.Equal(1, viewer.Shown);
    }

    private sealed class StubViewer : ILauncherLogViewer
    {
        public int Shown { get; private set; }

        public Task ShowAsync()
        {
            Shown++;

            return Task.CompletedTask;
        }
    }
}
