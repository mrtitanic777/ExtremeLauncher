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
 * Ported from ui/pages/instance/ScreenshotsPage.cpp, less the Imgur upload.
 *
 * PNG ONLY, which is upstream's filter and the game's own output format. A folder full of other things
 * is not this page's business, and showing them would invite renaming a file the rename does not
 * understand -- upstream appends ".png" to whatever is typed.
 *
 * DELETING ASKS, and trashes before it deletes, both as upstream does. A screenshot is not a world, but
 * it is also not re-downloadable the way a mod is: it is the only copy of a moment somebody wanted to
 * keep. That is the same reasoning as the worlds page, one notch quieter.
 *
 * THE UPLOAD IS NOT PORTED. Upstream can put screenshots on Imgur, which means an API key, an album
 * API, and sending a user's images to a third party. Not something to reproduce by halves.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ViewModels;

/// <summary>One row: a screenshot in the instance's screenshots folder.</summary>
public sealed partial class ScreenshotViewModel : ObservableObject
{
    public required string Path { get; init; }

    /// <summary>The filename without ".png", which is what upstream shows and lets you edit.</summary>
    public required string Name { get; init; }

    public DateTimeOffset Taken { get; init; }

    public string TakenString => Taken == default
        ? string.Empty
        : Taken.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class ScreenshotsPageViewModel : ObservableObject, IInstancePage
{
    private readonly IUserPrompts _prompts;

    private readonly IScreenshotUploader? _uploader;

    private readonly IClipboard _clipboard;

    private readonly IFolderOpener? _folders;

    private string _folder = string.Empty;

    public ScreenshotsPageViewModel(
        IUserPrompts? prompts = null,
        IScreenshotUploader? uploader = null,
        IClipboard? clipboard = null,
        IFolderOpener? folders = null)
    {
        _prompts = prompts ?? RefusingPrompts.Instance;
        _uploader = uploader;
        _clipboard = clipboard ?? NoClipboard.Instance;
        _folders = folders;
    }

    public string Title => "Screenshots";

    /// <summary>Always false: renaming and deleting happen immediately.</summary>
    public bool HasUnsavedChanges => false;

    public bool Save() => true;

    public ObservableCollection<ScreenshotViewModel> Screenshots { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    public void Load(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);

        _folder = FileSystem.PathCombine(gameRoot, "screenshots");

        Rebuild();

        // CanOpenFolder depends on the folder path, empty until now; the button is bound to it.
        OnPropertyChanged(nameof(CanOpenFolder));
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    public ScreenshotViewModel? Selected => Screenshots.FirstOrDefault(s => s.IsSelected);

    [RelayCommand]
    public void Select(string? path)
    {
        foreach (var screenshot in Screenshots)
        {
            screenshot.IsSelected = path is not null && screenshot.Path == path;
        }

        RaiseSelectionDependent();
    }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Screenshots.Count == 0;

    // ================================================================== acting

    /// <summary>Renames the selected screenshot, keeping the ".png".</summary>
    /// <remarks>
    /// The extension is appended rather than asked for, as upstream does: the list shows names without
    /// it, so typing one back would give "shot.png.png", and typing none would give a file the page
    /// then filters out of its own list.
    /// </remarks>
    [RelayCommand]
    public async Task RenameSelectedAsync()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        var chosen = await _prompts.PromptForTextAsync(
            "Rename screenshot",
            $"New name for “{selected.Name}”:",
            selected.Name).ConfigureAwait(true);

        if (chosen is null)
        {
            return;
        }

        var name = chosen.Trim();

        if (name.Length == 0)
        {
            Status = "A screenshot needs a name.";

            return;
        }

        var target = FileSystem.PathCombine(_folder, name + ".png");

        if (File.Exists(target))
        {
            // Refused rather than overwritten: the other file is also the only copy of something.
            Status = $"There is already a screenshot called “{name}”.";

            return;
        }

        try
        {
            File.Move(selected.Path, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not rename “{selected.Name}”.";

            return;
        }

        Status = $"Renamed to “{name}”.";

        Rebuild();
        Select(target);
    }

    /// <summary>Deletes the selected screenshot, after asking.</summary>
    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(
            "Delete screenshot",
            $"Delete “{selected.Name}”?\n\n"
            + $"It will be moved to the {InstanceRemoval.TrashName} where that is possible, "
            + "and deleted outright where it is not.",
            "Delete",
            destructive: true).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        // Trash first, delete second -- upstream's order, and the reason the message says both.
        if (!Trash.TryTrash(selected.Path, out _) && !FileSystem.DeletePath(selected.Path))
        {
            Status = $"Could not delete “{selected.Name}”.";

            return;
        }

        Status = $"Deleted “{selected.Name}”.";

        Rebuild();
    }

    /// <summary>Whether the Upload button does anything in this build.</summary>
    /// <remarks>
    /// Needs an uploader with somewhere to send to (a configured imgur Client-ID) and something
    /// selected. Without a key the button is hidden rather than offered and then failing at 401.
    /// </remarks>
    public bool CanUpload => _uploader is { } uploader && uploader.Destination.Length != 0 && HasSelection;

    /// <summary>Uploads the selected screenshot to the configured image host.</summary>
    /// <remarks>
    /// Takes a one-item list today because the page is single-select; the shared flow already handles
    /// several and bundles them into an album, ready for a multi-select list when the UI grows one.
    /// The careful part -- ask first, name the host, warn what a screenshot can show -- lives in
    /// ScreenshotUpload, shared with nothing yet but written to be.
    /// </remarks>
    [RelayCommand]
    public async Task UploadSelectedAsync()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        Status = await ScreenshotUpload
            .RunAsync([selected.Path], _uploader, _prompts, _clipboard)
            .ConfigureAwait(true);
    }

    /// <summary>Whether the selected screenshot's location can be copied.</summary>
    public bool CanCopyPath => HasSelection;

    /// <summary>Copies the selected screenshot's file path to the clipboard.</summary>
    /// <remarks>
    /// A DELIBERATE SIMPLIFICATION of upstream's Copy File, which puts the file itself on the clipboard
    /// so it can be pasted into a file manager or a chat's attach box. The launcher's clipboard
    /// abstraction carries text, not files, so this copies the path -- useful for "where is this",
    /// an upload dialog's filename field, or a terminal. Copying the image itself (upstream's other
    /// action) needs the decoder the headless build fakes, so it is not offered.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCopyPath))]
    public async Task CopyPathSelectedAsync()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        Status = await _clipboard.SetTextAsync(selected.Path).ConfigureAwait(true)
            ? $"Copied the path of “{selected.Name}” to the clipboard."
            : "Could not copy the path: there is no clipboard.";
    }

    /// <summary>The screenshots folder, which is what "View folder" opens.</summary>
    public string FolderPath => _folder;

    /// <summary>Whether the screenshots folder can be opened in the file manager.</summary>
    public bool CanOpenFolder => _folders is not null && _folder.Length != 0;

    /// <summary>Opens the screenshots folder in the desktop's file manager.</summary>
    /// <remarks>Created first if absent, matching upstream's ensureFolderPathExists.</remarks>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    public async Task OpenFolderAsync()
    {
        if (_folders is null || _folder.Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(_folder);

        await _folders.OpenAsync(_folder).ConfigureAwait(true);
    }

    [RelayCommand]
    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        var selected = Selected?.Path;

        Screenshots.Clear();

        if (_folder.Length != 0 && Directory.Exists(_folder))
        {
            /*
             * Newest first: someone opening this page has just taken a screenshot far more often than
             * they are browsing a year of them.
             */
            foreach (var file in Directory.EnumerateFiles(_folder, "*.png")
                         .Select(f => new FileInfo(f))
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                Screenshots.Add(new ScreenshotViewModel
                {
                    Path = FileSystem.PathCombine(_folder, file.Name),

                    // Without the extension, which is what upstream shows and lets you edit.
                    Name = System.IO.Path.GetFileNameWithoutExtension(file.Name),
                    Taken = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
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
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanCopyPath));

        RenameSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        UploadSelectedCommand.NotifyCanExecuteChanged();
        CopyPathSelectedCommand.NotifyCanExecuteChanged();
    }
}
