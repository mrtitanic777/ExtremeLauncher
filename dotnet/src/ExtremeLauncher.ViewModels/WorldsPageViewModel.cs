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
 * Ported from ui/pages/instance/WorldListPage.cpp, less its Qt model.
 *
 * A WORLD IS THE ONLY THING IN AN INSTANCE THAT EXISTS NOWHERE ELSE. A mod is a download and an
 * instance can be rebuilt; a world someone has played for two years cannot. That single fact decides
 * everything different about this page:
 *
 *   - Deleting ASKS, where the mods page does not.
 *   - World::destroy trashes first and only deletes outright where there is no trash -- upstream makes
 *     that distinction here and not for mods, and it is exactly the right place to make it.
 *   - An INVALID world is listed rather than hidden, and CAN be deleted -- upstream's cannot; see
 *     upstream bug #20. A save folder that will not parse is precisely what someone is looking for
 *     when they come here, and hiding it is indistinguishable from having deleted it.
 *
 * Upstream's confirmation says the world "may be gone forever (A LONG TIME)" and defaults to No. The
 * wording here is plainer but the default is the same, and for the same reason.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.ViewModels;

/// <summary>One row: a world in the instance's saves folder.</summary>
public sealed partial class WorldViewModel : ObservableObject
{
    public required string Path { get; init; }

    /// <summary>The world's own name from level.dat, or its folder name when that will not read.</summary>
    public required string Name { get; init; }

    /// <summary>The folder on disk, which is not always the name — renaming does not move it.</summary>
    public required string FolderName { get; init; }

    public string GameType { get; init; } = string.Empty;

    public DateTimeOffset LastPlayed { get; init; }

    /// <summary>The world's seed, from level.dat. Zero when the world declares none — which is
    /// indistinguishable from the valid seed zero, exactly as upstream leaves it.</summary>
    public long Seed { get; init; }

    /// <summary>The seed as it goes to the clipboard: the number, nothing else.</summary>
    public string SeedString => Seed.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether level.dat could be read at all.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Whether the world has a saved icon.png that Reset Icon could remove.</summary>
    public bool HasIcon { get; init; }

    /// <summary>The last-played time, or empty when the world will not parse.</summary>
    public string LastPlayedString => IsValid && LastPlayed != default
        ? LastPlayed.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)
        : string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Asks the desktop for a world zip to import. Implemented by the app.</summary>
public interface IWorldFilePicker
{
    /// <returns>The chosen file's path, or an empty string when the picker was cancelled.</returns>
    Task<string> PickAsync();
}

public sealed partial class WorldsPageViewModel : ObservableObject, IInstancePage, ILocksWhileRunning
{
    /// <summary>Set by the window while the game is running.</summary>
    /// <remarks>
    /// A NAG, NOT A LOCK, which is upstream's choice and the better one here. The servers page locks
    /// because the game REWRITES servers.dat when it exits and would silently undo any edit. A world
    /// is different: the game holds it open and writes to it, so changing one is unsafe rather than
    /// futile -- and somebody who knows their world is idle in a menu has a legitimate reason to go
    /// ahead.
    /// </remarks>
    [ObservableProperty]
    private bool _isLocked;

    private readonly IUserPrompts _prompts;

    private string _savesFolder = string.Empty;

    private readonly ITaskRunner? _runner;

    private readonly IClipboard _clipboard;

    private readonly IWorldFilePicker? _picker;

    private readonly IFolderOpener? _folders;

    /// <summary>Deletes a folder that should not have survived, quietly.</summary>
    private static void TryRemove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to say: the copy already failed, and this was the tidying up.
        }
    }

    /// <summary>Lets a CopyTask be shown in the progress window.</summary>
    private sealed class CopyTaskAdapter(CopyTask task) : IRunnableTask
    {
        public event EventHandler<string>? StatusChanged;

        public event EventHandler<(long Current, long Total)>? ProgressChanged;

        public bool CanAbort => task.CanAbort;

        public string FailReason => task.FailReason;

        public Task<bool> RunAsync(CancellationToken cancellationToken)
        {
            task.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
            task.ProgressChanged += (_, p) => ProgressChanged?.Invoke(this, (p.Current, p.Total));

            return task.RunAsync(cancellationToken);
        }
    }

    /// <param name="runner">
    /// Runs a long copy where the user can see it. Null in a headless caller and in the tests, where
    /// the copy simply runs inline -- the page is never disabled for want of a window.
    /// </param>
    /// <param name="clipboard">
    /// Where "Copy seed" writes. Null gives the no-op clipboard, which leaves the button working but
    /// copying nowhere -- the same treatment the other pages give a missing clipboard.
    /// </param>
    /// <param name="picker">
    /// Chooses a world zip to import. Null leaves the Add button disabled rather than present and
    /// inert -- a headless caller and the tests that do not exercise import pass nothing.
    /// </param>
    public WorldsPageViewModel(
        IUserPrompts? prompts = null,
        ITaskRunner? runner = null,
        IClipboard? clipboard = null,
        IWorldFilePicker? picker = null,
        IFolderOpener? folders = null)
    {
        _prompts = prompts ?? RefusingPrompts.Instance;
        _runner = runner;
        _clipboard = clipboard ?? NoClipboard.Instance;
        _picker = picker;
        _folders = folders;
    }

    /// <summary>The saves folder, which is what "View folder" opens.</summary>
    public string FolderPath => _savesFolder;

    /// <summary>Whether the saves folder can be opened in the file manager.</summary>
    public bool CanOpenFolder => _folders is not null && _savesFolder.Length != 0;

    /// <summary>Opens the saves folder in the desktop's file manager.</summary>
    /// <remarks>
    /// Created first if absent, matching upstream's ensureFolderPathExists -- a brand-new instance has
    /// no saves folder until a world is made, and opening a file manager on a missing path errors.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    public async Task OpenFolderAsync()
    {
        if (_folders is null || _savesFolder.Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(_savesFolder);

        await _folders.OpenAsync(_savesFolder).ConfigureAwait(true);
    }

    public string Title => "Worlds";

    /// <summary>Always false: renaming and deleting happen immediately, as on the mods page.</summary>
    public bool HasUnsavedChanges => false;

    public bool Save() => true;

    public ObservableCollection<WorldViewModel> Worlds { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Reads the worlds out of an instance's game directory.</summary>
    public void Load(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);

        _savesFolder = Core.FileSystem.PathCombine(gameRoot, "saves");

        Rebuild();

        // CanOpenFolder depends on the saves path, which was empty until now; the button is bound to it.
        OnPropertyChanged(nameof(CanOpenFolder));
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    public WorldViewModel? Selected => Worlds.FirstOrDefault(w => w.IsSelected);

    [RelayCommand]
    public void Select(string? path)
    {
        foreach (var world in Worlds)
        {
            world.IsSelected = path is not null && world.Path == path;
        }

        RaiseSelectionDependent();
    }

    public bool HasSelection => Selected is not null;

    /// <summary>
    /// Whether the selected world can be renamed.
    /// </summary>
    /// <remarks>
    /// A world that will not parse cannot be renamed: the name lives INSIDE level.dat, so renaming one
    /// means rewriting a file the launcher has just failed to read. Deleting it is still allowed —
    /// tidying up a broken save is the main reason to come here.
    /// </remarks>
    public bool CanRename => Selected is { IsValid: true };

    // ================================================================== acting

    /// <summary>Renames the selected world, asking for the new name.</summary>
    /// <summary>
    /// Upstream's worldSafetyNagQuestion: warns before touching a world the game may have open.
    /// </summary>
    /// <returns>True to go ahead.</returns>
    /// <remarks>
    /// ASKED, NOT REFUSED. Upstream's wording is kept almost exactly, because it is right about the
    /// shape of the risk: "potentially unsafe" rather than "will break", since a world sitting idle
    /// at the title screen is fine and one being actively written is not, and the launcher cannot
    /// tell which from out here.
    /// </remarks>
    private async Task<bool> SafeToTouchAsync(string what)
    {
        if (!IsLocked)
        {
            return true;
        }

        return await _prompts.ConfirmAsync(
            what,
            "Minecraft is running. Changing a world while it is open is potentially unsafe — the game "
            + "may overwrite the change, or the world may be left in a state it cannot load.\n\n"
            + "Do you wish to proceed?",
            "Continue",
            destructive: true).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task RenameSelectedAsync()
    {
        if (!CanRename || Selected is not { } selected)
        {
            return;
        }

        if (!await SafeToTouchAsync("Rename world").ConfigureAwait(true))
        {
            return;
        }

        var chosen = await _prompts.PromptForTextAsync(
            "Rename world",
            $"New name for “{selected.Name}”:",
            selected.Name).ConfigureAwait(true);

        if (chosen is null)
        {
            return;
        }

        var name = chosen.Trim();

        if (name.Length == 0)
        {
            Status = "A world needs a name.";

            return;
        }

        var world = World.Load(selected.Path);

        if (!world.Rename(name))
        {
            Status = $"Could not rename “{selected.Name}”. The game may still be running.";

            return;
        }

        Status = $"Renamed to “{name}”.";

        Rebuild();

        // Renaming moves the folder as well as the name inside level.dat, so the selection follows the
        // world to wherever it ended up.
        Select(world.Path);
    }

    /// <summary>
    /// Deletes the selected world, after asking.
    /// </summary>
    /// <remarks>
    /// THE ONE PLACE ON THIS PAGE THAT ASKS. Upstream's message says the world "may be gone forever (A
    /// LONG TIME)" and defaults to No; the wording here is plainer and the default is the same.
    /// </remarks>
    /// <summary>Whether the selected world can be copied.</summary>
    /// <remarks>
    /// Needs a world that PARSES, unlike delete: copying one whose level.dat will not read would
    /// produce a second copy of something already broken, and name it after a folder rather than a
    /// world.
    /// </remarks>
    public bool CanCopy => Selected is { IsValid: true };

    /// <summary>
    /// Copies the selected world under a new name.
    /// </summary>
    /// <remarks>
    /// Ported from WorldListPage::on_actionCopy_triggered and World::install.
    ///
    /// THE SAFE WAY TO EXPERIMENT WITH A SAVE, and the reason it is worth having: the alternative is
    /// somebody copying a folder by hand in a file manager, ending up with two worlds both called
    /// "New World" in the game's list, and not knowing which is which -- because the game reads the
    /// name out of level.dat and does not care what the folder is called.
    ///
    /// So this does what upstream does: copy the folder, then REWRITE THE NAME INSIDE the copy.
    /// </remarks>
    [RelayCommand]
    public async Task CopySelectedAsync()
    {
        if (!CanCopy || Selected is not { } selected)
        {
            return;
        }

        if (!await SafeToTouchAsync("Copy world").ConfigureAwait(true))
        {
            return;
        }

        var chosen = await _prompts.PromptForTextAsync(
            "Copy world",
            $"Name for the copy of “{selected.Name}”:",
            selected.Name).ConfigureAwait(true);

        if (chosen is null)
        {
            return;
        }

        var name = chosen.Trim();

        if (name.Length == 0)
        {
            Status = "A world needs a name.";

            return;
        }

        /*
         * THE FOLDER IS NAMED AFTER THE ORIGINAL, deduped, which is upstream's behaviour and looks
         * odd until you notice it cannot be otherwise: a world's name may contain characters a
         * folder cannot, so the folder name is always derived and never the name as typed. Dedupe is
         * what stops a copy landing on top of the world it came from.
         */
        var folder = Core.FileSystem.DirNameFromString(selected.Name, _savesFolder);
        var destination = Core.FileSystem.PathCombine(_savesFolder, folder);

        Status = $"Copying “{selected.Name}”…";

        var copy = new CopyTask(selected.Path, destination);

        /*
         * BEHIND THE PROGRESS WINDOW WHERE THERE IS ONE. A test world is a handful of files; a real
         * one is hundreds of megabytes of region data, and copying that inline is the same silence
         * the progress window was built to end.
         *
         * Falls back to running it directly when there is no runner, so a headless caller and the
         * tests still work -- and so the page is never disabled for want of a window.
         */
        var copied = _runner is not null
            ? await _runner.RunAsync(new CopyTaskAdapter(copy), $"Copying {selected.Name}").ConfigureAwait(true)
            : await copy.RunAsync().ConfigureAwait(true);

        if (!copied)
        {
            /*
             * A HALF-COPIED WORLD IS WORSE THAN NO COPY. Cancelling leaves a folder that looks like a
             * world, appears in this list and in the game's, and is missing region files -- so the
             * partial copy goes, and the original was never touched.
             */
            TryRemove(destination);

            Status = copy.State == TaskState.AbortedByUser
                ? $"Copy of “{selected.Name}” cancelled. Nothing was changed."
                : $"Could not copy “{selected.Name}”: {copy.FailReason}";

            Rebuild();

            return;
        }

        /*
         * AND THEN THE NAME INSIDE IT. Without this the copy is a second world with the same name in
         * the game's list, which is the exact confusion this feature exists to avoid -- and the
         * folder name, which is the only thing that differs, is not shown anywhere in Minecraft.
         */
        if (!string.Equals(name, selected.Name, StringComparison.Ordinal))
        {
            var world = World.Load(destination);

            if (!world.Rename(name))
            {
                /*
                 * The copy IS there and is usable; it just has the old name inside it. Said plainly
                 * rather than treated as a failure, because deleting a good copy to report a tidy
                 * error would be worse than leaving somebody a world to rename themselves.
                 */
                Status = $"Copied “{selected.Name}”, but the copy could not be renamed. It is in {folder}.";

                Rebuild();

                return;
            }
        }

        Status = $"Copied “{selected.Name}” to “{name}”.";

        Rebuild();
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        if (!await SafeToTouchAsync("Delete world").ConfigureAwait(true))
        {
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(
            "Delete world",
            $"Delete “{selected.Name}”?\n\n"
            + "A world is the one thing in an instance that exists nowhere else. "
            + $"It will be moved to the {InstanceRemoval.TrashName} where that is possible.",
            "Delete",
            destructive: true).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        /*
         * allowInvalid, which upstream cannot do: see World.Destroy and upstream bug #20. A save the
         * launcher cannot parse is exactly the one a user came here to remove, and refusing leaves
         * them with a row the launcher shows and will not act on.
         */
        if (!World.Load(selected.Path).Destroy(allowInvalid: true))
        {
            Status = $"Could not delete “{selected.Name}”. The game may still be running.";

            return;
        }

        Status = $"Deleted “{selected.Name}”.";

        Rebuild();
    }

    [RelayCommand]
    public void Refresh() => Rebuild();

    /// <summary>Whether the selected world's seed can be copied.</summary>
    /// <remarks>
    /// A world that will not parse has no seed to copy -- the number comes from level.dat, the file the
    /// launcher just failed to read -- so this follows validity, the way rename and copy do.
    /// </remarks>
    public bool CanCopySeed => Selected is { IsValid: true };

    /// <summary>Copies the selected world's seed to the clipboard.</summary>
    /// <remarks>
    /// The bare number, which is what a seed is and what the game's "seed" command prints. Upstream's
    /// actionCopy_Seed does the same. A world that declares no seed copies "0", indistinguishable from
    /// the world whose seed is 0 -- level.dat does not tell the two apart, so neither can this.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCopySeed))]
    public async Task CopySeedSelectedAsync()
    {
        if (Selected is not { IsValid: true } selected)
        {
            return;
        }

        Status = await _clipboard.SetTextAsync(selected.SeedString).ConfigureAwait(true)
            ? $"Copied the seed of “{selected.Name}” to the clipboard."
            : "Could not copy the seed: there is no clipboard.";
    }

    /// <summary>Whether a world can be imported. Needs a picker to choose the file.</summary>
    public bool CanAdd => _picker is not null;

    /// <summary>Imports a world from a chosen zip, then re-reads the folder.</summary>
    /// <remarks>
    /// Upstream's actionAdd. The import itself lives in <see cref="World.Install"/>; this only chooses
    /// the file and reports what happened. The list is re-read from disk rather than patched, the way
    /// every change on this page is -- the folder is the truth.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    public async Task AddAsync()
    {
        if (_picker is null)
        {
            return;
        }

        var source = await _picker.PickAsync().ConfigureAwait(true);

        if (source.Length == 0)
        {
            return;
        }

        var installed = World.Install(source, _savesFolder);

        if (installed is null)
        {
            Status = "Could not import that file. It does not contain a world, or could not be read.";

            return;
        }

        Rebuild();

        /*
         * Land the selection on what was just imported, so the next action acts on it. Matched by
         * folder name, not by the returned path: the scan and the path builder can spell the same
         * directory with different separators, and the folder name is unique after DirNameFromString.
         */
        var importedFolder = System.IO.Path.GetFileName(installed);
        var row = Worlds.FirstOrDefault(w =>
            string.Equals(w.FolderName, importedFolder, StringComparison.Ordinal));

        if (row is not null)
        {
            Select(row.Path);
        }

        Status = $"Imported “{row?.Name ?? importedFolder}”.";
    }

    /// <summary>Whether the selected world's datapacks folder can be opened.</summary>
    public bool CanOpenDatapacks => _folders is not null && Selected is { IsValid: true };

    /// <summary>Opens the selected world's datapacks folder in the file manager.</summary>
    /// <remarks>
    /// Upstream's actionDatapacks. Datapacks live INSIDE the world (world/datapacks), so this opens the
    /// selected world's own folder, not the instance's. Nags first when the game may be running -- the
    /// folder is one the user is about to change by hand -- and creates it if absent, so a world with no
    /// datapacks yet still has somewhere to drop the first one.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanOpenDatapacks))]
    public async Task OpenDatapacksSelectedAsync()
    {
        if (_folders is null || Selected is not { IsValid: true } selected)
        {
            return;
        }

        if (!await SafeToTouchAsync("Open the datapacks folder").ConfigureAwait(true))
        {
            return;
        }

        var datapacks = Core.FileSystem.PathCombine(selected.Path, "datapacks");

        Directory.CreateDirectory(datapacks);

        await _folders.OpenAsync(datapacks).ConfigureAwait(true);
    }

    /// <summary>Whether the selected world has an icon to reset.</summary>
    /// <remarks>
    /// Gated on the world HAVING an icon, not just being valid: with nothing to remove the action does
    /// nothing, so upstream disables it, and so does this.
    /// </remarks>
    public bool CanResetIcon => Selected is { IsValid: true, HasIcon: true };

    /// <summary>Removes the selected world's icon so Minecraft regenerates it on next play.</summary>
    [RelayCommand(CanExecute = nameof(CanResetIcon))]
    public void ResetIconSelected()
    {
        if (Selected is not { IsValid: true, HasIcon: true } selected)
        {
            return;
        }

        if (World.Load(selected.Path).ResetIcon())
        {
            Status = $"Reset the icon of “{selected.Name}”.";

            Rebuild();
            Select(selected.Path);
        }
        else
        {
            Status = $"Could not reset the icon of “{selected.Name}”. The game may still be running.";
        }
    }

    private void Rebuild()
    {
        var selected = Selected?.Path;

        Worlds.Clear();

        if (_savesFolder.Length != 0)
        {
            /*
             * Most recently played first, which is the order someone looking for "the world I was just
             * in" needs. Worlds that will not parse have no last-played time and sort to the end,
             * where they are still visible rather than hidden.
             */
            foreach (var world in WorldList.Scan(_savesFolder)
                         .OrderByDescending(w => w.IsValid)
                         .ThenByDescending(w => w.LastPlayed))
            {
                Worlds.Add(new WorldViewModel
                {
                    Path = world.Path,

                    // The folder name where level.dat will not read: something has to identify the row,
                    // and it is the only thing known about a broken save.
                    Name = world.IsValid && world.ActualName.Length != 0 ? world.ActualName : world.FolderName,
                    FolderName = world.FolderName,
                    GameType = world.IsValid ? world.GameType.ToString() : string.Empty,
                    LastPlayed = world.LastPlayed,
                    Seed = world.RandomSeed,
                    IsValid = world.IsValid,
                    HasIcon = world.HasIcon,
                });
            }
        }

        if (selected is not null)
        {
            Select(selected);
        }

        RaiseSelectionDependent();
    }

    private void RaiseSelectionDependent()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanCopySeed));
        OnPropertyChanged(nameof(CanOpenDatapacks));
        OnPropertyChanged(nameof(CanResetIcon));

        RenameSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CopySeedSelectedCommand.NotifyCanExecuteChanged();
        OpenDatapacksSelectedCommand.NotifyCanExecuteChanged();
        ResetIconSelectedCommand.NotifyCanExecuteChanged();
    }
}
