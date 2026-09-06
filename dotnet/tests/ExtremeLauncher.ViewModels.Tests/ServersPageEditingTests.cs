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
 * Editing the multiplayer list.
 *
 * MOST OF THIS IS ABOUT NOT WRITING. servers.dat holds the one thing in an instance nobody can
 * reconstruct -- a world can be restored, a mod re-downloaded, but nobody remembers the address of
 * the server they joined once in 2019. So the tests that matter most here are the ones where the
 * page REFUSES: while the game is running, and when the file could not be read.
 *
 * ASSERTED ON THE FILE wherever a save is involved, because the view model agreeing with itself
 * proves nothing about what the game will read.
 */

using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ServersPageEditingTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-servers-" + Guid.NewGuid().ToString("N"));

    public ServersPageEditingTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubPrompts(bool answer) : IUserPrompts
    {
        public int Asked { get; private set; }

        public bool WasDestructive { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        {
            Asked++;
            WasDestructive = destructive;

            return Task.FromResult(answer);
        }

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult<string?>(null);
    }

    private void GiveItServers(params (string Name, string Address)[] servers)
        => ServerList.Save(_temp, servers.Select(s => new MinecraftServer { Name = s.Name, Address = s.Address }));

    private ServersPageViewModel Page(bool confirm = true)
    {
        var page = new ServersPageViewModel(new StubPrompts(confirm));

        page.Load(_temp);

        return page;
    }

    private List<MinecraftServer> OnDisk() => ServerList.Load(_temp);

    // ================================================================== adding

    [Fact]
    public void AddingPutsARowAfterTheSelectionRatherThanAtTheEnd()
    {
        // Upstream's addEmptyRow(currentServer + 1), so somebody grouping servers can put one next to
        // its neighbours.
        GiveItServers(("First", "a"), ("Second", "b"), ("Third", "c"));

        var page = Page();

        // Selected by ADDRESS, which is what Select takes -- the first version of this test passed
        // the name and quietly selected nothing, so the row went to the end and the test failed for
        // a reason that had nothing to do with the code.
        page.Select("a");
        page.Add();

        Assert.Equal(["First", "Minecraft Server", "Second", "Third"], page.Servers.Select(s => s.Name));
    }

    [Fact]
    public void ANewRowIsSelectedSoItCanBeTypedInto()
    {
        var page = Page();

        page.Add();

        Assert.Same(page.Servers[0], page.Selected);
    }

    [Fact]
    public void AddingWithNothingSelectedAppends()
    {
        GiveItServers(("First", "a"));

        var page = Page();

        page.Add();

        Assert.Equal(["First", "Minecraft Server"], page.Servers.Select(s => s.Name));
    }

    [Fact]
    public void AnInstanceThatHasNeverBeenPlayedCanStillGetItsFirstServer()
    {
        /*
         * No servers.dat at all. That is not a failure to read anything -- it is an empty list -- and
         * treating it as unreadable would leave a new instance permanently unable to add a server.
         */
        var page = Page();

        Assert.True(page.CanAdd);

        page.Add();
        page.Servers[0].Address = "first.example.invalid";

        Assert.True(page.Save());
        Assert.Equal("first.example.invalid", OnDisk().Single().Address);
    }

    // ================================================================== removing

    [Fact]
    public async Task RemovingAsksFirstAndSaysItIsPermanent()
    {
        GiveItServers(("Home", "a"));

        var prompts = new StubPrompts(answer: true);
        var page = new ServersPageViewModel(prompts);

        page.Load(_temp);
        page.Select("a");

        await page.RemoveAsync();

        Assert.Equal(1, prompts.Asked);
        Assert.True(prompts.WasDestructive);
        Assert.Empty(page.Servers);
    }

    [Fact]
    public async Task SayingNoRemovesNothing()
    {
        GiveItServers(("Home", "a"));

        var page = Page(confirm: false);

        page.Select("a");

        await page.RemoveAsync();

        Assert.Single(page.Servers);
        Assert.False(page.HasUnsavedChanges);
    }

    [Fact]
    public async Task RemovingSelectsWhatTookItsPlace()
    {
        // So a run of removals does not need a click between each.
        GiveItServers(("First", "a"), ("Second", "b"), ("Third", "c"));

        var page = Page();

        page.Select("b");

        await page.RemoveAsync();

        Assert.Equal("Third", page.Selected?.Name);
    }

    [Fact]
    public async Task RemovingTheLastRowLeavesNothingSelectedRatherThanCrashing()
    {
        GiveItServers(("Only", "a"));

        var page = Page();

        page.Select("a");

        await page.RemoveAsync();

        Assert.Null(page.Selected);
        Assert.False(page.CanRemove);
    }

    // ================================================================== reordering

    [Fact]
    public void MovingChangesTheOrderTheGameWillShow()
    {
        // The file's order IS the multiplayer screen's order, which is the point of the buttons.
        GiveItServers(("First", "a"), ("Second", "b"));

        var page = Page();

        page.Select("b");
        page.MoveUp();

        Assert.True(page.Save());
        Assert.Equal(["Second", "First"], OnDisk().Select(s => s.Name));
    }

    [Fact]
    public void TheSelectionFollowsTheRowRatherThanThePosition()
    {
        GiveItServers(("First", "a"), ("Second", "b"));

        var page = Page();

        page.Select("b");
        page.MoveUp();

        Assert.Equal("Second", page.Selected?.Name);
    }

    [Fact]
    public void TheTopRowCannotGoUpAndTheBottomCannotGoDown()
    {
        GiveItServers(("First", "a"), ("Second", "b"));

        var page = Page();

        page.Select("a");

        Assert.False(page.CanMoveUp);
        Assert.True(page.CanMoveDown);

        page.Select("b");

        Assert.True(page.CanMoveUp);
        Assert.False(page.CanMoveDown);
    }

    // ================================================================== saving

    [Fact]
    public void EditingARowMarksThePageDirty()
    {
        GiveItServers(("Home", "a"));

        var page = Page();

        Assert.False(page.HasUnsavedChanges);

        page.Servers[0].Name = "Renamed";

        Assert.True(page.HasUnsavedChanges);
    }

    [Fact]
    public void ClickingThroughTheListIsNotAnEdit()
    {
        // Otherwise the window asks about unsaved changes for somebody who only looked.
        GiveItServers(("First", "a"), ("Second", "b"));

        var page = Page();

        page.Select("a");
        page.Select("b");

        Assert.False(page.HasUnsavedChanges);
    }

    [Fact]
    public void AnEditReachesTheFile()
    {
        GiveItServers(("Home", "home.example.invalid"));

        var page = Page();

        page.Servers[0].Name = "Renamed";
        page.Servers[0].AcceptsTextures = AcceptsTextures.Always;

        Assert.True(page.Save());

        var saved = OnDisk().Single();

        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(AcceptsTextures.Always, saved.AcceptsTextures);
    }

    [Fact]
    public void SavingClearsTheDirtyFlag()
    {
        GiveItServers(("Home", "a"));

        var page = Page();

        page.Servers[0].Name = "Renamed";

        Assert.True(page.Save());
        Assert.False(page.HasUnsavedChanges);
    }

    [Fact]
    public void AnIconSurvivesAnEditToTheRowItBelongsTo()
    {
        /*
         * The game writes the icon after it has connected once, and nothing here shows or edits it.
         * If the view model did not carry it, renaming one server would silently strip the icons off
         * the whole list -- visible only the next time the multiplayer screen was opened.
         */
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 7, 7 };

        ServerList.Save(_temp, [new MinecraftServer { Name = "Home", Address = "a", Icon = png }]);

        var page = Page();

        page.Servers[0].Name = "Renamed";

        Assert.True(page.Save());
        Assert.Equal(png, OnDisk().Single().Icon);
    }

    [Fact]
    public void SavingWithNothingChangedIsAllowedAndWritesNothing()
    {
        GiveItServers(("Home", "a"));

        var page = Page();

        var before = File.GetLastWriteTimeUtc(Path.Combine(_temp, "servers.dat"));

        Assert.True(page.Save());
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(_temp, "servers.dat")));
    }

    // ================================================================== refusing to write

    [Fact]
    public void AFileThatWouldNotParseIsNeverWrittenOver()
    {
        /*
         * THE ONE THAT MATTERS MOST. ServerList.Load returns an empty list for a corrupt file, which
         * is right for showing a page and catastrophic for saving one: the page would look like an
         * instance with no servers, and saving it would replace forty real ones with nothing.
         *
         * Upstream guards this with m_loaded and logs "Server list should never save if it didn't
         * successfully load".
         */
        var path = Path.Combine(_temp, "servers.dat");

        File.WriteAllText(path, "this is not NBT at all");

        var page = Page();

        Assert.Empty(page.Servers);
        Assert.False(page.IsEditable);
        Assert.False(page.CanAdd);
        Assert.Contains("could not be read", page.LockReason, StringComparison.Ordinal);

        // And the file is still exactly what it was.
        Assert.Equal("this is not NBT at all", File.ReadAllText(path));
    }

    [Fact]
    public void SavingAnUnreadableListReportsFailureRatherThanSucceedingQuietly()
    {
        // The window treats false as a failure and says so. A page that looks saved and is not is
        // worse than one that refuses.
        File.WriteAllText(Path.Combine(_temp, "servers.dat"), "not NBT");

        var page = Page();

        page.HasUnsavedChanges = true;

        Assert.False(page.Save());
    }

    [Fact]
    public void NothingCanBeChangedWhileTheGameIsRunning()
    {
        /*
         * The game REWRITES servers.dat whole when it exits. An edit made while it is up is not
         * merged -- it is replaced, silently, and the player has no reason to suspect it.
         */
        GiveItServers(("Home", "a"));

        var page = Page();

        page.Select("a");

        Assert.True(page.CanRemove);

        page.IsLocked = true;

        Assert.False(page.CanAdd);
        Assert.False(page.CanRemove);
        Assert.False(page.CanMoveUp);
        Assert.False(page.CanMoveDown);
        Assert.False(page.IsEditable);
        Assert.Contains("game is running", page.LockReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockedPageRefusesToSave()
    {
        GiveItServers(("Home", "a"));

        var page = Page();

        page.Servers[0].Name = "Renamed";
        page.IsLocked = true;

        Assert.False(page.Save());
        Assert.Equal("Home", OnDisk().Single().Name);
    }

    [Fact]
    public void TheLockIsAnnouncedSoTheButtonsFollowIt()
    {
        /*
         * The bug class again, and it bites harder here than usual: buttons that stay live after the
         * game starts are buttons that write a file the game is about to overwrite.
         */
        GiveItServers(("Home", "a"));

        var page = Page();

        page.Select("a");

        var announced = new List<string>();

        page.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        page.IsLocked = true;

        Assert.Contains(nameof(ServersPageViewModel.CanAdd), announced);
        Assert.Contains(nameof(ServersPageViewModel.CanRemove), announced);
        Assert.Contains(nameof(ServersPageViewModel.IsEditable), announced);
        Assert.Contains(nameof(ServersPageViewModel.LockReason), announced);
    }

    [Fact]
    public void UnlockingLetsEditingResume()
    {
        // A game that has exited releases the page, and the file it just rewrote is re-read.
        GiveItServers(("Home", "a"));

        var page = Page();

        page.IsLocked = true;
        page.IsLocked = false;

        Assert.True(page.CanAdd);
    }

    // ================================================================== re-reading

    [Fact]
    public void RefreshingPicksUpWhatTheGameWrote()
    {
        GiveItServers(("Home", "a"));

        var page = Page();

        GiveItServers(("Home", "a"), ("Added in game", "b"));

        page.Refresh();

        Assert.Equal(["Home", "Added in game"], page.Servers.Select(s => s.Name));
    }

    [Fact]
    public void RefreshingDoesNotLeaveThePageLookingEdited()
    {
        // Filling the list from disk is not an edit, and a page that says so would ask about unsaved
        // changes every time a game exits.
        GiveItServers(("Home", "a"));

        var page = Page();

        page.Refresh();

        Assert.False(page.HasUnsavedChanges);
    }

    // ================================================================== joining a server

    private sealed class StubJoiner : IServerJoiner
    {
        public string? Joined { get; private set; }

        public Task JoinAsync(string address)
        {
            Joined = address;

            return Task.CompletedTask;
        }
    }

    [Fact]
    public void JoinIsDisabledWithoutAJoiner()
    {
        GiveItServers(("Home", "play.example.net"));

        var page = Page();
        page.Select(page.Servers[0]);

        Assert.False(page.CanJoin);
    }

    [Fact]
    public async Task JoinLaunchesTheSelectedServersAddress()
    {
        GiveItServers(("Home", "play.example.net:25566"));

        var joiner = new StubJoiner();
        var page = new ServersPageViewModel(new StubPrompts(true), joiner);
        page.Load(_temp);
        page.Select(page.Servers[0]);

        Assert.True(page.CanJoin);

        await page.JoinSelectedCommand.ExecuteAsync(null);

        Assert.Equal("play.example.net:25566", joiner.Joined);
    }

    [Fact]
    public void JoinIsDisabledWhileTheGameRuns()
    {
        GiveItServers(("Home", "play.example.net"));

        var page = new ServersPageViewModel(new StubPrompts(true), new StubJoiner());
        page.Load(_temp);
        page.Select(page.Servers[0]);

        page.IsLocked = true;

        // Joining starts a launch, and a second one over a running instance is what the lock prevents.
        Assert.False(page.CanJoin);
    }

    [Fact]
    public async Task JoiningARowWithNoAddressLaunchesNothing()
    {
        // A freshly Added row has no address yet; Join must not turn that into a plain launch.
        var joiner = new StubJoiner();
        var page = new ServersPageViewModel(new StubPrompts(true), joiner);
        page.Load(_temp);
        page.Add();

        await page.JoinSelectedCommand.ExecuteAsync(null);

        Assert.Null(joiner.Joined);
    }
}
