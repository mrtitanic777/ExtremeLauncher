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
 * THE CONFIRMATION DIALOG, ACTUALLY OPENED AND ACTUALLY ANSWERED.
 *
 * This is the dialog standing between a misclick and somebody's worlds, and until now nobody had seen
 * it. Its rules were argued for in comments and never executed: that Escape means no, that closing
 * means no, that Return activates Cancel rather than the destructive button, and that a dialog with no
 * owner answers no rather than throwing.
 *
 * Every one of those is a way to delete an instance the user did not agree to delete.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class DialogPromptsTests
{
    private static Window ShowOwner()
    {
        var owner = new Window { Width = 400, Height = 300 };

        owner.Show();

        Dispatcher.UIThread.RunJobs();

        return owner;
    }

    /// <summary>Runs the dispatcher until the dialog is up, and returns it.</summary>
    private static async Task<Window> WaitForDialogAsync(Window owner)
    {
        for (var i = 0; i < 50; i++)
        {
            Dispatcher.UIThread.RunJobs();

            if (owner.OwnedWindows.Count != 0)
            {
                var dialog = owner.OwnedWindows[0];

                dialog.UpdateLayout();

                return dialog;
            }

            await Task.Yield();
        }

        throw new InvalidOperationException("The dialog never opened.");
    }

    private static Button Button(Visual root, string content)
        => root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content);

    // ================================================================== it opens and says the right thing

    [AvaloniaFact]
    public async Task TheDialogShowsTheMessageAndBothAnswers()
    {
        var owner = ShowOwner();
        var prompts = new DialogPrompts(() => owner);

        var asked = prompts.ConfirmAsync("Delete instance", "Move “My Pack” to the Recycle Bin?", "Delete", true);

        var dialog = await WaitForDialogAsync(owner).ConfigureAwait(true);

        Assert.Equal("Delete instance", dialog.Title);

        Assert.Contains(
            dialog.GetLogicalDescendants().OfType<SelectableTextBlock>(),
            t => t.Text?.Contains("My Pack", StringComparison.Ordinal) ?? false);

        // Both answers are reachable; a dialog with only one is a dialog with no choice.
        Assert.NotNull(Button(dialog, "Delete"));
        Assert.NotNull(Button(dialog, "Cancel"));

        Button(dialog, "Cancel").Command?.Execute(null);
        dialog.Close();

        Assert.False(await asked.ConfigureAwait(true));
    }

    // ================================================================== every way of saying no

    /*
     * CLOSING THE WINDOW IS "NO". A dialog dismissed with the X has not been agreed to, and treating
     * it as a yes would delete an instance on a stray click of the wrong corner.
     */
    [AvaloniaFact]
    public async Task ClosingTheDialogMeansNo()
    {
        var owner = ShowOwner();
        var prompts = new DialogPrompts(() => owner);

        var asked = prompts.ConfirmAsync("Delete instance", "Delete it?", "Delete", true);

        var dialog = await WaitForDialogAsync(owner).ConfigureAwait(true);

        dialog.Close();

        Assert.False(await asked.ConfigureAwait(true));
    }

    [AvaloniaFact]
    public async Task EscapeMeansNo()
    {
        var owner = ShowOwner();
        var prompts = new DialogPrompts(() => owner);

        var asked = prompts.ConfirmAsync("Delete instance", "Delete it?", "Delete", true);

        var dialog = await WaitForDialogAsync(owner).ConfigureAwait(true);

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Dispatcher.UIThread.RunJobs();

        Assert.False(await asked.ConfigureAwait(true));
    }

    /*
     * NO OWNER MEANS NO. ShowDialog throws without a parent, and a confirmation that could not be shown
     * has not been given -- so it must answer no rather than propagate an exception into a delete.
     */
    [AvaloniaFact]
    public async Task WithNoOwnerWindowTheAnswerIsNo()
    {
        var prompts = new DialogPrompts(() => null);

        Assert.False(await prompts.ConfirmAsync("Delete", "Delete it?", "Delete", true).ConfigureAwait(true));
        Assert.Null(await prompts.PromptForTextAsync("Copy", "Name?", "Pack").ConfigureAwait(true));
    }

    // ================================================================== saying yes

    [AvaloniaFact]
    public async Task PressingTheNamedButtonMeansYes()
    {
        var owner = ShowOwner();
        var prompts = new DialogPrompts(() => owner);

        var asked = prompts.ConfirmAsync("Delete instance", "Delete it?", "Delete", true);

        var dialog = await WaitForDialogAsync(owner).ConfigureAwait(true);

        Button(dialog, "Delete").Focus();
        Button(dialog, "Delete").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Dispatcher.UIThread.RunJobs();

        Assert.True(await asked.ConfigureAwait(true));
    }

    /*
     * RETURN MUST NOT DELETE. Cancel is the default button and the focused one, so the destructive
     * answer is never the one a stray keypress reaches. This is the reason the confirm button is
     * coloured rather than made default.
     */
    [AvaloniaFact]
    public async Task TheDestructiveButtonIsNotTheDefault()
    {
        var owner = ShowOwner();
        var prompts = new DialogPrompts(() => owner);

        var asked = prompts.ConfirmAsync("Delete instance", "Delete it?", "Delete", true);

        var dialog = await WaitForDialogAsync(owner).ConfigureAwait(true);

        Assert.False(Button(dialog, "Delete").IsDefault);
        Assert.True(Button(dialog, "Cancel").IsDefault);

        dialog.Close();

        Assert.False(await asked.ConfigureAwait(true));
    }

    // ================================================================== the name prompt

    [AvaloniaFact]
    public async Task TheNamePromptStartsFromTheSuggestionAndReturnsWhatWasTyped()
    {
        var owner = ShowOwner();
        var prompts = new DialogPrompts(() => owner);

        var asked = prompts.PromptForTextAsync("Copy instance", "Name for the copy:", "My Pack");

        var dialog = await WaitForDialogAsync(owner).ConfigureAwait(true);

        var box = dialog.GetLogicalDescendants().OfType<TextBox>().Single();

        Assert.Equal("My Pack", box.Text);

        box.Text = "My Pack (experiment)";

        Button(dialog, "OK").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("My Pack (experiment)", await asked.ConfigureAwait(true));
    }

    /*
     * NULL FOR CANCELLED, not empty. Conflating them turns "I changed my mind" into "make one called
     * nothing", which is why InstanceDuplication treats the two differently.
     */
    [AvaloniaFact]
    public async Task CancellingTheNamePromptReturnsNull()
    {
        var owner = ShowOwner();
        var prompts = new DialogPrompts(() => owner);

        var asked = prompts.PromptForTextAsync("Copy instance", "Name for the copy:", "My Pack");

        var dialog = await WaitForDialogAsync(owner).ConfigureAwait(true);

        dialog.Close();

        Assert.Null(await asked.ConfigureAwait(true));
    }

    /// <summary>Here OK <em>is</em> the default: the affirmative answer creates rather than destroys.</summary>
    [AvaloniaFact]
    public async Task TheNamePromptsOkButtonIsTheDefault()
    {
        var owner = ShowOwner();
        var prompts = new DialogPrompts(() => owner);

        var asked = prompts.PromptForTextAsync("Copy instance", "Name:", "My Pack");

        var dialog = await WaitForDialogAsync(owner).ConfigureAwait(true);

        Assert.True(Button(dialog, "OK").IsDefault);

        dialog.Close();

        await asked.ConfigureAwait(true);
    }
}
