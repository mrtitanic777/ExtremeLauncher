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
 * Ported from ui/InstanceWindow.cpp and the PageContainer it hosts, less everything that is Qt.
 *
 * WHAT UPSTREAM'S PAGE CONTAINER DOES THAT MATTERS: each page has an apply() called when you leave it,
 * and the container calls every page's apply() on close. Miss that and a user's edits vanish when they
 * shut the window -- which is the single most annoying way for a settings screen to fail, because
 * nothing appears to go wrong.
 *
 * SAVING IS EXPLICIT AND CHECKED HERE. Upstream's BasePage::apply() returns void, so a page that could
 * not write its file has no way to say so and the window closes anyway. Save() returns a bool, the
 * window refuses to close on false, and the failure is put in front of the user.
 *
 * THIS WINDOW IS ALSO THE LAUNCH WINDOW, which is upstream's arrangement and not an obvious one.
 * InstanceWindow carries Launch and Kill buttons, and `runningStateChanged` SELECTS THE LOG PAGE the
 * moment a game starts -- so pressing Launch anywhere puts the output in front of you rather than
 * leaving it somewhere to be found.
 *
 * CLOSING THIS WINDOW DOES NOT STOP THE GAME. Upstream's closeEvent saves the pages and accepts;
 * nothing kills the process. That is right: the window is a console onto a running game, not the game
 * itself, and someone tidying their desktop should not lose their session.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ExtremeLauncher.ViewModels;

/// <summary>One page of the instance window.</summary>
public interface IInstancePage
{
    /// <summary>What the page list calls it.</summary>
    string Title { get; }

    /// <summary>Whether it holds edits that are not on disk.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>Writes its edits out. False means it could not, and must not be ignored.</summary>
    bool Save();
}

/// <summary>A page that must not be edited while the game is running.</summary>
/// <remarks>
/// For pages that edit a file THE GAME OWNS AND REWRITES -- servers.dat is the one, so far. An edit
/// made while the game is up is not merged with what the game writes on exit; it is replaced by it,
/// silently. Upstream carries the same flag on its servers model and sets it from
/// runningStateChanged.
/// </remarks>
public interface ILocksWhileRunning
{
    bool IsLocked { get; set; }
}

public sealed partial class InstanceWindowViewModel : ObservableObject
{
    private readonly IUserPrompts _prompts;

    /// <param name="launch">
    /// The instance's own coordinator, from the LaunchRegistry. Null in a build with no launching, where
    /// the buttons stay disabled rather than doing nothing.
    /// </param>
    public InstanceWindowViewModel(
        string instanceName,
        IUserPrompts? prompts = null,
        LaunchCoordinator? launch = null)
    {
        InstanceName = instanceName;
        _prompts = prompts ?? RefusingPrompts.Instance;
        Launch = launch;

        if (Launch is not null)
        {
            Launch.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(LaunchCoordinator.IsBusy))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(CanKill));

                LockPages();

                LaunchCommand.NotifyCanExecuteChanged();
                KillCommand.NotifyCanExecuteChanged();

                /*
                 * Upstream's runningStateChanged: the log page is selected the MOMENT a game starts, so
                 * pressing Launch puts the output in front of you. Only on the way up -- jumping to the
                 * log when a game exits would yank the page out from under someone reading their mods.
                 */
                if (Launch.IsBusy && LogPage is not null)
                {
                    SelectedPage = LogPage;
                }
            };
        }
    }

    public string InstanceName { get; }

    /// <summary>This instance's launch, or null in a build with no launching.</summary>
    public LaunchCoordinator? Launch { get; }

    /// <summary>The log page, remembered so a starting game can jump to it.</summary>
    public IInstancePage? LogPage { get; private set; }

    public bool IsRunning => Launch is { IsBusy: true };

    /// <summary>Whether this instance can be started from here.</summary>
    /// <remarks>
    /// Refused while it is already running -- the second launch would extract natives into the folder
    /// the first is reading. That is per INSTANCE, which is why each has its own coordinator; a
    /// different pack starting at the same time is fine and upstream allows it.
    /// </remarks>
    public bool CanLaunch => Launch is { IsBusy: false };

    public bool CanKill => Launch is { IsBusy: true };

    public string Title => $"{InstanceName} — Extreme Launcher";

    /// <summary>The pages, in the order the list shows them.</summary>
    public ObservableCollection<IInstancePage> Pages { get; } = [];

    [ObservableProperty]
    private IInstancePage? _selectedPage;

    /// <summary>What went wrong with the last save, or empty.</summary>
    [ObservableProperty]
    private string _saveFailure = string.Empty;

    public bool HasSaveFailure => SaveFailure.Length != 0;

    partial void OnSaveFailureChanged(string value) => OnPropertyChanged(nameof(HasSaveFailure));

    /// <summary>Adds a page, selecting the first one added.</summary>
    public void AddPage(IInstancePage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Pages.Add(page);

        SelectedPage ??= page;

        // Remembered so a starting game can jump straight to it.
        if (page is LogPageViewModel)
        {
            LogPage = page;
        }

        // A page added to a window whose game is ALREADY running starts locked. The instance window
        // can be opened at any time, including from the log of a game in progress.
        if (page is ILocksWhileRunning lockable)
        {
            lockable.IsLocked = IsRunning;
        }
    }

    /// <summary>Locks or releases the pages that edit files the game owns.</summary>
    private void LockPages()
    {
        foreach (var page in Pages.OfType<ILocksWhileRunning>())
        {
            page.IsLocked = IsRunning;
        }
    }

    /// <summary>Starts this instance.</summary>
    [RelayCommand]
    public async Task LaunchAsync()
    {
        if (!CanLaunch || Launch is null)
        {
            return;
        }

        await Launch.LaunchAsync(InstanceId).ConfigureAwait(true);
    }

    /// <summary>Asks the running game to stop.</summary>
    [RelayCommand]
    public void Kill()
    {
        if (CanKill)
        {
            Launch!.Cancel();
        }
    }

    /// <summary>The id this window launches, which is not always the display name.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>Whether any page holds edits that are not on disk.</summary>
    public bool HasUnsavedChanges => Pages.Any(p => p.HasUnsavedChanges);

    /// <summary>
    /// Writes out every page that has something to write.
    /// </summary>
    /// <remarks>
    /// EVERY page is attempted, not just up to the first failure. One page failing to save is no reason
    /// to abandon the others' work, and the user is going to be told about it either way.
    /// </remarks>
    public bool SaveAll()
    {
        var failed = new List<string>();

        foreach (var page in Pages.Where(p => p.HasUnsavedChanges))
        {
            if (!page.Save())
            {
                failed.Add(page.Title);
            }
        }

        SaveFailure = failed.Count == 0
            ? string.Empty
            : $"Could not save: {string.Join(", ", failed)}. Check the instance folder is writable.";

        return failed.Count == 0;
    }

    /// <summary>
    /// Decides whether the window may close, saving or discarding as the user asks.
    /// </summary>
    /// <returns>False to keep the window open.</returns>
    public async Task<bool> RequestCloseAsync()
    {
        /*
         * A RUNNING GAME DOES NOT BLOCK THIS, and is not even mentioned. Upstream's closeEvent saves the
         * pages and accepts; nothing kills the process. The window is a console onto a running game, not
         * the game itself, and someone tidying their desktop should not lose their session.
         */
        if (!HasUnsavedChanges)
        {
            return true;
        }

        /*
         * ASKED, NOT ASSUMED. Upstream saves silently on close, which is fine until the edit was a
         * mistake -- and on this page a mistake means an instance that no longer launches. The
         * affirmative answer here is "Save", because that is what someone who edited something
         * usually meant.
         */
        var save = await _prompts.ConfirmAsync(
            "Unsaved changes",
            $"“{InstanceName}” has changes that are not saved.\n\nSave them before closing?",
            "Save").ConfigureAwait(true);

        if (!save)
        {
            /*
             * Discarded, and the window closes. Nothing on disk was touched, so there is nothing to
             * undo -- reopening the window re-reads the files.
             */
            return true;
        }

        // A save that failed must not close the window: closing would discard the very edits the user
        // just asked to keep.
        return SaveAll();
    }

    [RelayCommand]
    public void SelectPage(IInstancePage? page) => SelectedPage = page;

    /// <summary>
    /// Releases anything the pages hold. Called once the window has actually closed.
    /// </summary>
    /// <remarks>
    /// Only the log pages need this today -- one of them holds a filesystem watcher -- but a page that
    /// leaks an OS handle every time an instance window opens is exactly the kind of thing nobody
    /// notices until a launcher that has been open all day stops being able to watch anything.
    /// </remarks>
    public void DisposePages()
    {
        foreach (var page in Pages.OfType<IDisposable>())
        {
            page.Dispose();
        }
    }
}
