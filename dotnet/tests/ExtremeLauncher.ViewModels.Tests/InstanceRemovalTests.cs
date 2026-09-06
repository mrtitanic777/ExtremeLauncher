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
 * THE MOST IMPORTANT TEST IN THIS FILE IS THAT "NO" MEANS NO. Everything else here is a message.
 *
 * Upstream has no tests for any of this because every branch ends in a QMessageBox, and a dialog is
 * only testable by clicking it. With the prompts behind an interface, a test can answer "no" and then
 * check that the worlds are still on disk -- which is the assertion that actually matters.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class InstanceRemovalTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-rm-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public InstanceRemovalTests()
    {
        _instances = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(_instances);
    }

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

    /// <summary>Answers whatever the test says, and records what it was asked.</summary>
    private sealed class ScriptedPrompts(params bool[] answers) : IUserPrompts
    {
        private int _asked;

        public List<string> Questions { get; } = [];

        public List<string> Messages { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        {
            Questions.Add(title);
            Messages.Add(message);

            return Task.FromResult(_asked < answers.Length && answers[_asked++]);
        }

        /// <summary>Removal never asks for text; a call here would mean the flow took a wrong turn.</summary>
        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => throw new InvalidOperationException("Removing an instance must not ask for text.");
    }

    private string MakeInstance(string id)
    {
        var path = Path.Combine(_instances, id);
        var saves = Path.Combine(path, "minecraft", "saves", "My World");

        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(path, "instance.cfg"), $"name={id}\nInstanceType=OneSix\n");
        File.WriteAllText(Path.Combine(saves, "level.dat"), "irreplaceable");

        return path;
    }

    private InstanceList NewList()
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        return list;
    }

    // ================================================================== no means no

    /*
     * The one that matters. Everything below is wording; this is whether a person's "no" is honoured
     * when the alternative is losing worlds that exist nowhere else.
     */
    [Fact]
    public async Task DecliningLeavesTheInstanceCompletelyUntouched()
    {
        var path = MakeInstance("Precious");
        var list = NewList();

        var removal = new InstanceRemoval(new ScriptedPrompts(false));

        Assert.Equal(RemovalOutcome.Cancelled, await removal.RemoveAsync(list, "Precious", "Precious").ConfigureAwait(true));

        Assert.True(Directory.Exists(path));
        Assert.Equal("irreplaceable", File.ReadAllText(Path.Combine(path, "minecraft", "saves", "My World", "level.dat")));
        Assert.NotNull(list.GetInstanceById("Precious"));
    }

    /*
     * A BUILD WITH NO DIALOGS MUST NOT DELETE. The default prompts answer "no" to everything, so a
     * front-end that forgot to supply them fails safe rather than destroying instances silently.
     */
    [Fact]
    public async Task WithNoPromptsWiredUpNothingIsRemoved()
    {
        var path = MakeInstance("Precious");
        var list = NewList();

        var removal = new InstanceRemoval();

        Assert.Equal(RemovalOutcome.Cancelled, await removal.RemoveAsync(list, "Precious", "Precious").ConfigureAwait(true));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task RemovingNothingAsksNothing()
    {
        var prompts = new ScriptedPrompts(true);
        var removal = new InstanceRemoval(prompts);

        Assert.Equal(
            RemovalOutcome.Cancelled,
            await removal.RemoveAsync(NewList(), string.Empty, "?").ConfigureAwait(true));

        Assert.Empty(prompts.Questions);
    }

    /// <summary>An instance already gone is not worth a dialog about deleting it.</summary>
    [Fact]
    public async Task RemovingSomethingAlreadyGoneAsksNothing()
    {
        var prompts = new ScriptedPrompts(true);
        var removal = new InstanceRemoval(prompts);

        Assert.Equal(
            RemovalOutcome.Cancelled,
            await removal.RemoveAsync(NewList(), "NeverExisted", "NeverExisted").ConfigureAwait(true));

        Assert.Empty(prompts.Questions);
    }

    // ================================================================== what the question says

    /*
     * THE NAME THE USER SEES IS IN THE QUESTION. "Delete this instance?" beside a grid of similar tiles
     * is a question about which one, and the wrong answer is unrecoverable.
     */
    [Fact]
    public async Task TheQuestionNamesTheInstanceAndWhatGoesWithIt()
    {
        MakeInstance("Precious");

        var prompts = new ScriptedPrompts(false);

        await new InstanceRemoval(prompts).RemoveAsync(NewList(), "Precious", "My Nice Pack").ConfigureAwait(true);

        Assert.Contains("My Nice Pack", prompts.Messages[0], StringComparison.Ordinal);
        Assert.Contains("worlds", prompts.Messages[0], StringComparison.Ordinal);
    }

    /// <summary>Windows users do not have a "Trash" to look in, so the message must not send them there.</summary>
    [Fact]
    public async Task TheQuestionUsesThisDesktopsNameForTheTrash()
    {
        MakeInstance("Precious");

        var prompts = new ScriptedPrompts(false);

        await new InstanceRemoval(prompts).RemoveAsync(NewList(), "Precious", "Precious").ConfigureAwait(true);

        Assert.Contains(InstanceRemoval.TrashName, prompts.Messages[0], StringComparison.Ordinal);
        Assert.Equal(OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash", InstanceRemoval.TrashName);
    }

    // ================================================================== saying yes

    [Fact]
    public async Task AgreeingRemovesTheInstance()
    {
        var path = MakeInstance("Doomed");
        var list = NewList();

        var outcome = await new InstanceRemoval(new ScriptedPrompts(true, true))
            .RemoveAsync(list, "Doomed", "Doomed").ConfigureAwait(true);

        Assert.True(outcome is RemovalOutcome.Trashed or RemovalOutcome.Deleted);
        Assert.False(Directory.Exists(path));
        Assert.Null(list.GetInstanceById("Doomed"));
    }

    /*
     * PERMANENT DELETION IS A SECOND, DIFFERENT QUESTION, asked only when trashing has been tried and
     * failed. Asking it up front would train people to click through a dialog that occasionally means
     * what it says.
     *
     * Provoked for real by holding a file open, so the trash genuinely cannot move the directory.
     */
    [SkippableFact]
    public async Task PermanentDeletionIsAskedSeparatelyAndOnlyAfterTrashingFails()
    {
        // Holding a file open only blocks a move on Windows; Unix trashes an open file happily, so the
        // "trash failed, ask about permanent deletion" path cannot be provoked this way there.
        Skip.IfNot(OperatingSystem.IsWindows(), "Open files only block removal on Windows.");

        var path = MakeInstance("Stuck");
        var list = NewList();

        var prompts = new ScriptedPrompts(true, false);

        using (var _ = new FileStream(
                   Path.Combine(path, "minecraft", "saves", "My World", "level.dat"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var outcome = await new InstanceRemoval(prompts).RemoveAsync(list, "Stuck", "Stuck").ConfigureAwait(true);

            // Asked twice, said no the second time, so nothing was destroyed.
            Assert.Equal(RemovalOutcome.Cancelled, outcome);
            Assert.Equal(2, prompts.Questions.Count);
            Assert.Contains("permanently", prompts.Messages[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cannot be undone", prompts.Messages[1], StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(Directory.Exists(path));
    }

    /// <summary>Both routes failing says why, because "delete failed" invites the same second click.</summary>
    [SkippableFact]
    public async Task FailingBothWaysExplainsItself()
    {
        // Same Windows-only provocation: an open file blocks removal only there.
        Skip.IfNot(OperatingSystem.IsWindows(), "Open files only block removal on Windows.");

        var path = MakeInstance("Stuck");
        var list = NewList();

        var removal = new InstanceRemoval(new ScriptedPrompts(true, true));

        using var _ = new FileStream(
            Path.Combine(path, "minecraft", "saves", "My World", "level.dat"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        Assert.Equal(RemovalOutcome.Failed, await removal.RemoveAsync(list, "Stuck", "Stuck").ConfigureAwait(true));

        Assert.Contains("running", removal.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(path));
    }

    // ================================================================== undo

    /// <summary>No Undo is offered unless it could actually work.</summary>
    [Fact]
    public async Task UndoIsOnlyOfferedWhenTheInstanceCanBeBroughtBack()
    {
        MakeInstance("Doomed");

        var list = NewList();
        var removal = new InstanceRemoval(new ScriptedPrompts(true, true));

        await removal.RemoveAsync(list, "Doomed", "Doomed").ConfigureAwait(true);

        Assert.Equal(list.TrashedSomething, removal.CanUndo);
    }

    [Fact]
    public void UndoingWithNothingTrashedDoesNothing()
    {
        var removal = new InstanceRemoval();

        Assert.False(removal.Undo(NewList()));
    }
}
