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
 * Copying is what people do before they risk a world they care about, so the assertion that matters is
 * that the WORLDS ARRIVE. A copy that quietly produced an empty instance would be discovered at the
 * worst possible moment.
 *
 * InstanceCopyTask itself was tested in wave 7. What is checked here is the flow around it: the name
 * that was asked for, the group it lands in, what happens when the user cancels, and the id handed
 * back -- which the window uses to select the copy, and which is not the display name.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class InstanceDuplicationTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-dup-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public InstanceDuplicationTests()
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

    /// <summary>Answers the name prompt with whatever the test says.</summary>
    private sealed class ScriptedPrompts(string? answer) : IUserPrompts
    {
        public string? Offered { get; private set; }

        public string Message { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
            => Task.FromResult(false);

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
        {
            Offered = initialValue;
            Message = message;

            return Task.FromResult(answer);
        }
    }

    private void MakeInstance(string id, string worldContent = "irreplaceable")
    {
        var path = Path.Combine(_instances, id);
        var saves = Path.Combine(path, "minecraft", "saves", "My World");

        Directory.CreateDirectory(saves);
        Directory.CreateDirectory(Path.Combine(path, "minecraft", "mods"));

        File.WriteAllText(Path.Combine(path, "instance.cfg"), $"name={id}\nInstanceType=OneSix\n");
        File.WriteAllText(Path.Combine(saves, "level.dat"), worldContent);
        File.WriteAllText(Path.Combine(path, "minecraft", "mods", "a.jar"), "mod");
    }

    private InstanceList NewList()
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        return list;
    }

    // ================================================================== the copy itself

    /*
     * THE WORLDS AND THE MODS COME WITH IT. A Copy button that left them behind would be the opposite
     * of what it is for -- copying is precisely how people protect a world before experimenting on it.
     */
    [Fact]
    public async Task CopyingBringsTheWorldsAndModsAlong()
    {
        MakeInstance("Original");

        var list = NewList();

        var id = await new InstanceDuplication(new ScriptedPrompts("Experiment"))
            .CopyAsync(list, "Original", "Original").ConfigureAwait(true);

        Assert.NotEqual(string.Empty, id);

        var copied = Path.Combine(_instances, id);

        Assert.Equal(
            "irreplaceable",
            File.ReadAllText(Path.Combine(copied, "minecraft", "saves", "My World", "level.dat")));

        Assert.True(File.Exists(Path.Combine(copied, "minecraft", "mods", "a.jar")));
    }

    [Fact]
    public async Task TheCopyTakesTheNameThatWasAskedFor()
    {
        MakeInstance("Original");

        var list = NewList();

        var id = await new InstanceDuplication(new ScriptedPrompts("Experiment"))
            .CopyAsync(list, "Original", "Original").ConfigureAwait(true);

        NewList();
        list.LoadList();

        Assert.Equal("Experiment", list.GetInstanceById(id)?.Name);
    }

    /// <summary>The original is untouched — this is a copy, not a move.</summary>
    [Fact]
    public async Task TheOriginalSurvivesUnchanged()
    {
        MakeInstance("Original");

        var list = NewList();

        await new InstanceDuplication(new ScriptedPrompts("Experiment"))
            .CopyAsync(list, "Original", "Original").ConfigureAwait(true);

        Assert.Equal(
            "irreplaceable",
            File.ReadAllText(Path.Combine(_instances, "Original", "minecraft", "saves", "My World", "level.dat")));
    }

    /// <summary>The copy lands beside the original rather than loose at the top of the list.</summary>
    [Fact]
    public async Task TheCopyJoinsTheOriginalsGroup()
    {
        MakeInstance("Original");

        var list = NewList();
        list.SetInstanceGroup("Original", "Modded");

        var id = await new InstanceDuplication(new ScriptedPrompts("Experiment"))
            .CopyAsync(list, "Original", "Original").ConfigureAwait(true);

        Assert.Equal("Modded", list.GetInstanceGroup(id));

        /*
         * RE-READ FROM DISK. The in-memory assertion above passed even while the group was being
         * filtered straight back out of instgroups.json: SaveGroupList skips ids it does not know as
         * instances, and a freshly committed one was not registered yet. The copy landed ungrouped for
         * anybody who restarted the launcher, and this test said it had not.
         *
         * Same lesson as the settings page: for anything whose output is a file, assert on the file.
         */
        Assert.Equal("Modded", NewList().GetInstanceGroup(id));
    }

    /*
     * THE ID IS NOT THE NAME. When something already occupies the directory, DirNameFromString appends
     * "(1)" -- and the window uses the returned id to select the copy, so a guess from the name would
     * select the wrong instance.
     */
    [Fact]
    public async Task CopyingToANameAlreadyTakenReturnsTheRealId()
    {
        MakeInstance("Original");
        MakeInstance("Experiment");

        var list = NewList();

        var id = await new InstanceDuplication(new ScriptedPrompts("Experiment"))
            .CopyAsync(list, "Original", "Original").ConfigureAwait(true);

        Assert.NotEqual("Experiment", id);
        Assert.NotEqual(string.Empty, id);
        Assert.True(Directory.Exists(Path.Combine(_instances, id)));

        // And the instance that already had that directory is untouched.
        Assert.True(File.Exists(Path.Combine(_instances, "Experiment", "instance.cfg")));
    }

    // ================================================================== not copying

    [Fact]
    public async Task CancellingTheNamePromptCopiesNothing()
    {
        MakeInstance("Original");

        var list = NewList();

        Assert.Equal(
            string.Empty,
            await new InstanceDuplication(new ScriptedPrompts(null)).CopyAsync(list, "Original", "Original")
                .ConfigureAwait(true));

        Assert.Single(Directory.GetDirectories(_instances));
    }

    /*
     * EMPTY IS NOT THE SAME AS CANCELLED. Cancelling is silent; an empty name is something the user
     * typed, and worth telling them about rather than appearing to do nothing.
     */
    [Fact]
    public async Task AnEmptyNameIsRefusedWithAnExplanation()
    {
        MakeInstance("Original");

        var duplication = new InstanceDuplication(new ScriptedPrompts("   "));

        Assert.Equal(string.Empty, await duplication.CopyAsync(NewList(), "Original", "Original").ConfigureAwait(true));
        Assert.Contains("name", duplication.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A build with no dialogs cannot ask for a name, so it must not invent one.</summary>
    [Fact]
    public async Task WithNoPromptsWiredUpNothingIsCopied()
    {
        MakeInstance("Original");

        Assert.Equal(
            string.Empty,
            await new InstanceDuplication().CopyAsync(NewList(), "Original", "Original").ConfigureAwait(true));

        Assert.Single(Directory.GetDirectories(_instances));
    }

    [Fact]
    public async Task CopyingSomethingThatIsNotThereAsksNothing()
    {
        var prompts = new ScriptedPrompts("Anything");

        Assert.Equal(
            string.Empty,
            await new InstanceDuplication(prompts).CopyAsync(NewList(), "NeverExisted", "?").ConfigureAwait(true));

        Assert.Null(prompts.Offered);
    }

    // ================================================================== what the prompt says

    /// <summary>Prefilled with the original name, as upstream's copy dialog is.</summary>
    [Fact]
    public async Task TheNamePromptStartsFromTheOriginalName()
    {
        MakeInstance("Original");

        var prompts = new ScriptedPrompts(null);

        await new InstanceDuplication(prompts).CopyAsync(NewList(), "Original", "My Nice Pack").ConfigureAwait(true);

        Assert.Equal("My Nice Pack", prompts.Offered);
        Assert.Contains("My Nice Pack", prompts.Message, StringComparison.Ordinal);
    }

    // ================================================================== the copy dialog

    /// <summary>Answers the copy dialog with a fixed choice, or null to cancel.</summary>
    private sealed class StubCopyPrompt(InstanceCopyChoice? choice) : IInstanceCopyPrompt
    {
        public string? Offered { get; private set; }

        public Task<InstanceCopyChoice?> AskAsync(string sourceName)
        {
            Offered = sourceName;

            return Task.FromResult(choice);
        }
    }

    /*
     * THE DIALOG'S OPTIONS ARE HONOURED. This is the whole point of the wave: unchecking "Mods" has to
     * actually leave the mods behind, which proves the prefs reach InstanceCopyTask rather than being
     * built with defaults as they were before.
     */
    [Fact]
    public async Task TheCopyDialogsOptionsDecideWhatIsCopied()
    {
        MakeInstance("Original");

        var choice = new InstanceCopyChoice("Slim", new InstanceCopyPrefs { CopyMods = false });

        var id = await new InstanceDuplication(copyPrompt: new StubCopyPrompt(choice))
            .CopyAsync(NewList(), "Original", "Original").ConfigureAwait(true);

        Assert.NotEqual(string.Empty, id);

        var copied = Path.Combine(_instances, id);

        // The world came across; the mods did not, because the dialog said not to.
        Assert.True(File.Exists(Path.Combine(copied, "minecraft", "saves", "My World", "level.dat")));
        Assert.False(File.Exists(Path.Combine(copied, "minecraft", "mods", "a.jar")));
    }

    [Fact]
    public async Task TheCopyDialogPrefillsTheSourceName()
    {
        MakeInstance("Original");

        var prompt = new StubCopyPrompt(null);

        await new InstanceDuplication(copyPrompt: prompt).CopyAsync(NewList(), "Original", "My Pack").ConfigureAwait(true);

        Assert.Equal("My Pack", prompt.Offered);
    }

    [Fact]
    public async Task CancellingTheCopyDialogCopiesNothing()
    {
        MakeInstance("Original");

        var id = await new InstanceDuplication(copyPrompt: new StubCopyPrompt(null))
            .CopyAsync(NewList(), "Original", "Original").ConfigureAwait(true);

        Assert.Equal(string.Empty, id);
        Assert.Single(Directory.GetDirectories(_instances));
    }

    // ================================================================== progress

    /*
     * REPORTED THROUGH THE UI-THREAD BATCHER, not straight onto the bound properties. The copy runs off
     * the UI thread, so InstanceCopyTask's events arrive on whatever thread is moving files -- writing
     * to a bound property from there is the crash BatchingProgressReporter exists to prevent.
     *
     * Checked by counting: every callback must arrive through the post delegate, and none outside it.
     */
    [Fact]
    public async Task ProgressArrivesThroughThePostedDelegateOnly()
    {
        MakeInstance("Original");

        var posted = 0;
        var outside = 0;
        var inside = false;

        var duplication = new InstanceDuplication(
            new ScriptedPrompts("Experiment"),
            action =>
            {
                posted++;
                inside = true;

                try
                {
                    action();
                }
                finally
                {
                    inside = false;
                }
            });

        duplication.PropertyChanged += (_, e) =>
        {
            /*
             * Only VALUES have to come through the post. The flow's own bookkeeping -- clearing the bar
             * before and after -- runs on the calling thread, which is the UI thread in the app, and is
             * as safe as setting IsBusy there. What must never arrive off-thread is a figure the copy
             * task reported while it was moving files.
             */
            if (e.PropertyName == nameof(InstanceDuplication.Progress)
                && duplication.Progress is not null
                && !inside)
            {
                outside++;
            }
        };

        var id = await duplication.CopyAsync(NewList(), "Original", "Original").ConfigureAwait(true);

        Assert.NotEqual(string.Empty, id);
        Assert.True(posted > 0, "the copy reported nothing at all");
        Assert.Equal(0, outside);
    }

    /// <summary>The bar is cleared when the copy ends, whatever the last progress said.</summary>
    [Fact]
    public async Task ProgressIsClearedWhenTheCopyFinishes()
    {
        MakeInstance("Original");

        var duplication = new InstanceDuplication(new ScriptedPrompts("Experiment"));

        await duplication.CopyAsync(NewList(), "Original", "Original").ConfigureAwait(true);

        Assert.False(duplication.IsBusy);
        Assert.Null(duplication.Progress);
    }

    /// <summary>The status ends up describing the result rather than the last thing the task said.</summary>
    [Fact]
    public async Task TheFinalStatusDescribesTheResult()
    {
        MakeInstance("Original");

        var duplication = new InstanceDuplication(new ScriptedPrompts("Experiment"));

        await duplication.CopyAsync(NewList(), "Original", "My Copy").ConfigureAwait(true);

        Assert.Contains("My Copy", duplication.Status, StringComparison.Ordinal);
    }
}
