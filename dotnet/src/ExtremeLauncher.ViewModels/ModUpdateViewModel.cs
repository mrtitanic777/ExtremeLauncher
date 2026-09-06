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
 * Ported in behaviour from launcher/ui/dialogs/ModUpdateDialog.cpp.
 *
 * WHAT THE USER SEES WHEN THEY ASK "is anything out of date". Everything is ticked to begin with,
 * because "update all" is what people mean by pressing the button -- but each one can be unticked,
 * because a mod that has just broken for everybody is one to skip this week.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

/// <summary>Checks for and applies mod updates. Implemented in the app.</summary>
public interface IModUpdateService
{
    Task<ModUpdateReport> CheckAsync(CancellationToken cancellationToken);

    /// <returns>A sentence describing what happened.</returns>
    Task<string> ApplyAsync(IReadOnlyList<ModUpdate> updates, CancellationToken cancellationToken);
}

/// <summary>One available update.</summary>
public sealed partial class ModUpdateRowViewModel(ModUpdate update) : ObservableObject
{
    public ModUpdate Update { get; } = update;

    public string Name => Update.Name;

    public string Change => $"{Update.CurrentFileName}  →  {Update.NewFileName}";

    public string NewVersionName => Update.NewVersionName;

    public bool IsDisabledMod => Update.WasDisabled;

    /// <summary>Ticked by default: "update all" is what pressing the button means.</summary>
    [ObservableProperty]
    private bool _isChosen = true;
}

public sealed partial class ModUpdateViewModel(IModUpdateService? service = null, string loader = "")
    : ObservableObject
{
    public ObservableCollection<ModUpdateRowViewModel> Updates { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Set once updates have been applied, so the dialog can say so and stop offering.</summary>
    [ObservableProperty]
    private bool _isDone;

    public bool CanCheck => service is not null && !IsBusy;

    public int ChosenCount => Updates.Count(u => u.IsChosen);

    public bool CanApply => service is not null && !IsBusy && !IsDone && ChosenCount > 0;

    public string ApplyLabel => ChosenCount switch
    {
        0 => "Update",
        1 => "Update 1 mod",
        var n => $"Update {n} mods",
    };

    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task CheckAsync()
    {
        if (service is null)
        {
            return;
        }

        /*
         * Said before asking, because Modrinth's update filter needs a loader and a vanilla instance
         * has none. Without this the check comes back empty and reads as "everything is current",
         * which is a different and wrong answer.
         */
        if (loader.Length == 0)
        {
            Status = "This instance has no mod loader, so there are no mods to update.";

            return;
        }

        IsBusy = true;
        Status = "Checking…";

        Updates.Clear();

        try
        {
            var report = await service.CheckAsync(CancellationToken.None).ConfigureAwait(true);

            foreach (var update in report.Updates)
            {
                Updates.Add(Track(new ModUpdateRowViewModel(update)));
            }

            Status = Describe(report);
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            // "Could not check" and "no updates" are different answers, and only one of them means
            // the instance is current.
            Status = e.Message;
        }
        finally
        {
            IsBusy = false;

            RaiseSelectionDependent();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    public async Task ApplyAsync()
    {
        if (service is null)
        {
            return;
        }

        var chosen = Updates.Where(u => u.IsChosen).Select(u => u.Update).ToArray();

        if (chosen.Length == 0)
        {
            return;
        }

        IsBusy = true;
        Status = "Updating…";

        try
        {
            Status = await service.ApplyAsync(chosen, CancellationToken.None).ConfigureAwait(true);

            /*
             * The dialog STAYS OPEN and says what happened. Some mods may have failed, and a window
             * that closes on completion takes the only account of which ones with it.
             */
            IsDone = true;
        }
        finally
        {
            IsBusy = false;

            RaiseSelectionDependent();
        }
    }

    /// <summary>
    /// What the check amounted to, including what it could not answer for.
    /// </summary>
    /// <remarks>
    /// THE UNRECOGNISED COUNT IS SAID OUT LOUD. Only Modrinth is asked, so a CurseForge mod -- or one
    /// built by hand -- is simply absent from the answer, which is indistinguishable from "already
    /// current". A folder full of CurseForge mods would otherwise report "Everything is up to date",
    /// which is not merely unhelpful: it is wrong, and it is the kind of wrong somebody acts on.
    /// </remarks>
    private static string Describe(ModUpdateReport report)
    {
        var found = report.Updates.Count switch
        {
            0 => "Everything Modrinth knows about is up to date.",
            1 => "1 mod can be updated.",
            var n => $"{n} mods can be updated.",
        };

        if (report.Unrecognised == 0)
        {
            // Nothing was skipped, so the plainer sentence is the honest one.
            return report.Updates.Count == 0 ? "Everything is up to date." : found;
        }

        var skipped = report.Unrecognised == 1
            ? "1 mod was not recognised by Modrinth and could not be checked"
            : $"{report.Unrecognised} mods were not recognised by Modrinth and could not be checked";

        return $"{found} {skipped} — mods from CurseForge or added by hand are not covered.";
    }

    [RelayCommand]
    public void SelectAll() => SetAll(true);

    [RelayCommand]
    public void SelectNone() => SetAll(false);

    private void SetAll(bool chosen)
    {
        foreach (var update in Updates)
        {
            update.IsChosen = chosen;
        }

        RaiseSelectionDependent();
    }

    private ModUpdateRowViewModel Track(ModUpdateRowViewModel row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModUpdateRowViewModel.IsChosen))
            {
                RaiseSelectionDependent();
            }
        };

        return row;
    }

    private void RaiseSelectionDependent()
    {
        // Announced as well as computed: all three are bound. See AccountsViewModel.
        OnPropertyChanged(nameof(ChosenCount));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(ApplyLabel));
        ApplyCommand.NotifyCanExecuteChanged();
        CheckCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => RaiseSelectionDependent();

    partial void OnIsDoneChanged(bool value) => RaiseSelectionDependent();
}
