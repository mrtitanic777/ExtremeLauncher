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
 * Ported from RecursiveFileSystemWatcher.cpp, and much shorter than it: Qt's QFileSystemWatcher does
 * not recurse, so upstream walks the tree itself and adds every directory and file by hand, re-adding
 * them whenever anything changes. .NET's FileSystemWatcher has IncludeSubdirectories, so all of that
 * disappears.
 *
 * WHAT DOES NOT DISAPPEAR IS THE FLOOD. A game writing to logs/latest.log produces a change event per
 * buffer flush -- dozens a second -- and upstream has no debounce at all, because Qt coalesces at the
 * signal level. Rescanning a directory tree that often would make the log page cost more than the game.
 *
 * So changes are COALESCED: the first one schedules a rescan, and everything arriving before it fires
 * is absorbed. THE DELAY IS INJECTED, for the same reason the dispatcher is in BatchingProgressReporter
 * -- a test that waits on a real timer is a test that fails on a loaded machine, and this port has
 * already had one round of flaky tests.
 */

namespace ExtremeLauncher.Core;

public sealed class RecursiveFileWatcher : IDisposable
{
    private readonly IPathMatcher? _matcher;

    private readonly Action<Action> _schedule;

    private readonly Lock _gate = new();

    private FileSystemWatcher? _watcher;

    private bool _pending;

    private bool _disposed;

    /// <param name="matcher">Which files count. Null watches everything.</param>
    /// <param name="schedule">
    /// Runs a rescan after a short delay. Defaults to a real timer; a test passes something it drives
    /// itself, so nothing here depends on wall-clock timing.
    /// </param>
    public RecursiveFileWatcher(IPathMatcher? matcher = null, Action<Action>? schedule = null)
    {
        _matcher = matcher;
        _schedule = schedule ?? DefaultSchedule;
    }

    /// <summary>How long changes are gathered for before a rescan.</summary>
    public static TimeSpan Debounce { get; } = TimeSpan.FromMilliseconds(250);

    private static void DefaultSchedule(Action action) => _ = DelayThen(action);

    private static async Task DelayThen(Action action)
    {
        await Task.Delay(Debounce).ConfigureAwait(false);

        action();
    }

    /// <summary>The directory being watched, or empty.</summary>
    public string Root { get; private set; } = string.Empty;

    /// <summary>Raised after a change, once the flood has settled.</summary>
    /// <remarks>
    /// Fired on a THREAD POOL thread, like FileSystemWatcher's own events. A UI caller has to marshal
    /// it, exactly as it does for launch progress.
    /// </remarks>
    public event EventHandler? FilesChanged;

    /// <summary>Starts watching a directory and everything under it.</summary>
    /// <remarks>
    /// A directory that does not exist is not an error: an instance with no logs folder yet is the
    /// normal state before its first run, and the folder appearing is exactly what the caller wants to
    /// hear about. It simply watches nothing until told a different root.
    /// </remarks>
    public void Watch(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        lock (_gate)
        {
            StopLocked();

            Root = root;

            if (_disposed || root.Length == 0 || !Directory.Exists(root))
            {
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,

                    /*
                     * FileName and DirectoryName catch a log appearing or being rotated away;
                     * LastWrite catches one growing. Size is deliberately NOT included -- it fires for
                     * the same writes as LastWrite and only doubles the flood.
                     */
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };

                _watcher.Changed += OnChanged;
                _watcher.Created += OnChanged;
                _watcher.Deleted += OnChanged;
                _watcher.Renamed += OnChanged;

                /*
                 * The buffer overflowing is itself a change: it means events were dropped, so the only
                 * safe response is to rescan. Ignoring it is how a watcher silently stops noticing.
                 */
                _watcher.Error += (_, _) => Poke();

                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A path the OS will not watch -- a network share, or one that vanished between the
                // check above and here. The caller's Refresh still works.
                _watcher = null;
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (_matcher is not null && !_matcher.Matches(Path.GetFileName(e.Name ?? string.Empty)))
        {
            return;
        }

        Poke();
    }

    /// <summary>
    /// Notes that something changed, scheduling a rescan if one is not already coming.
    /// </summary>
    /// <remarks>
    /// Public because the caller may know about a change the OS will not report -- a file it wrote
    /// itself, or a root that has just been created.
    /// </remarks>
    public void Poke()
    {
        lock (_gate)
        {
            if (_disposed || _pending)
            {
                return;
            }

            _pending = true;
        }

        _schedule(Fire);
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Cleared BEFORE the event, so a change arriving during it schedules another rescan rather
            // than being swallowed by the one already in flight.
            _pending = false;
        }

        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops watching, without forgetting the root.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;

            StopLocked();
        }
    }
}
