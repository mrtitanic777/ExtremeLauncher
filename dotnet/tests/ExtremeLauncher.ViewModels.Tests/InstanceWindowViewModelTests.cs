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
 * THE FAILURE THIS GUARDS AGAINST IS EDITS VANISHING ON CLOSE -- the most annoying way a settings
 * screen can break, because nothing appears to go wrong at the time.
 *
 * Upstream's BasePage::apply() returns void, so a page that could not write its file has no way to say
 * so and the window closes regardless. Here Save() returns a bool and a failed save keeps the window
 * open, which is the case with the most tests below.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class InstanceWindowViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-iw-" + Guid.NewGuid().ToString("N"));

    public InstanceWindowViewModelTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>A page that does whatever the test needs.</summary>
    private sealed class FakePage(string title, bool dirty = false, bool saveSucceeds = true) : IInstancePage
    {
        public string Title { get; } = title;

        public bool HasUnsavedChanges { get; private set; } = dirty;

        public int SaveAttempts { get; private set; }

        public bool Save()
        {
            SaveAttempts++;

            if (!saveSucceeds)
            {
                return false;
            }

            HasUnsavedChanges = false;

            return true;
        }
    }

    private sealed class ScriptedPrompts(bool answer) : IUserPrompts
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        {
            Asked++;

            return Task.FromResult(answer);
        }

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => throw new InvalidOperationException("The instance window does not ask for text.");
    }

    // ================================================================== the page list

    [Fact]
    public void TheFirstPageAddedIsSelected()
    {
        var window = new InstanceWindowViewModel("My Pack");

        var first = new FakePage("Version");
        window.AddPage(first);
        window.AddPage(new FakePage("Notes"));

        Assert.Same(first, window.SelectedPage);
        Assert.Equal(["Version", "Notes"], window.Pages.Select(p => p.Title));
    }

    [Fact]
    public void TheTitleNamesTheInstance()
        => Assert.Contains("My Pack", new InstanceWindowViewModel("My Pack").Title, StringComparison.Ordinal);

    // ================================================================== closing

    [Fact]
    public async Task WithNothingUnsavedItClosesWithoutAsking()
    {
        var prompts = new ScriptedPrompts(true);
        var window = new InstanceWindowViewModel("My Pack", prompts);

        window.AddPage(new FakePage("Version"));

        Assert.True(await window.RequestCloseAsync().ConfigureAwait(true));
        Assert.Equal(0, prompts.Asked);
    }

    [Fact]
    public async Task AgreeingToSaveWritesEveryDirtyPageAndCloses()
    {
        var window = new InstanceWindowViewModel("My Pack", new ScriptedPrompts(true));

        var version = new FakePage("Version", dirty: true);
        var notes = new FakePage("Notes", dirty: true);
        var clean = new FakePage("Log");

        window.AddPage(version);
        window.AddPage(notes);
        window.AddPage(clean);

        Assert.True(await window.RequestCloseAsync().ConfigureAwait(true));

        Assert.Equal(1, version.SaveAttempts);
        Assert.Equal(1, notes.SaveAttempts);

        // A page with nothing to write is not asked to write.
        Assert.Equal(0, clean.SaveAttempts);
    }

    /*
     * DECLINING DISCARDS AND CLOSES. Nothing on disk was touched, so there is nothing to undo --
     * reopening the window re-reads the files.
     */
    [Fact]
    public async Task DecliningClosesWithoutSaving()
    {
        var window = new InstanceWindowViewModel("My Pack", new ScriptedPrompts(false));

        var page = new FakePage("Version", dirty: true);
        window.AddPage(page);

        Assert.True(await window.RequestCloseAsync().ConfigureAwait(true));
        Assert.Equal(0, page.SaveAttempts);
    }

    /*
     * A FAILED SAVE MUST NOT CLOSE THE WINDOW. Closing would discard exactly the edits the user just
     * asked to keep -- worse than never offering to save at all. Upstream cannot do this: apply()
     * returns void.
     */
    [Fact]
    public async Task AFailedSaveKeepsTheWindowOpenAndSaysWhy()
    {
        var window = new InstanceWindowViewModel("My Pack", new ScriptedPrompts(true));

        window.AddPage(new FakePage("Version", dirty: true, saveSucceeds: false));

        Assert.False(await window.RequestCloseAsync().ConfigureAwait(true));

        Assert.True(window.HasSaveFailure);
        Assert.Contains("Version", window.SaveFailure, StringComparison.Ordinal);
    }

    /*
     * EVERY PAGE IS ATTEMPTED, not just up to the first failure. One page failing is no reason to
     * abandon another page's work, and the user is told about it either way.
     */
    [Fact]
    public void OnePageFailingDoesNotStopTheOthersSaving()
    {
        var window = new InstanceWindowViewModel("My Pack");

        var broken = new FakePage("Version", dirty: true, saveSucceeds: false);
        var fine = new FakePage("Notes", dirty: true);

        window.AddPage(broken);
        window.AddPage(fine);

        Assert.False(window.SaveAll());

        Assert.Equal(1, fine.SaveAttempts);
        Assert.False(fine.HasUnsavedChanges);

        // Named, so the user knows which one to go and look at.
        Assert.Contains("Version", window.SaveFailure, StringComparison.Ordinal);
        Assert.DoesNotContain("Notes", window.SaveFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulSaveClearsAnEarlierFailure()
    {
        var window = new InstanceWindowViewModel("My Pack");

        window.AddPage(new FakePage("Version", dirty: true, saveSucceeds: false));

        Assert.False(window.SaveAll());
        Assert.True(window.HasSaveFailure);

        window.Pages.Clear();
        window.AddPage(new FakePage("Notes", dirty: true));

        Assert.True(window.SaveAll());
        Assert.False(window.HasSaveFailure);
    }

    // ================================================================== the notes page, end to end

    /*
     * The whole save path in miniature, against a real instance.cfg: type, close, reopen, and the text
     * is still there. This is what the container is for.
     */
    [Fact]
    public async Task NotesSurviveBeingSavedAndReloaded()
    {
        var path = Path.Combine(_temp, "instance.cfg");

        File.WriteAllText(path, "name=My Pack\nInstanceType=OneSix\n");

        var globals = GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg"));

        var page = new NotesPageViewModel();
        page.Load(new InstanceSettings(new IniSettingsObject(path), globals));

        Assert.False(page.HasUnsavedChanges);

        page.Text = "Remember to update Sodium before 1.21.";

        Assert.True(page.HasUnsavedChanges);

        var window = new InstanceWindowViewModel("My Pack", new ScriptedPrompts(true));
        window.AddPage(page);

        Assert.True(await window.RequestCloseAsync().ConfigureAwait(true));

        // Re-read from disk entirely: what is in the file is the thing that matters.
        var reopened = new NotesPageViewModel();
        reopened.Load(new InstanceSettings(new IniSettingsObject(path), globals));

        Assert.Equal("Remember to update Sodium before 1.21.", reopened.Text);
        Assert.False(reopened.HasUnsavedChanges);
    }

    /*
     * Typing something and undoing it leaves NOTHING to save. A dirty flag set on every keystroke would
     * still prompt on close, which is how people learn to dismiss that prompt without reading it.
     */
    [Fact]
    public void TypingAndUndoingLeavesNothingToSave()
    {
        var path = Path.Combine(_temp, "instance.cfg");

        File.WriteAllText(path, "name=My Pack\nInstanceType=OneSix\nnotes=Original\n");

        var page = new NotesPageViewModel();

        page.Load(new InstanceSettings(
            new IniSettingsObject(path),
            GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg"))));

        Assert.Equal("Original", page.Text);

        page.Text = "Original and more";

        Assert.True(page.HasUnsavedChanges);

        page.Text = "Original";

        Assert.False(page.HasUnsavedChanges);
    }

    // ================================================================== the window is the launch window

    private sealed class HeldLauncher : IInstanceLauncher
    {
        private readonly TaskCompletionSource _release = new();

        public TaskCompletionSource Started { get; } = new();

        public void Release() => _release.TrySetResult();

        public async Task LaunchAsync(string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
        {
            progress.Log($"Starting {instanceId}");
            Started.TrySetResult();

            await _release.Task.ConfigureAwait(false);
        }
    }

    /*
     * UPSTREAM'S runningStateChanged: the log page is selected the MOMENT a game starts, so pressing
     * Launch puts the output in front of you rather than leaving it somewhere to be found.
     */
    [Fact]
    public async Task StartingAGameJumpsToTheLogPage()
    {
        var launcher = new HeldLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        var window = new InstanceWindowViewModel("My Pack", launch: coordinator) { InstanceId = "MyPack" };

        var version = new FakePage("Version");
        window.AddPage(version);
        window.AddPage(new LogPageViewModel(coordinator));

        // The first page added is selected, which is not the log.
        Assert.Same(version, window.SelectedPage);

        var running = window.LaunchAsync();
        await launcher.Started.Task.ConfigureAwait(true);

        Assert.True(window.IsRunning);
        Assert.IsType<LogPageViewModel>(window.SelectedPage);

        launcher.Release();
        await running.ConfigureAwait(true);

        /*
         * ...and it does NOT jump anywhere when the game exits. Yanking the page out from under someone
         * reading their mods list is the opposite of helpful.
         */
        window.SelectedPage = version;

        Assert.Same(version, window.SelectedPage);
    }

    [Fact]
    public async Task LaunchAndKillFollowTheRunningState()
    {
        var launcher = new HeldLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        var window = new InstanceWindowViewModel("My Pack", launch: coordinator) { InstanceId = "MyPack" };

        Assert.True(window.CanLaunch);
        Assert.False(window.CanKill);

        var running = window.LaunchAsync();
        await launcher.Started.Task.ConfigureAwait(true);

        Assert.False(window.CanLaunch);
        Assert.True(window.CanKill);

        launcher.Release();
        await running.ConfigureAwait(true);

        Assert.True(window.CanLaunch);
        Assert.False(window.CanKill);
    }

    /// <summary>With no coordinator, both buttons stay disabled rather than doing nothing.</summary>
    [Fact]
    public void WithNoLauncherTheButtonsAreDisabled()
    {
        var window = new InstanceWindowViewModel("My Pack");

        Assert.False(window.CanLaunch);
        Assert.False(window.CanKill);
        Assert.False(window.IsRunning);
    }

    /*
     * CLOSING THIS WINDOW DOES NOT STOP THE GAME. Upstream's closeEvent saves the pages and accepts;
     * nothing kills the process. The window is a console onto a running game, not the game itself, and
     * someone tidying their desktop should not lose their session.
     */
    [Fact]
    public async Task ClosingWhileRunningIsAllowedAndLeavesTheGameAlone()
    {
        var launcher = new HeldLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        var window = new InstanceWindowViewModel("My Pack", launch: coordinator) { InstanceId = "MyPack" };

        var running = window.LaunchAsync();
        await launcher.Started.Task.ConfigureAwait(true);

        Assert.True(await window.RequestCloseAsync().ConfigureAwait(true));

        // Still going.
        Assert.True(coordinator.IsBusy);
        Assert.False(running.IsCompleted);

        launcher.Release();
        await running.ConfigureAwait(true);
    }

    // ================================================================== the log page

    [Fact]
    public async Task TheLogPageShowsTheRunsOutputAndKeepsItAfterwards()
    {
        var launcher = new HeldLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        var page = new LogPageViewModel(coordinator);

        Assert.True(page.IsEmpty);
        Assert.False(page.IsRunning);

        var running = coordinator.LaunchAsync("MyPack");
        await launcher.Started.Task.ConfigureAwait(true);

        Assert.True(page.IsRunning);
        Assert.Contains(page.Lines, l => l.Text == "Starting MyPack");

        launcher.Release();
        await running.ConfigureAwait(true);

        /*
         * THE LOG SURVIVES THE GAME EXITING, which is the case this page exists for: the ten seconds
         * after a crash are exactly when it is wanted.
         */
        Assert.False(page.IsRunning);
        Assert.False(page.IsEmpty);
        Assert.Contains("Starting MyPack", page.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogPageHasNothingToSave()
    {
        var page = new LogPageViewModel(new LaunchCoordinator(new HeldLauncher()));

        Assert.False(page.HasUnsavedChanges);
        Assert.True(page.Save());
    }

    // ================================================================== copying the log out

    private sealed class RecordingClipboard(bool works = true) : IClipboard
    {
        public string? Copied { get; private set; }

        public Task<bool> SetTextAsync(string text)
        {
            if (!works)
            {
                return Task.FromResult(false);
            }

            Copied = text;

            return Task.FromResult(true);
        }
    }

    /*
     * The whole reason the log page exists is that somebody is about to paste it into a bug report.
     * Selecting five thousand lines by dragging is not a way to do that.
     */
    [Fact]
    public async Task TheLogCanBeCopiedInOnePiece()
    {
        var launcher = new HeldLauncher();
        var coordinator = new LaunchCoordinator(launcher);
        var clipboard = new RecordingClipboard();

        var page = new LogPageViewModel(coordinator, clipboard);

        var running = coordinator.LaunchAsync("MyPack");
        await launcher.Started.Task.ConfigureAwait(true);

        launcher.Release();
        await running.ConfigureAwait(true);

        await page.CopyAsync().ConfigureAwait(true);

        Assert.Contains("Starting MyPack", clipboard.Copied ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Copied", page.CopyStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyingAnEmptyLogDoesNothing()
    {
        var clipboard = new RecordingClipboard();
        var page = new LogPageViewModel(new LaunchCoordinator(new HeldLauncher()), clipboard);

        await page.CopyAsync().ConfigureAwait(true);

        Assert.Null(clipboard.Copied);
        Assert.Equal(string.Empty, page.CopyStatus);
    }

    /*
     * A BUILD WITH NO CLIPBOARD SAYS SO. Silently doing nothing is the failure mode this port keeps
     * finding, and a Copy button is exactly where it would go unnoticed -- the log is still on screen,
     * so nothing looks wrong until the paste.
     */
    [Fact]
    public async Task AFailedCopySaysSoRatherThanNothing()
    {
        var launcher = new HeldLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        var page = new LogPageViewModel(coordinator, new RecordingClipboard(works: false));

        var running = coordinator.LaunchAsync("MyPack");
        await launcher.Started.Task.ConfigureAwait(true);

        launcher.Release();
        await running.ConfigureAwait(true);

        await page.CopyAsync().ConfigureAwait(true);

        Assert.Contains("Could not copy", page.CopyStatus, StringComparison.Ordinal);
    }

    /// <summary>With no clipboard supplied at all, the default refuses rather than throwing.</summary>
    [Fact]
    public async Task WithNoClipboardTheCopyIsRefusedNotCrashed()
    {
        var launcher = new HeldLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        var page = new LogPageViewModel(coordinator);

        var running = coordinator.LaunchAsync("MyPack");
        await launcher.Started.Task.ConfigureAwait(true);

        launcher.Release();
        await running.ConfigureAwait(true);

        await page.CopyAsync().ConfigureAwait(true);

        Assert.Contains("no clipboard", page.CopyStatus, StringComparison.OrdinalIgnoreCase);
    }
}
