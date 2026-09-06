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
 * Ported from ui/pages/instance/LogPage.cpp, less the log-file loading.
 *
 * THE PAGE PEOPLE ARE SENT TO WHEN SOMETHING GOES WRONG, so what it must never do is be empty when the
 * answer is right there. It shows the coordinator's own lines, which are already bounded and already
 * marshalled onto the UI thread by BatchingProgressReporter -- this page adds no plumbing of its own,
 * which is the whole reason the log lives on the coordinator rather than here.
 *
 * IT SURVIVES THE GAME EXITING. The coordinator keeps its lines after the process ends, and this page
 * shows them: the ten seconds after a crash are exactly when the log is wanted, and a page that
 * cleared itself on exit would be blank for precisely that.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ExtremeLauncher.ViewModels;

public sealed partial class LogPageViewModel : ObservableObject, IInstancePage
{
    private readonly LaunchCoordinator _launch;

    private readonly IClipboard _clipboard;

    private readonly ILogUploader? _uploader;

    private readonly IUserPrompts? _prompts;

    public LogPageViewModel(
        LaunchCoordinator launch,
        IClipboard? clipboard = null,
        ILogUploader? uploader = null,
        IUserPrompts? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(launch);

        _launch = launch;
        _clipboard = clipboard ?? NoClipboard.Instance;
        _uploader = uploader;
        _prompts = prompts;

        _launch.LogLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        _launch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LaunchCoordinator.IsBusy) or nameof(LaunchCoordinator.Status))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(Status));
            }
        };
    }

    public string Title => "Log";

    /// <summary>Always false: a log is not edited.</summary>
    public bool HasUnsavedChanges => false;

    public bool Save() => true;

    /// <summary>The lines themselves, owned by the coordinator and bounded there.</summary>
    public ObservableCollection<LaunchLogLine> Lines => _launch.LogLines;

    public bool IsRunning => _launch.IsBusy;

    public string Status => _launch.Status;

    /// <summary>
    /// Whether there is nothing to show.
    /// </summary>
    /// <remarks>
    /// Distinct from "not running": an instance that ran and exited still has its log, and that is the
    /// case this page exists for.
    /// </remarks>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>The whole log as one string, for copying into a bug report.</summary>
    /// <remarks>
    /// Built on demand rather than kept: pasting a log into a report is what people actually do with
    /// this page, and holding a second copy of a five-thousand-line log to save that is not a trade
    /// worth making.
    /// </remarks>
    public string ToPlainText() => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    /// <summary>Asks the running game to stop.</summary>
    [RelayCommand]
    public void Kill() => _launch.Cancel();

    /// <summary>What the last copy did, or empty.</summary>
    [ObservableProperty]
    private string _copyStatus = string.Empty;

    /// <summary>
    /// Copies the whole log.
    /// </summary>
    /// <remarks>
    /// The reason this page exists is that somebody is about to paste this into a bug report. Selecting
    /// five thousand lines of text by dragging is not a way to do that.
    /// </remarks>
    [RelayCommand]
    public async Task CopyAsync()
    {
        if (IsEmpty)
        {
            return;
        }

        CopyStatus = await _clipboard.SetTextAsync(ToPlainText()).ConfigureAwait(true)
            ? $"Copied {Lines.Count} lines."
            : "Could not copy: this build has no clipboard.";
    }

    /// <summary>Whether the Upload button does anything in this build.</summary>
    public bool CanUpload => _uploader is { } uploader && uploader.Destination.Length != 0 && _prompts is not null;

    /// <summary>
    /// Uploads this session's log to the configured paste service.
    /// </summary>
    /// <remarks>
    /// THE MORE USEFUL OF THE TWO UPLOAD BUTTONS, because this is the log of the launch that just went
    /// wrong -- the one somebody is looking at when they decide to go and ask for help. The other is
    /// on the instance's log FILES, which is where you go when the launcher has been closed since.
    ///
    /// The careful part -- asking first, naming the host, saying what a log contains -- is shared with
    /// that page rather than written twice.
    /// </remarks>
    [RelayCommand]
    public async Task UploadAsync()
    {
        CopyStatus = await LogUpload
            .RunAsync(ToPlainText(), "this launch's log", _uploader, _prompts, _clipboard)
            .ConfigureAwait(true);
    }
}
