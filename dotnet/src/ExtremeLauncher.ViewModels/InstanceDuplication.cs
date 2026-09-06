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
 * COPYING AN INSTANCE IS HOW PEOPLE EXPERIMENT SAFELY. Someone about to add forty mods to a world they
 * care about copies it first, and that copy is the thing standing between them and losing it.
 *
 * The work itself is InstanceCopyTask, which was ported in wave 7 with its three strategies (clone,
 * hard link, plain copy) and its tests. What is here is only the flow around it: ask for a name, build
 * in a staging directory, commit, and re-read the list.
 *
 * IT BUILDS IN STAGING AND COMMITS, rather than writing into the instances folder directly. That is
 * upstream's arrangement and the reason is failure: a copy that dies half way leaves a directory that
 * looks like an instance and is not. Staging means the instances folder only ever sees the finished
 * thing.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

/// <summary>What the copy dialog came back with: the new name and which parts to bring across.</summary>
public sealed record InstanceCopyChoice(string Name, InstanceCopyPrefs Prefs);

/// <summary>Asks for a copy's name and options. Implemented by the app's copy dialog.</summary>
public interface IInstanceCopyPrompt
{
    /// <returns>The choice, or null when the dialog was cancelled.</returns>
    Task<InstanceCopyChoice?> AskAsync(string sourceName);
}

/*
 * IT IS ITS OWN PROGRESS SINK. The copy runs off the UI thread, so InstanceCopyTask's status and
 * progress events arrive on worker threads -- and this object is bound to controls. Rather than a
 * second mechanism, it reuses the one launching already uses: BatchingProgressReporter takes the
 * events, coalesces bursts, and calls back on the UI thread.
 *
 * That reporter was called BatchingLaunchReporter until this needed it, which is what "generalisable,
 * and not generalised" turns into if left alone.
 */
public sealed partial class InstanceDuplication : ObservableObject, IProgressSink
{
    private readonly IUserPrompts _prompts;

    private readonly Action<Action> _post;

    private readonly IInstanceCopyPrompt? _copyPrompt;

    /// <param name="post">
    /// Runs an action on the UI thread. Defaults to running inline, which is right for a test or a
    /// headless caller: there is no UI thread to get onto.
    /// </param>
    /// <param name="copyPrompt">
    /// The copy dialog, which asks for a name AND which parts to bring across. Null falls back to a
    /// plain name prompt that copies everything -- the behaviour before the dialog existed.
    /// </param>
    public InstanceDuplication(
        IUserPrompts? prompts = null, Action<Action>? post = null, IInstanceCopyPrompt? copyPrompt = null)
    {
        _prompts = prompts ?? RefusingPrompts.Instance;
        _post = post ?? (action => action());
        _copyPrompt = copyPrompt;
    }

    /// <summary>Whether a copy is under way.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>What the last copy did, for the window to report.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>0 to 1, or null when the work has no known total.</summary>
    [ObservableProperty]
    private double? _progress;

    void IProgressSink.SetStatus(string status) => Status = status;

    void IProgressSink.SetProgress(long current, long total)
        => Progress = total > 0 ? Math.Clamp((double)current / total, 0, 1) : null;

    /*
     * The copy's log lines are DROPPED. There is nowhere to show them yet, and the interesting one --
     * why it failed -- comes back through FailReason instead. Named here so it reads as a decision
     * rather than an omission.
     */
    void IProgressSink.Log(string line, bool isError)
    {
    }

    /// <summary>
    /// Copies an instance, asking for a name first.
    /// </summary>
    /// <returns>The id of the copy, or empty when nothing was made.</returns>
    public async Task<string> CopyAsync(InstanceList list, string sourceId, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (IsBusy || list.GetInstanceById(sourceId) is not { } source)
        {
            return string.Empty;
        }

        /*
         * The dialog asks for a name AND which parts to copy; without one, a plain name prompt does,
         * and everything is copied. Either way the name is PREFILLED with the original -- odd next to
         * the obvious "Copy of X", but a name is what most people change first, so the field is a
         * starting point rather than something to delete. Two instances may share a display name; only
         * the directory has to be unique, and DirNameFromString sees to that at commit time.
         */
        string name;
        InstanceCopyPrefs prefs;

        if (_copyPrompt is not null)
        {
            var choice = await _copyPrompt.AskAsync(sourceName).ConfigureAwait(true);

            if (choice is null)
            {
                return string.Empty;
            }

            name = choice.Name.Trim();
            prefs = choice.Prefs;
        }
        else
        {
            var chosen = await _prompts.PromptForTextAsync(
                "Copy instance",
                $"Name for the copy of “{sourceName}”:",
                sourceName).ConfigureAwait(true);

            if (chosen is null)
            {
                return string.Empty;
            }

            name = chosen.Trim();
            prefs = new InstanceCopyPrefs();
        }

        if (name.Length == 0)
        {
            // An empty name would become an empty directory name, which is not a thing.
            Status = "A copy needs a name.";

            return string.Empty;
        }

        IsBusy = true;
        Progress = null;
        Status = $"Copying “{sourceName}”…";

        try
        {
            var copy = new InstanceCopyTask(source.Paths.InstanceRoot, string.Empty, prefs, name)
            {
                // The copy lands beside the original rather than loose at the top of the list.
                Group = list.GetInstanceGroup(sourceId),
            };

            var staging = new InstanceStagingTask(list, copy, copy);

            /*
             * Both the wrapper's status ("Committing instance") and the copy's own progress. Subscribed
             * before the run, and everything goes through the reporter rather than straight onto these
             * properties, because these events fire on whatever thread is copying files.
             */
            using var reporter = new BatchingProgressReporter(this, _post);

            staging.StatusChanged += (_, status) => reporter.Status(status);
            staging.ProgressChanged += (_, p) => reporter.Progress(p.Current, p.Total);

            copy.StatusChanged += (_, status) => reporter.Status(status);
            copy.ProgressChanged += (_, p) => reporter.Progress(p.Current, p.Total);

            /*
             * Off the UI thread. A plain copy of a large modpack is gigabytes of file I/O, and
             * LauncherTask is not async all the way down -- awaiting it directly would freeze the
             * window for the duration, which is exactly what AppLauncher avoids for launches.
             */
            var succeeded = await Task.Run(() => staging.RunAsync()).ConfigureAwait(true);

            // Anything still buffered, before the status below replaces it.
            reporter.Flush();

            if (!succeeded)
            {
                Status = staging.FailReason.Length != 0
                    ? $"Could not copy “{sourceName}”: {staging.FailReason}"
                    : $"Could not copy “{sourceName}”.";

                return string.Empty;
            }

            Status = $"Copied “{sourceName}” to “{name}”.";

            /*
             * Reported by the commit rather than guessed at. The id is the DIRECTORY name it settled
             * on, which is not the display name when something already occupied it -- DirNameFromString
             * appends "(1)". An earlier version searched the list for an instance with a matching name
             * and took the longest id, which is a guess that picks the wrong instance as soon as two
             * of them share a name.
             */
            return staging.CommittedId;
        }
        finally
        {
            IsBusy = false;
            Progress = null;
        }
    }
}
