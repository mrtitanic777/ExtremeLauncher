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
 * Ported in behaviour from launcher/ui/java/InstallJavaDialog.cpp.
 *
 * GETTING A JAVA WITHOUT LEAVING THE LAUNCHER. This is the reason somebody who has never installed a
 * JDK can run a modded 1.20.1 instance: the game needs Java 17, the machine has Java 8, and the
 * alternative to this dialog is a trip to a vendor's website and a wrong guess about which build.
 *
 * FILTERED BY MAJOR VERSION, because that is the only number that decides compatibility. The list is
 * two hundred and forty-odd runtimes on a real metadata server and almost nobody wants to read it --
 * they want "the Java 17 one".
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

/// <summary>Lists and installs Java runtimes. Implemented in the app.</summary>
public interface IJavaInstallService
{
    Task<IReadOnlyList<InstallableJava>> ListAsync(CancellationToken cancellationToken);

    /// <returns>A sentence describing what happened.</returns>
    Task<string> InstallAsync(InstallableJava java, CancellationToken cancellationToken);
}

public sealed partial class JavaInstallViewModel(IJavaInstallService? service = null) : ObservableObject
{
    private IReadOnlyList<InstallableJava> _all = [];

    /// <summary>The runtimes matching the current filter.</summary>
    public ObservableCollection<InstallableJava> Runtimes { get; } = [];

    /// <summary>The major versions on offer, newest first, plus "All".</summary>
    public ObservableCollection<string> Majors { get; } = [];

    [ObservableProperty]
    private InstallableJava? _selected;

    [ObservableProperty]
    private string _selectedMajor = AllMajors;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>What "no filter" is called in the picker.</summary>
    public const string AllMajors = "All";

    public bool CanLoad => service is not null && !IsBusy;

    public bool CanInstall => service is not null && !IsBusy && Selected is not null;

    /// <summary>Fetches the list.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    public async Task LoadAsync()
    {
        if (service is null)
        {
            return;
        }

        IsBusy = true;
        Status = "Looking for available runtimes…";

        try
        {
            _all = await service.ListAsync(CancellationToken.None).ConfigureAwait(true);

            Majors.Clear();
            Majors.Add(AllMajors);

            foreach (var major in _all.Select(j => j.Major).Distinct().OrderByDescending(m => m))
            {
                Majors.Add(major.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            Rebuild();

            /*
             * Named for THIS machine. A list that silently excludes other platforms looks short and
             * arbitrary, and somebody comparing it with a vendor's website will wonder what is wrong.
             */
            Status = _all.Count == 0
                ? "No runtimes were found for this machine."
                : $"{_all.Count} runtimes available for {SysInfo.SupportedJavaArchitecture()}.";
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Could not fetch the list: {e.Message}";
        }
        finally
        {
            IsBusy = false;

            RaiseDependent();
        }
    }

    /// <summary>Downloads and unpacks the selected runtime.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    public async Task InstallAsync()
    {
        if (service is null || Selected is not { } chosen)
        {
            return;
        }

        IsBusy = true;
        Status = $"Installing {chosen.DisplayName}…";

        try
        {
            Status = await service.InstallAsync(chosen, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;

            RaiseDependent();
        }
    }

    private void Rebuild()
    {
        var previous = Selected;

        Runtimes.Clear();

        foreach (var java in _all)
        {
            if (!string.Equals(SelectedMajor, AllMajors, StringComparison.Ordinal)
                && !string.Equals(
                    java.Major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SelectedMajor,
                    StringComparison.Ordinal))
            {
                continue;
            }

            Runtimes.Add(java);
        }

        // Kept across a filter change where it survives; otherwise the newest, which is what the list
        // is already ordered by and what somebody filtering to a major almost always wants.
        Selected = Runtimes.Contains(previous!) ? previous : Runtimes.FirstOrDefault();
    }

    partial void OnSelectedMajorChanged(string value) => Rebuild();

    partial void OnSelectedChanged(InstallableJava? value) => RaiseDependent();

    partial void OnIsBusyChanged(bool value) => RaiseDependent();

    private void RaiseDependent()
    {
        // Announced as well as computed: both are bound to IsEnabled. See AccountsViewModel.
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(CanInstall));
        LoadCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }
}
