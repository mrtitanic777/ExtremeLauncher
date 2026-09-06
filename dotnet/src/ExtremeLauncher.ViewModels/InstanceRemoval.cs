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
 * DELETING AN INSTANCE IS THE ONLY THING THIS LAUNCHER DOES THAT CANNOT BE UNDONE BY DOING IT AGAIN.
 * An instance directory holds worlds that exist in exactly one place, so the rules below are about
 * making the destructive path narrow and the recoverable path the default.
 *
 * The order is: trash it if the platform can, and only ask about permanent deletion when trashing has
 * actually been TRIED AND FAILED. Asking "delete permanently?" up front trains people to click through
 * a dialog that occasionally means what it says.
 *
 * THE PROMPTS ARE AN INTERFACE, so this is testable. Every branch here ends in a message box upstream,
 * which is why upstream has no tests for any of it -- and the branch that matters most is the one
 * where the user says no.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

/// <summary>Questions the launcher has to ask a person before doing something irreversible.</summary>
public interface IUserPrompts
{
    /// <summary>Asks a yes/no question. False for "no" AND for a dialog that could not be shown.</summary>
    /// <param name="destructive">Whether the affirmative answer destroys data, so it can be styled as such.</param>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false);

    /// <summary>
    /// Asks for a line of text.
    /// </summary>
    /// <returns>
    /// What was entered, or NULL when the user cancelled or no dialog could be shown. Null and empty
    /// are different answers: empty is a name the user actually typed, and worth telling them about.
    /// </returns>
    Task<string?> PromptForTextAsync(string title, string message, string initialValue);
}

/// <summary>Putting text on the clipboard, which is a UI toolkit's job.</summary>
/// <remarks>
/// Separate from IUserPrompts because copying asks nobody anything -- and because a page that can copy
/// but must not delete should not have to be handed the ability to delete.
/// </remarks>
public interface IClipboard
{
    /// <summary>Copies text. False when there is no clipboard to copy to.</summary>
    Task<bool> SetTextAsync(string text);
}

/// <summary>A clipboard that copies nothing, for a build with no UI.</summary>
public sealed class NoClipboard : IClipboard
{
    public static readonly NoClipboard Instance = new();

    public Task<bool> SetTextAsync(string text) => Task.FromResult(false);
}

/// <summary>Prompts that answer "no" to everything.</summary>
/// <remarks>
/// The safe default for a build with no dialogs wired up: a launcher that cannot ask must not act. The
/// opposite default would delete instances without ever showing a prompt.
/// </remarks>
public sealed class RefusingPrompts : IUserPrompts
{
    public static readonly RefusingPrompts Instance = new();

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        => Task.FromResult(false);

    public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
        => Task.FromResult<string?>(null);
}

/// <summary>What happened to an instance the user asked to remove.</summary>
public enum RemovalOutcome
{
    /// <summary>The user said no, or was never asked because nothing was selected.</summary>
    Cancelled,

    /// <summary>Moved to the desktop trash, and recoverable from there.</summary>
    Trashed,

    /// <summary>Deleted permanently, because there was no trash and the user agreed to that.</summary>
    Deleted,

    /// <summary>Tried and could not: in use, read-only, or gone already.</summary>
    Failed,
}

public sealed partial class InstanceRemoval : ObservableObject
{
    private readonly IUserPrompts _prompts;

    public InstanceRemoval(IUserPrompts? prompts = null) => _prompts = prompts ?? RefusingPrompts.Instance;

    /// <summary>What the last removal did, for the window to report.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Whether the last removal left something that can be put back.</summary>
    [ObservableProperty]
    private bool _canUndo;

    /// <summary>
    /// Removes an instance, preferring the trash and asking before anything irreversible.
    /// </summary>
    public async Task<RemovalOutcome> RemoveAsync(InstanceList list, string id, string displayName)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (id.Length == 0 || list.GetInstanceById(id) is null)
        {
            return RemovalOutcome.Cancelled;
        }

        /*
         * THE NAME THE USER SEES IS IN THE QUESTION. "Delete this instance?" beside a grid of similar
         * tiles is a question about which one, and the wrong answer is unrecoverable.
         */
        var confirmed = await _prompts.ConfirmAsync(
            "Delete instance",
            $"Move “{displayName}” to the {TrashName}?\n\n"
            + "Its worlds, mods, screenshots and settings all go with it.",
            "Delete",
            destructive: true).ConfigureAwait(true);

        if (!confirmed)
        {
            return RemovalOutcome.Cancelled;
        }

        if (list.TrashInstance(id))
        {
            // False where the platform does not report where things went, which is what decides
            // whether an Undo button appears at all -- offering one that cannot work is worse than
            // offering none, since the Recycle Bin's own Restore is right there.
            CanUndo = list.TrashedSomething;

            Status = $"Moved “{displayName}” to the {TrashName}.";

            return RemovalOutcome.Trashed;
        }

        /*
         * ONLY NOW is permanent deletion raised, and only because trashing was tried and failed. The
         * second question is deliberately blunt: it is a different question from the first, and a user
         * who has already clicked once is owed the word "permanently".
         */
        var permanent = await _prompts.ConfirmAsync(
            "Delete permanently",
            $"“{displayName}” could not be moved to the {TrashName}.\n\n"
            + "Delete it permanently instead? This cannot be undone.",
            "Delete permanently",
            destructive: true).ConfigureAwait(true);

        if (!permanent)
        {
            Status = $"“{displayName}” was not deleted.";

            return RemovalOutcome.Cancelled;
        }

        if (!list.DeleteInstance(id))
        {
            /*
             * Both routes failed with the user having agreed to the worse one, which almost always
             * means the game is still running out of that directory. Said plainly, because "delete
             * failed" alone invites a second click that will fail the same way.
             */
            Status = $"Could not delete “{displayName}”. It may still be running, or in use.";

            return RemovalOutcome.Failed;
        }

        CanUndo = false;
        Status = $"Deleted “{displayName}” permanently.";

        return RemovalOutcome.Deleted;
    }

    /// <summary>Puts back the instance most recently moved to the trash.</summary>
    public bool Undo(InstanceList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!list.TrashedSomething)
        {
            return false;
        }

        var restored = list.UndoTrashInstance();

        CanUndo = list.TrashedSomething;

        /*
         * The name it came back UNDER, which is not always the name it left with -- something else may
         * have taken the old one meanwhile. Telling the user the old name would send them looking for
         * a tile that is not there.
         */
        Status = restored.Length != 0 ? $"Restored “{restored}”." : "Nothing to restore.";

        return restored.Length != 0;
    }

    /// <summary>What this desktop calls the place deleted things go.</summary>
    /// <remarks>
    /// Worth getting right rather than saying "trash" everywhere: a Windows user looking for the
    /// "trash" will not find it, and the whole point of the message is that the instance is
    /// recoverable from somewhere they can name.
    /// </remarks>
    public static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";
}
