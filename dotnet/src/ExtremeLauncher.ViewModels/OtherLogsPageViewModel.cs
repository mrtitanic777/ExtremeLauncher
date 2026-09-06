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
 * Ported from ui/pages/instance/OtherLogsPage.cpp.
 *
 * THE LOG PAGE SHOWS THIS SESSION; THIS ONE SHOWS YESTERDAY'S. It reads what the GAME wrote --
 * logs/latest.log, the rotated .log.gz files, and crash reports -- which is what somebody actually has
 * when the launcher was closed and reopened between the crash and the question about it.
 *
 * THE FILE PATTERNS ARE UPSTREAM'S, exactly, and they are wider than they look:
 *
 *   .*\.log(\.[0-9]*)?(\.gz)?    latest.log, 2024-01-01-1.log.gz, debug.log.3
 *   crash-.*\.txt                the game's crash reports
 *   IDMap dump.*\.txt            a 1.6-era Forge artefact
 *   ModLoader\.txt(\..*)?        older still
 *
 * The last two are for versions almost nobody runs, and are kept for the same reason "coremods" is
 * scanned for mods: the people who do run them are exactly the ones who need a launcher that can see
 * their files.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ViewModels;

/// <summary>One row: a log file the game left behind.</summary>
public sealed partial class LogFileViewModel : ObservableObject
{
    public required string Path { get; init; }

    /// <summary>The name relative to the game directory, so "logs/latest.log" reads as one.</summary>
    public required string Name { get; init; }

    public long Size { get; init; }

    public DateTimeOffset Written { get; init; }

    public string WrittenString => Written == default
        ? string.Empty
        : Written.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class OtherLogsPageViewModel : ObservableObject, IInstancePage, IDisposable
{
    private readonly Action<Action> _post;

    private readonly RecursiveFileWatcher _watcher;

    /// <param name="post">
    /// Runs an action on the UI thread. The watcher fires on a thread pool thread, and this page is
    /// bound to controls -- the same rule as launch progress. Defaults to running inline, which is
    /// right for a test.
    /// </param>
    private readonly IClipboard _clipboard;

    private readonly ILogUploader? _uploader;

    private readonly IUserPrompts? _prompts;

    public OtherLogsPageViewModel(
        Action<Action>? post = null,
        IClipboard? clipboard = null,
        ILogUploader? uploader = null,
        IUserPrompts? prompts = null)
    {
        _post = post ?? (action => action());
        _clipboard = clipboard ?? NoClipboard.Instance;
        _uploader = uploader;
        _prompts = prompts;

        _watcher = new RecursiveFileWatcher(LogMatcher);

        /*
         * Rescanned when the folder changes, so a crash report appearing while the window is open shows
         * up without anybody pressing Refresh. The watcher has already coalesced the flood a growing
         * log produces; this only has to get onto the UI thread.
         */
        _watcher.FilesChanged += (_, _) => _post(() =>
        {
            Rebuild();

            // The file being read is usually the one still being written to.
            if (Selected is { } selected)
            {
                Content = Read(selected);
            }
        });
    }

    /// <summary>Upstream's patterns, in upstream's order.</summary>
    private static readonly IPathMatcher LogMatcher = new MultiMatcher()
        .Add(new RegexpMatcher(@".*\.log(\.[0-9]*)?(\.gz)?$"))
        .Add(new RegexpMatcher(@"crash-.*\.txt"))
        .Add(new RegexpMatcher(@"IDMap dump.*\.txt$"))
        .Add(new RegexpMatcher(@"ModLoader\.txt(\..*)?$"));

    /// <summary>Upstream's cap on the file, before it is read at all.</summary>
    /// <remarks>
    /// 12 MiB. A modded game can write a log far larger than that, and putting one in a text box is how
    /// a launcher stops responding -- so it is refused with a message naming the file, which the user
    /// can then open in something built for it.
    /// </remarks>
    public const long MaxFileSize = 1024L * 1024L * 12L;

    /// <summary>Upstream's second cap, on the DECOMPRESSED text.</summary>
    /// <remarks>
    /// A 12 MiB .gz is a great deal more than 12 MiB of text, so the size check has to happen again
    /// after unzipping. Both caps are upstream's numbers.
    /// </remarks>
    public const long MaxContentLength = 50_000_000L;

    private string _gameRoot = string.Empty;

    public string Title => "Other logs";

    /// <summary>Always false: this page reads files, it does not edit them.</summary>
    public bool HasUnsavedChanges => false;

    public bool Save() => true;

    public ObservableCollection<LogFileViewModel> Files { get; } = [];

    /// <summary>The selected file's text, or the reason there is none.</summary>
    [ObservableProperty]
    private string _content = string.Empty;

    public bool IsEmpty => Files.Count == 0;

    public void Load(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);

        _gameRoot = gameRoot;

        Rebuild();

        /*
         * Watched from the GAME directory rather than logs/, because logs/ may not exist yet -- an
         * instance that has never run has no logs folder, and the folder appearing is exactly the event
         * worth hearing about.
         */
        _watcher.Watch(gameRoot);
    }

    /// <summary>What the last copy did, or empty.</summary>
    [ObservableProperty]
    private string _copyStatus = string.Empty;

    /// <summary>Copies the file being shown.</summary>
    [RelayCommand]
    public async Task CopyAsync()
    {
        if (Content.Length == 0)
        {
            return;
        }

        CopyStatus = await _clipboard.SetTextAsync(Content).ConfigureAwait(true)
            ? $"Copied {Selected?.Name ?? "the log"}."
            : "Could not copy: this build has no clipboard.";
    }

    /// <summary>Whether the Upload button does anything in this build.</summary>
    public bool CanUpload => _uploader is { } uploader && uploader.Destination.Length != 0 && _prompts is not null;

    /// <summary>Uploads the file being shown to the configured paste service.</summary>
    /// <remarks>
    /// The careful part lives in <see cref="LogUpload"/>, shared with the launch console, because a
    /// second copy of "ask before publishing somebody's personal data" is a second chance to get it
    /// wrong.
    /// </remarks>
    [RelayCommand]
    public async Task UploadAsync()
    {
        CopyStatus = await LogUpload
            .RunAsync(Content, Selected?.Name ?? "the log", _uploader, _prompts, _clipboard)
            .ConfigureAwait(true);
    }

    /// <summary>Whether the shown log can be deleted.</summary>
    /// <remarks>Needs prompts: deleting is destructive and upstream asks first, so a build with no way
    /// to ask cannot delete rather than deleting unasked.</remarks>
    public bool CanDelete => HasSelection && _prompts is not null;

    /// <summary>Deletes the selected log file, after asking.</summary>
    /// <remarks>
    /// Ported from on_btnDelete_clicked: trashed where the platform can, deleted where it cannot. A log
    /// is regenerable, so this asks once and does not offer an undo -- unlike a world.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } selected || _prompts is null)
        {
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(
            "Delete log",
            $"Delete “{selected.Name}”? It will be gone from the logs folder.",
            "Delete",
            destructive: true).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        if (!Trash.TryTrash(selected.Path, out _) && !FileSystem.DeletePath(selected.Path))
        {
            CopyStatus = $"Could not delete “{selected.Name}”. The game may still be writing to it.";

            return;
        }

        CopyStatus = $"Deleted “{selected.Name}”.";
        Content = string.Empty;

        Rebuild();
    }

    /// <summary>Whether there are logs to clean out.</summary>
    public bool CanClean => Files.Count != 0 && _prompts is not null;

    /// <summary>Deletes every log file, after asking.</summary>
    /// <remarks>
    /// Ported from on_btnClean_clicked. Each is trashed or deleted; the ones that could not be removed
    /// -- usually the log the running game still holds open -- are named rather than silently left.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanClean))]
    public async Task CleanAsync()
    {
        if (_prompts is null || Files.Count == 0)
        {
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(
            "Delete all logs",
            $"Delete all {Files.Count.ToString(CultureInfo.InvariantCulture)} log files?",
            "Delete all",
            destructive: true).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        var failed = new List<string>();

        // A copy of the paths, because Rebuild will clear the collection being iterated.
        foreach (var file in Files.ToList())
        {
            if (!Trash.TryTrash(file.Path, out _) && !FileSystem.DeletePath(file.Path))
            {
                failed.Add(file.Name);
            }
        }

        Content = string.Empty;
        Rebuild();

        CopyStatus = failed.Count == 0
            ? "Deleted all logs."
            : $"Could not delete {string.Join(", ", failed)}. The game may still be writing to them.";
    }

    /// <summary>Stops watching. Called when the instance window closes.</summary>
    public void Dispose() => _watcher.Dispose();

    public LogFileViewModel? Selected => Files.FirstOrDefault(f => f.IsSelected);

    /// <summary>Selects a file and reads it.</summary>
    [RelayCommand]
    public void Select(string? path)
    {
        foreach (var file in Files)
        {
            file.IsSelected = path is not null && file.Path == path;
        }

        Content = Selected is { } selected ? Read(selected) : string.Empty;

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDelete));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    public bool HasSelection => Selected is not null;

    [RelayCommand]
    public void Refresh()
    {
        Rebuild();

        // Re-read: the file the user is looking at is usually the one still being written to.
        if (Selected is { } selected)
        {
            Content = Read(selected);
        }
    }

    /// <summary>
    /// Reads one log file, gunzipping it when it is compressed.
    /// </summary>
    /// <remarks>
    /// Every failure returns a SENTENCE rather than throwing or returning empty. This page is what a
    /// person is looking at when something has already gone wrong, and "" is indistinguishable from an
    /// empty log.
    /// </remarks>
    private static string Read(LogFileViewModel file)
    {
        var tooBig = $"“{file.Name}” is too big to show here. "
                     + "Open it in a viewer built for large files.";

        try
        {
            // Checked before reading, so a huge file is never loaded into memory at all.
            if (new FileInfo(file.Path).Length > MaxFileSize)
            {
                return tooBig;
            }

            var raw = File.ReadAllBytes(file.Path);

            if (file.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                if (!GZip.TryUnzip(raw, out var decompressed))
                {
                    return $"“{file.Name}” could not be read. It may be truncated or not really gzipped.";
                }

                raw = decompressed;
            }

            // Again, on the DECOMPRESSED size: a 12 MiB archive is a lot more than 12 MiB of text.
            if (raw.LongLength >= MaxContentLength)
            {
                return tooBig;
            }

            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Being written to right now is the usual cause, and it is worth saying so.
            return $"“{file.Name}” could not be read: {e.Message}";
        }
    }

    private void Rebuild()
    {
        var selected = Selected?.Path;

        Files.Clear();

        if (_gameRoot.Length != 0 && Directory.Exists(_gameRoot))
        {
            /*
             * Searched RECURSIVELY from the game directory, which is what upstream's watcher does --
             * logs/ holds most of them, crash-reports/ holds the rest, and a few land at the top.
             */
            foreach (var file in Directory.EnumerateFiles(_gameRoot, "*", SearchOption.AllDirectories)
                         .Select(f => new FileInfo(f))
                         .Where(f => LogMatcher.Matches(f.Name))
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                Files.Add(new LogFileViewModel
                {
                    Path = FileSystem.CleanPath(file.FullName),

                    // Relative to the game directory, so a row reads "logs/latest.log" rather than an
                    // absolute path nobody can scan down a list of.
                    Name = FileSystem.CleanPath(System.IO.Path.GetRelativePath(_gameRoot, file.FullName)),
                    Size = file.Length,
                    Written = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                });
            }
        }

        if (selected is not null)
        {
            Select(selected);
        }

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanClean));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CleanCommand.NotifyCanExecuteChanged();
    }
}
