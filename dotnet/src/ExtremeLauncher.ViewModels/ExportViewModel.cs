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
 * Ported in behaviour from launcher/ui/dialogs/ExportInstanceDialog.cpp and ExportPackDialog.cpp.
 *
 * TWO EXPORTS THAT ARE NOT THE SAME THING, and upstream gives them separate dialogs for good reason:
 *
 *   instance zip   a BACKUP. Everything, worlds and screenshots included, restores exactly what was
 *                  there, readable only by this family of launchers.
 *   .mrpack        a SHARE. Mods become links, somebody else's worlds are left out, and any launcher
 *                  can install it.
 *
 * One dialog with a mode rather than two windows, because the choice between them is the first thing
 * the user has to make and putting it behind two menu entries makes it a guess.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ExtremeLauncher.ViewModels;

public enum ExportKind
{
    /// <summary>Everything, as a plain zip. A backup.</summary>
    InstanceZip,

    /// <summary>A Modrinth modpack. For other people.</summary>
    ModrinthPack,

    /// <summary>A CurseForge modpack. Mods it recognises are linked by id; the rest are carried.</summary>
    CurseForgePack,
}

/// <summary>Helpers over ExportKind, so the pack-vs-zip distinction lives in one place.</summary>
public static class ExportKindExtensions
{
    /// <summary>Whether the kind produces a modpack (with a manifest and name), as opposed to a zip.</summary>
    public static bool IsPack(this ExportKind kind)
        => kind is ExportKind.ModrinthPack or ExportKind.CurseForgePack;

    /// <summary>The file extension the kind saves as.</summary>
    public static string Extension(this ExportKind kind)
        => kind == ExportKind.ModrinthPack ? "mrpack" : "zip";
}

/// <summary>Runs the chosen export. Implemented by the app.</summary>
public interface IExportRunner
{
    /// <summary>Asks where to save. Empty when cancelled.</summary>
    Task<string> PickTargetAsync(ExportKind kind, string suggestedFileName);

    /// <returns>A sentence describing what happened, for the dialog to show.</returns>
    Task<string> RunAsync(ExportKind kind, string targetPath, string name, string version, string summary);
}

public sealed partial class ExportViewModel : ObservableObject
{
    private readonly IExportRunner? _runner;

    public ExportViewModel(string instanceName, IExportRunner? runner = null)
    {
        _runner = runner;

        InstanceName = instanceName;
        Name = instanceName;
    }

    public string InstanceName { get; }

    [ObservableProperty]
    private ExportKind _kind = ExportKind.ModrinthPack;

    /// <summary>The pack's name. Only used by the modpack export.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _version = "1.0.0";

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>True once an export has finished successfully.</summary>
    [ObservableProperty]
    private bool _isDone;

    /// <summary>Whether the pack fields are relevant. A zip has no manifest to put them in.</summary>
    public bool ShowsPackFields => Kind.IsPack();

    public string KindDescription => Kind switch
    {
        ExportKind.ModrinthPack =>
            "Mods are recorded as links rather than copied, so the file stays small. Worlds, "
            + "screenshots and your options are left out. Any launcher can install it.",
        ExportKind.CurseForgePack =>
            "Mods from CurseForge are recorded by id; anything else -- Modrinth mods, config -- is "
            + "bundled in. Worlds, screenshots and your options are left out. For CurseForge's own app "
            + "and launchers that read its format.",
        _ =>
            "Everything in the instance, including worlds and screenshots. A backup, or a way to move "
            + "the instance to another machine.",
    };

    public bool CanExport => _runner is not null
        && !IsExporting
        && (!Kind.IsPack() || Name.Trim().Length != 0);

    /// <summary>What to call the file, before the user changes it.</summary>
    public string SuggestedFileName
    {
        get
        {
            var stem = Kind.IsPack() && Name.Trim().Length != 0 ? Name.Trim() : InstanceName;

            // Anything a filesystem would refuse, replaced rather than left to fail at save time.
            foreach (var bad in Path.GetInvalidFileNameChars())
            {
                stem = stem.Replace(bad, '-');
            }

            return stem + "." + Kind.Extension();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task ExportAsync()
    {
        if (_runner is null)
        {
            return;
        }

        var target = await _runner.PickTargetAsync(Kind, SuggestedFileName).ConfigureAwait(true);

        if (target.Length == 0)
        {
            return;
        }

        IsExporting = true;
        Status = "Exporting…";

        try
        {
            Status = await _runner
                .RunAsync(Kind, target, Name.Trim(), Version.Trim(), Summary.Trim())
                .ConfigureAwait(true);

            /*
             * The dialog STAYS OPEN and says what happened, rather than closing on success. An export
             * produces a file somewhere the user then has to find, and a window that vanishes takes
             * the only statement of where it went with it.
             */
            IsDone = true;
        }
        finally
        {
            IsExporting = false;
        }
    }

    partial void OnKindChanged(ExportKind value)
    {
        OnPropertyChanged(nameof(ShowsPackFields));
        OnPropertyChanged(nameof(KindDescription));
        OnPropertyChanged(nameof(SuggestedFileName));
        RaiseCanExport();
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(SuggestedFileName));
        RaiseCanExport();
    }

    partial void OnIsExportingChanged(bool value) => RaiseCanExport();

    private void RaiseCanExport()
    {
        // Announced as well as computed: bound to IsEnabled. See AccountsViewModel.
        OnPropertyChanged(nameof(CanExport));
        ExportCommand.NotifyCanExecuteChanged();
    }
}
