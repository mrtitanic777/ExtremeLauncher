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
 * A WORLD IS THE ONLY THING IN AN INSTANCE THAT EXISTS NOWHERE ELSE, so the test that matters most
 * here is the same one as for instance deletion: that "no" means no, and the save is still on disk
 * afterwards.
 *
 * The worlds are built with real NBT level.dat files, because everything this page shows is read out
 * of them -- a fixture with a fake level.dat would test the fixture.
 */

using System.IO.Compression;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class WorldsPageViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-worlds-" + Guid.NewGuid().ToString("N"));

    private readonly string _saves;

    public WorldsPageViewModelTests()
    {
        _saves = Path.Combine(_temp, "saves");
        Directory.CreateDirectory(_saves);
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

    /// <summary>A real world: a folder with a gzipped NBT level.dat naming it.</summary>
    /// <remarks>Built the same way WorldTests builds one, against the real NBT writer.</remarks>
    private string MakeWorld(string folder, string name, long lastPlayed = 1_700_000_000_000, long? seed = null)
    {
        var path = Path.Combine(_saves, folder);

        Directory.CreateDirectory(path);

        var data = new NbtTag { Type = NbtTagType.Compound, Name = "Data" };

        data.Put("LevelName", name);
        data.Children.Add(new NbtTag { Type = NbtTagType.Long, Name = "LastPlayed", Value = lastPlayed });
        data.Children.Add(new NbtTag { Type = NbtTagType.Int, Name = "GameType", Value = 0 });

        if (seed is { } value)
        {
            // The modern location: Data/WorldGenSettings/seed. World.Load tries this first.
            var worldGen = new NbtTag { Type = NbtTagType.Compound, Name = "WorldGenSettings" };
            worldGen.Children.Add(new NbtTag { Type = NbtTagType.Long, Name = "seed", Value = value });
            data.Children.Add(worldGen);
        }

        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Children.Add(data);

        File.WriteAllBytes(Path.Combine(path, "level.dat"), Nbt.WriteCompressed(root));

        return path;
    }

    /// <summary>A folder that looks like a world and is not.</summary>
    private string MakeBrokenWorld(string folder)
    {
        var path = Path.Combine(_saves, folder);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "level.dat"), "not nbt at all");

        return path;
    }

    private sealed class ScriptedPrompts(bool confirm = false, string? text = null) : IUserPrompts
    {
        public int Confirmations { get; private set; }

        public string LastMessage { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        {
            Confirmations++;
            LastMessage = message;

            return Task.FromResult(confirm);
        }

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult(text);
    }

    /// <summary>A clipboard that remembers the last thing copied. Present=false is "no clipboard".</summary>
    private sealed class StubClipboard(bool present = true) : IClipboard
    {
        public string? LastCopied { get; private set; }

        public Task<bool> SetTextAsync(string text)
        {
            if (!present)
            {
                return Task.FromResult(false);
            }

            LastCopied = text;

            return Task.FromResult(true);
        }
    }

    private WorldsPageViewModel Load(IUserPrompts? prompts = null, IClipboard? clipboard = null)
    {
        var page = new WorldsPageViewModel(prompts, clipboard: clipboard);

        page.Load(_temp);

        return page;
    }

    /// <summary>A picker that hands back a fixed path -- or an empty string, meaning "cancelled".</summary>
    private sealed class StubWorldPicker(string path) : IWorldFilePicker
    {
        public Task<string> PickAsync() => Task.FromResult(path);
    }

    /// <summary>A world packed into a zip, the way the Add button receives one.</summary>
    private string MakeWorldZip(string zipName, string levelName)
    {
        var data = new NbtTag { Type = NbtTagType.Compound, Name = "Data" };
        data.Put("LevelName", levelName);

        var root = new NbtTag { Type = NbtTagType.Compound };
        root.Children.Add(data);

        var zipPath = Path.Combine(_temp, zipName);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry("level.dat").Open();
        stream.Write(Nbt.WriteCompressed(root));

        return zipPath;
    }

    // ================================================================== what it shows

    [Fact]
    public void WorldsAreListedByTheirNameFromLevelDat()
    {
        MakeWorld("world-folder", "My Survival World");

        var page = Load();

        var world = Assert.Single(page.Worlds);

        // The NAME, not the folder: renaming a world does not move its folder, so the two diverge.
        Assert.Equal("My Survival World", world.Name);
        Assert.Equal("world-folder", world.FolderName);
        Assert.True(world.IsValid);
    }

    /*
     * A SAVE THAT WILL NOT PARSE IS STILL LISTED, named after its folder. Hiding it is
     * indistinguishable from having deleted it, and this is exactly the row someone came here to find.
     */
    [Fact]
    public void ABrokenWorldIsListedUnderItsFolderName()
    {
        MakeBrokenWorld("corrupted");

        var page = Load();

        var world = Assert.Single(page.Worlds);

        Assert.Equal("corrupted", world.Name);
        Assert.False(world.IsValid);
        Assert.Equal(string.Empty, world.LastPlayedString);
    }

    /// <summary>Most recently played first, with unreadable saves at the end but still visible.</summary>
    [Fact]
    public void WorldsAreOrderedByWhenTheyWereLastPlayed()
    {
        MakeWorld("old", "Old World", lastPlayed: 1_600_000_000_000);
        MakeWorld("recent", "Recent World", lastPlayed: 1_700_000_000_000);
        MakeBrokenWorld("broken");

        var page = Load();

        Assert.Equal(["Recent World", "Old World", "broken"], page.Worlds.Select(w => w.Name));
    }

    [Fact]
    public void AnInstanceWithNoSavesListsNothing()
    {
        var page = Load();

        Assert.Empty(page.Worlds);
        Assert.False(page.HasSelection);
    }

    // ================================================================== deleting

    /*
     * THE TEST THAT MATTERS. Everything else on this page is a message; this is whether a person's
     * "no" is honoured when the alternative is a save they have played for two years.
     */
    [Fact]
    public async Task DecliningLeavesTheWorldOnDisk()
    {
        var path = MakeWorld("world-folder", "My Survival World");

        var page = Load(new ScriptedPrompts(confirm: false));

        page.Select(page.Worlds[0].Path);
        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.True(Directory.Exists(path));
        Assert.Single(page.Worlds);
    }

    /// <summary>A build with no dialogs cannot ask, so it must not delete.</summary>
    [Fact]
    public async Task WithNoPromptsWiredUpNothingIsDeleted()
    {
        var path = MakeWorld("world-folder", "My Survival World");

        var page = Load();

        page.Select(page.Worlds[0].Path);
        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task AgreeingDeletesTheWorld()
    {
        var path = MakeWorld("world-folder", "My Survival World");
        MakeWorld("keep", "Keep This");

        var page = Load(new ScriptedPrompts(confirm: true));

        page.Select(page.Worlds.Single(w => w.Name == "My Survival World").Path);
        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.False(Directory.Exists(path));
        Assert.Equal(["Keep This"], page.Worlds.Select(w => w.Name));
    }

    /// <summary>The question names the world and says where it is going.</summary>
    [Fact]
    public async Task TheQuestionNamesTheWorld()
    {
        MakeWorld("world-folder", "My Survival World");

        var prompts = new ScriptedPrompts(confirm: false);
        var page = Load(prompts);

        page.Select(page.Worlds[0].Path);
        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.Equal(1, prompts.Confirmations);
        Assert.Contains("My Survival World", prompts.LastMessage, StringComparison.Ordinal);
        Assert.Contains(InstanceRemoval.TrashName, prompts.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingWithNothingSelectedAsksNothing()
    {
        MakeWorld("world-folder", "My Survival World");

        var prompts = new ScriptedPrompts(confirm: true);
        var page = Load(prompts);

        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.Equal(0, prompts.Confirmations);
        Assert.Single(page.Worlds);
    }

    /*
     * A BROKEN SAVE CAN STILL BE DELETED -- upstream's cannot. WorldListPage enables Remove whenever a
     * row is selected while World::destroy refuses an invalid world, so pressing it does nothing at
     * all, with no message (upstream bug #20). Tidying up a corrupted save is the main reason to come
     * to this page.
     *
     * An earlier version of this test asserted `Directory.Exists(path) || page.Worlds.Count == 0`,
     * which is true whatever happens. It asserted nothing, and hid this entirely.
     */
    [Fact]
    public async Task ABrokenWorldCanStillBeDeleted()
    {
        var path = MakeBrokenWorld("corrupted");

        var page = Load(new ScriptedPrompts(confirm: true));

        page.Select(page.Worlds[0].Path);
        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.False(Directory.Exists(path));
        Assert.Empty(page.Worlds);
    }

    // ================================================================== renaming

    [Fact]
    public async Task RenamingChangesTheNameTheWorldReportsForItself()
    {
        MakeWorld("world-folder", "Old Name");

        var page = Load(new ScriptedPrompts(text: "New Name"));

        page.Select(page.Worlds[0].Path);
        await page.RenameSelectedAsync().ConfigureAwait(true);

        Assert.Equal("New Name", Assert.Single(page.Worlds).Name);

        // Read back from disk entirely: what is in level.dat is what the game will show.
        Assert.Equal("New Name", World.Load(page.Worlds[0].Path).ActualName);
    }

    [Fact]
    public async Task CancellingARenameChangesNothing()
    {
        MakeWorld("world-folder", "Old Name");

        var page = Load(new ScriptedPrompts(text: null));

        page.Select(page.Worlds[0].Path);
        await page.RenameSelectedAsync().ConfigureAwait(true);

        Assert.Equal("Old Name", Assert.Single(page.Worlds).Name);
    }

    [Fact]
    public async Task AnEmptyNameIsRefusedWithAnExplanation()
    {
        MakeWorld("world-folder", "Old Name");

        var page = Load(new ScriptedPrompts(text: "   "));

        page.Select(page.Worlds[0].Path);
        await page.RenameSelectedAsync().ConfigureAwait(true);

        Assert.Equal("Old Name", Assert.Single(page.Worlds).Name);
        Assert.Contains("name", page.Status, StringComparison.OrdinalIgnoreCase);
    }

    /*
     * A WORLD THAT WILL NOT PARSE CANNOT BE RENAMED: the name lives inside level.dat, so renaming means
     * rewriting a file the launcher has just failed to read. Deleting it is still allowed.
     */
    [Fact]
    public void ABrokenWorldCannotBeRenamed()
    {
        MakeBrokenWorld("corrupted");

        var page = Load();

        page.Select(page.Worlds[0].Path);

        Assert.True(page.HasSelection);
        Assert.False(page.CanRename);
    }

    // ================================================================== the page contract

    [Fact]
    public void ThePageNeverHasAnythingToSave()
    {
        MakeWorld("world-folder", "My Survival World");

        var page = Load();

        Assert.False(page.HasUnsavedChanges);
        Assert.True(page.Save());
    }

    // ---------------------------------------------------------------- copying a world

    private WorldsPageViewModel Viewing(IUserPrompts prompts)
    {
        var page = new WorldsPageViewModel(prompts);

        page.Load(_temp);

        return page;
    }

    [Fact]
    public async Task CopyingAWorldMakesASecondOneWithTheNewNameInsideIt()
    {
        /*
         * THE WHOLE POINT. Minecraft shows the name out of level.dat and never shows the folder, so a
         * copy that keeps the old name inside it gives somebody two identical-looking worlds and no
         * way to tell which is which -- which is exactly the confusion people copy folders by hand
         * and then run into.
         */
        MakeWorld("world1", "My World");

        var page = Viewing(new ScriptedPrompts(text: "Backup"));

        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        Assert.True(page.CanCopy);

        await page.CopySelectedAsync();

        Assert.Equal(2, page.Worlds.Count);
        Assert.Contains(page.Worlds, w => w.Name == "My World");
        Assert.Contains(page.Worlds, w => w.Name == "Backup");
    }

    [Fact]
    public async Task TheCopyIsARealCopyOfTheFilesRatherThanJustAnEntry()
    {
        // A world is its region files, not its level.dat. A "copy" that only wrote a new level.dat
        // would look right in the list and be an empty world in the game.
        var source = MakeWorld("world1", "My World");

        Directory.CreateDirectory(Path.Combine(source, "region"));
        File.WriteAllText(Path.Combine(source, "region", "r.0.0.mca"), "chunk data");

        var page = Viewing(new ScriptedPrompts(text: "Backup"));

        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        var copy = page.Worlds.Single(w => w.Name == "Backup");

        Assert.Equal("chunk data", File.ReadAllText(Path.Combine(copy.Path, "region", "r.0.0.mca")));
    }

    [Fact]
    public async Task TheOriginalIsUntouched()
    {
        var source = MakeWorld("world1", "My World");

        var page = Viewing(new ScriptedPrompts(text: "Backup"));

        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.True(Directory.Exists(source));

        // Still there, still called what it was called: the copy is a second world, not a move.
        Assert.Contains(page.Worlds, w => w.FolderName == "world1" && w.Name == "My World");
    }

    [Fact]
    public async Task TheCopyLandsInItsOwnFolderRatherThanOnTopOfTheOriginal()
    {
        /*
         * The folder name is derived from the world's name and deduped -- a world's name can contain
         * characters a folder cannot, so it is never used as typed. Without the dedupe the copy would
         * be written straight over the world it came from.
         */
        var source = MakeWorld("world1", "My World");

        var page = Viewing(new ScriptedPrompts(text: "Backup"));

        var original = page.Worlds.Single(w => w.Name == "My World").Path;

        page.Select(original);

        await page.CopySelectedAsync();

        var copy = page.Worlds.Single(w => w.Name == "Backup");

        Assert.NotEqual(original, copy.Path);
    }

    [Fact]
    public async Task CopyingWithTheSameNameIsAllowedAndLeavesTwo()
    {
        // Somebody who just wants a backup and does not care what it is called. Both worlds keep the
        // name; they are in different folders, which is the best that can be done.
        var source = MakeWorld("world1", "My World");

        var page = Viewing(new ScriptedPrompts(text: "My World"));

        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.Equal(2, page.Worlds.Count(w => w.Name == "My World"));
    }

    [Fact]
    public async Task CancellingTheNamePromptCopiesNothing()
    {
        var source = MakeWorld("world1", "My World");

        // A null answer is a cancel; empty is a name the user actually typed.
        var page = Viewing(new ScriptedPrompts(text: null));

        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.Single(page.Worlds);
    }

    [Fact]
    public async Task AnEmptyNameIsRefusedRatherThanUsed()
    {
        var source = MakeWorld("world1", "My World");

        var page = Viewing(new ScriptedPrompts(text: "   "));

        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.Single(page.Worlds);
        Assert.Contains("needs a name", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorldThatWillNotParseCannotBeCopied()
    {
        /*
         * Unlike delete, which works on anything. Copying one would produce a second copy of
         * something already broken, and there is no name to write into it.
         */
        var broken = MakeBrokenWorld("broken");

        var page = Viewing(new ScriptedPrompts(text: "Backup"));

        page.Select(page.Worlds.Single(w => w.Name == "broken").Path);

        Assert.False(page.CanCopy);

        await page.CopySelectedAsync();

        Assert.Single(page.Worlds);
    }

    [Fact]
    public void NothingSelectedMeansNothingToCopy()
    {
        MakeWorld("world1", "My World");

        Assert.False(Viewing(new ScriptedPrompts()).CanCopy);
    }

    // ------------------------------------------- touching a world while the game is running

    [Fact]
    public async Task NothingIsTouchedWhileTheGameRunsUnlessSomebodyInsists()
    {
        /*
         * UPSTREAM'S worldSafetyNagQuestion, and a nag rather than a lock on purpose. The servers
         * page LOCKS because the game rewrites servers.dat on exit and would silently undo any edit.
         * A world is different: the game holds it open and writes to it, so the risk is real but a
         * world idling at the title screen is fine -- and the launcher cannot tell which from here.
         */
        MakeWorld("world1", "My World");

        var prompts = new ScriptedPrompts(confirm: false, text: "Backup");

        var page = Viewing(prompts);

        page.IsLocked = true;
        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.Single(page.Worlds);
        Assert.Contains("potentially unsafe", prompts.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SayingYesToTheNagGoesAhead()
    {
        // It is a warning, not a refusal: somebody who knows their world is idle has a real reason.
        MakeWorld("world1", "My World");

        var page = Viewing(new ScriptedPrompts(confirm: true, text: "Backup"));

        page.IsLocked = true;
        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.Contains(page.Worlds, w => w.Name == "Backup");
    }

    [Fact]
    public async Task RenamingAndDeletingAskTheSameQuestion()
    {
        // All three actions that touch a world go through the same nag, as upstream's do.
        MakeWorld("world1", "My World");

        var renamePrompts = new ScriptedPrompts(confirm: false, text: "Renamed");
        var renaming = Viewing(renamePrompts);

        renaming.IsLocked = true;
        renaming.Select(renaming.Worlds.Single(w => w.Name == "My World").Path);

        await renaming.RenameSelectedAsync();

        Assert.Contains("potentially unsafe", renamePrompts.LastMessage, StringComparison.Ordinal);
        Assert.Contains(renaming.Worlds, w => w.Name == "My World");

        var deletePrompts = new ScriptedPrompts(confirm: false);
        var deleting = Viewing(deletePrompts);

        deleting.IsLocked = true;
        deleting.Select(deleting.Worlds.Single(w => w.Name == "My World").Path);

        await deleting.DeleteSelectedAsync();

        Assert.Contains("potentially unsafe", deletePrompts.LastMessage, StringComparison.Ordinal);
        Assert.Single(deleting.Worlds);
    }

    [Fact]
    public async Task WithTheGameStoppedNothingIsAsked()
    {
        // The ordinary case, and it must not grow an extra click.
        MakeWorld("world1", "My World");

        var prompts = new ScriptedPrompts(confirm: true, text: "Backup");

        var page = Viewing(prompts);

        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.Equal(0, prompts.Confirmations);
        Assert.Contains(page.Worlds, w => w.Name == "Backup");
    }

    // ------------------------------------------- a copy that did not finish

    /// <summary>Runs the copy for real, then reports it as not finished.</summary>
    /// <remarks>
    /// IT HAS TO ACTUALLY RUN THE TASK. My first version of this returned false without running
    /// anything, so no partial folder ever existed and the cleanup test passed with the cleanup
    /// DELETED -- a test that asserted nothing, in exactly the shape this port keeps relearning.
    ///
    /// Running it and then reporting false is what a cancellation looks like from here: files have
    /// been written, and the caller is told the copy did not finish.
    /// </remarks>
    private sealed class RefusingRunner : ITaskRunner
    {
        public string? Title { get; private set; }

        public async Task<bool> RunAsync(IRunnableTask task, string title)
        {
            Title = title;

            await task.RunAsync(CancellationToken.None);

            return false;
        }
    }

    [Fact]
    public async Task AHalfFinishedCopyIsCleanedUpRatherThanLeftInTheList()
    {
        /*
         * A HALF-COPIED WORLD IS WORSE THAN NO COPY. The folder looks like a world, appears in this
         * list AND in the game's, and is missing region files -- so it loads as a broken or empty
         * world, which is a far more confusing outcome than the copy simply not happening.
         */
        MakeWorld("world1", "My World");

        var runner = new RefusingRunner();

        var page = new WorldsPageViewModel(new ScriptedPrompts(text: "Backup"), runner);

        page.Load(_temp);
        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.Equal("Copying My World", runner.Title);

        // Asserted on the FOLDER as well as the list: a partial copy that the page merely does not
        // show is still one the game would find and try to load.
        Assert.Single(Directory.GetDirectories(_saves));
        Assert.Single(page.Worlds);
        Assert.Contains(page.Worlds, w => w.Name == "My World");
    }

    [Fact]
    public async Task WithNoRunnerTheCopyStillHappens()
    {
        // A headless caller, and the tests above. The page is never disabled for want of a window.
        MakeWorld("world1", "My World");

        var page = new WorldsPageViewModel(new ScriptedPrompts(text: "Backup"));

        page.Load(_temp);
        page.Select(page.Worlds.Single(w => w.Name == "My World").Path);

        await page.CopySelectedAsync();

        Assert.Contains(page.Worlds, w => w.Name == "Backup");
    }

    // ================================================================== copying the seed

    [Fact]
    public void TheSeedIsReadFromLevelDat()
    {
        MakeWorld("world-folder", "My World", seed: 123456789);

        var world = Assert.Single(Load().Worlds);

        Assert.Equal(123456789, world.Seed);
        Assert.Equal("123456789", world.SeedString);
    }

    [Fact]
    public async Task CopyingTheSeedPutsTheBareNumberOnTheClipboard()
    {
        MakeWorld("world-folder", "My World", seed: 123456789);

        var clipboard = new StubClipboard();
        var page = Load(clipboard: clipboard);
        page.Select(page.Worlds.Single().Path);

        await page.CopySeedSelectedAsync();

        Assert.Equal("123456789", clipboard.LastCopied);
        Assert.Contains("Copied the seed", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANegativeSeedIsCopiedWhole()
    {
        // Seeds are signed 64-bit; a fixture that only handled positive ones would miss half of them.
        MakeWorld("world-folder", "My World", seed: -8_000_000_000_000_000_000);

        var clipboard = new StubClipboard();
        var page = Load(clipboard: clipboard);
        page.Select(page.Worlds.Single().Path);

        await page.CopySeedSelectedAsync();

        Assert.Equal("-8000000000000000000", clipboard.LastCopied);
    }

    [Fact]
    public void ABrokenWorldsSeedCannotBeCopied()
    {
        // The seed lives in the level.dat the launcher just failed to read; there is nothing to copy.
        MakeBrokenWorld("corrupted");

        var page = Load(clipboard: new StubClipboard());
        page.Select(page.Worlds.Single().Path);

        Assert.False(page.CanCopySeed);
    }

    [Fact]
    public async Task WithNoClipboardTheSeedCopyReportsItPlainly()
    {
        MakeWorld("world-folder", "My World", seed: 42);

        // Default page: no clipboard wired, so the button works but copies nowhere.
        var page = Load();
        page.Select(page.Worlds.Single().Path);

        await page.CopySeedSelectedAsync();

        Assert.Contains("no clipboard", page.Status, StringComparison.Ordinal);
    }

    // ================================================================== importing a world

    [Fact]
    public void WithNoPickerAddIsDisabled()
    {
        // No picker wired means no way to choose a file, so the button is off rather than inert.
        Assert.False(Load().CanAdd);
    }

    [Fact]
    public async Task AddingImportsTheChosenWorldAndSelectsIt()
    {
        var zip = MakeWorldZip("upload.zip", "Imported World");

        var page = new WorldsPageViewModel(picker: new StubWorldPicker(zip));
        page.Load(_temp);

        await page.AddCommand.ExecuteAsync(null);

        var world = Assert.Single(page.Worlds);
        Assert.Equal("Imported World", world.Name);
        Assert.Same(world, page.Selected);
        Assert.Contains("Imported", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheAddPickerImportsNothing()
    {
        var page = new WorldsPageViewModel(picker: new StubWorldPicker(string.Empty));
        page.Load(_temp);

        await page.AddCommand.ExecuteAsync(null);

        Assert.Empty(page.Worlds);
    }

    // ================================================================== view folder

    private sealed class StubFolderOpener : IFolderOpener
    {
        public string? Opened { get; private set; }

        public Task OpenAsync(string path)
        {
            Opened = path;

            return Task.CompletedTask;
        }

        public Task OpenKnownAsync(string kind) => Task.CompletedTask;
    }

    [Fact]
    public void WithNoOpenerViewFolderIsDisabled()
    {
        Assert.False(Load().CanOpenFolder);
    }

    [Fact]
    public async Task ViewFolderOpensTheSavesFolder()
    {
        var opener = new StubFolderOpener();

        var page = new WorldsPageViewModel(folders: opener);
        page.Load(_temp);

        Assert.True(page.CanOpenFolder);

        await page.OpenFolderCommand.ExecuteAsync(null);

        // The saves folder, created if it was not there, is what opened.
        Assert.EndsWith("saves", opener.Opened!.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.True(Directory.Exists(page.FolderPath));
    }

    // ================================================================== datapacks folder

    [Fact]
    public void DatapacksIsDisabledWithoutAnOpener()
    {
        MakeWorld("world-folder", "My World");

        var page = Load();
        page.Select(page.Worlds.Single().Path);

        Assert.False(page.CanOpenDatapacks);
    }

    [Fact]
    public async Task DatapacksOpensTheSelectedWorldsOwnDatapacksFolder()
    {
        var worldPath = MakeWorld("world-folder", "My World");

        var opener = new StubFolderOpener();
        var page = new WorldsPageViewModel(folders: opener);
        page.Load(_temp);
        page.Select(page.Worlds.Single().Path);

        await page.OpenDatapacksSelectedCommand.ExecuteAsync(null);

        // Inside the world, not the instance: world-folder/datapacks, created if absent.
        var expected = Path.Combine(worldPath, "datapacks");
        Assert.Equal(expected.Replace('\\', '/'), opener.Opened!.Replace('\\', '/'));
        Assert.True(Directory.Exists(expected));
    }

    [Fact]
    public async Task DatapacksHeedsTheSafetyNagWhileTheGameRuns()
    {
        MakeWorld("world-folder", "My World");

        var opener = new StubFolderOpener();

        // Game running (locked) and the nag is declined: nothing is opened.
        var page = new WorldsPageViewModel(new ScriptedPrompts(confirm: false), folders: opener) { IsLocked = true };
        page.Load(_temp);
        page.Select(page.Worlds.Single().Path);

        await page.OpenDatapacksSelectedCommand.ExecuteAsync(null);

        Assert.Null(opener.Opened);
    }

    // ================================================================== resetting the icon

    [Fact]
    public void ResetIconIsDisabledForAWorldWithNoIcon()
    {
        MakeWorld("world-folder", "My World");

        var page = Load();
        page.Select(page.Worlds.Single().Path);

        Assert.False(page.CanResetIcon);
    }

    [Fact]
    public void ResetIconRemovesTheWorldsIcon()
    {
        var worldPath = MakeWorld("world-folder", "My World");
        File.WriteAllBytes(Path.Combine(worldPath, "icon.png"), [1, 2, 3]);

        var page = Load();
        page.Select(page.Worlds.Single().Path);

        Assert.True(page.CanResetIcon);

        page.ResetIconSelectedCommand.Execute(null);

        Assert.False(File.Exists(Path.Combine(worldPath, "icon.png")));
        Assert.Contains("Reset the icon", page.Status, StringComparison.Ordinal);

        // The row is re-read, so the button turns itself off once the icon is gone.
        page.Select(page.Worlds.Single().Path);
        Assert.False(page.CanResetIcon);
    }

    [Fact]
    public async Task AddingAFileThatIsNotAWorldReportsIt()
    {
        // A zip with no level.dat is not a world; the page says so and imports nothing.
        var notAWorld = Path.Combine(_temp, "notaworld.zip");
        using (var zip = ZipFile.Open(notAWorld, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("readme.txt").Open();
            stream.Write([1, 2, 3]);
        }

        var page = new WorldsPageViewModel(picker: new StubWorldPicker(notAWorld));
        page.Load(_temp);

        await page.AddCommand.ExecuteAsync(null);

        Assert.Empty(page.Worlds);
        Assert.Contains("Could not import", page.Status, StringComparison.Ordinal);
    }
}
