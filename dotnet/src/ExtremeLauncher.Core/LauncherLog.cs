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
 * Ported from the logger block of Application.cpp and its appDebugOutput handler.
 *
 * THE LAUNCHER'S OWN LOG, which this port did not have. The game's logs survive a crash because the
 * game writes them; the launcher's view of a run -- which JVM was chosen, what the pipeline did, why a
 * download failed -- lived only in a window and died with the process.
 *
 * That is the difference between a bug report saying "it wouldn't start" and one that can be answered.
 *
 * FIVE FILES, ROTATED ON EVERY START, which is upstream's scheme exactly:
 *
 *     logs/ExtremeLauncher-0.log   this run
 *     logs/ExtremeLauncher-1.log   the run before
 *     ...
 *     logs/ExtremeLauncher-4.log   dropped when the next start rotates
 *
 * Rotated at START rather than by size or date, because the question is nearly always "what happened
 * the last time I ran it", and a run is the unit people think in.
 *
 * FLUSHED ON EVERY LINE. A launcher that crashes with its last few lines still in a buffer has written
 * a log that stops just before the interesting part -- which is worse than no log, because it looks
 * complete.
 */

using System.Globalization;
using System.Text;

namespace ExtremeLauncher.Core;

/// <summary>How serious a line is.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed class LauncherLog : IDisposable
{
    /// <summary>How many runs are kept, including the current one.</summary>
    public const int KeptRuns = 5;

    private readonly Lock _gate = new();

    private readonly TextWriter? _file;

    private readonly TextWriter? _console;

    private bool _disposed;

    private LauncherLog(TextWriter? file, TextWriter? console)
    {
        _file = file;
        _console = console;
    }

    /// <summary>
    /// Opens the log for this run, rotating the previous ones.
    /// </summary>
    /// <remarks>
    /// A LOG THAT CANNOT BE OPENED IS NOT FATAL HERE. Upstream refuses to start at all when the data
    /// folder is not writable, which is defensible for it -- everything else it does needs to write
    /// too. This returns a log that only echoes to the console instead, because the caller may be the
    /// CLI reading somebody else's read-only install, and refusing to run would be a worse answer than
    /// running without a log.
    /// </remarks>
    /// <param name="dataRoot">The launcher's data directory.</param>
    /// <param name="console">Where to echo, or null for a file only.</param>
    public static LauncherLog Open(string dataRoot, TextWriter? console = null)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var directory = FileSystem.PathCombine(dataRoot, "logs");

        try
        {
            Directory.CreateDirectory(directory);

            Rotate(directory);

            /*
             * Shared for reading, so the Other logs page can show this file WHILE it is being written.
             * Without FileShare.Read, a user looking at the launcher's own log sees an error instead.
             */
            var stream = new FileStream(
                Path_(directory, 0),
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);

            return new LauncherLog(new StreamWriter(stream, new UTF8Encoding(false)), console)
            {
                Path = Path_(directory, 0),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            console?.WriteLine($"warning: could not open a log file in {directory}. Continuing without one.");

            return new LauncherLog(file: null, console);
        }
    }

    /// <summary>Where this run's log is, or empty when there is not one.</summary>
    public string Path { get; private init; } = string.Empty;

    /// <summary>Whether anything is actually being written to disk.</summary>
    public bool IsWritingToFile => _file is not null;

    private static string Path_(string directory, int index)
        => FileSystem.PathCombine(
            directory,
            $"{BuildConfig.Instance.LauncherName}-{index.ToString(CultureInfo.InvariantCulture)}.log");

    /// <summary>
    /// Shifts each run's log one place older, dropping the oldest.
    /// </summary>
    /// <remarks>
    /// Walked DOWNWARDS -- 3 becomes 4, then 2 becomes 3 -- because going the other way would overwrite
    /// each file with the one before it and leave five copies of the same run.
    /// </remarks>
    private static void Rotate(string directory)
    {
        for (var i = KeptRuns - 1; i > 0; i--)
        {
            var from = Path_(directory, i - 1);
            var to = Path_(directory, i);

            if (!File.Exists(from))
            {
                continue;
            }

            try
            {
                File.Move(from, to, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A previous run's log held open by something. Losing an old log is not worth failing
                // the rotation for; this run still gets its own.
            }
        }
    }

    /// <summary>Writes one line.</summary>
    public void Write(LogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{DateTime.Now:HH:mm:ss}] [{Describe(level)}] {message}");

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _console?.WriteLine(line);

            if (_file is null)
            {
                return;
            }

            _file.WriteLine(line);

            /*
             * Flushed every line. A launcher that crashes with its last few lines still buffered has
             * written a log that stops just before the interesting part -- worse than no log, because
             * it looks complete.
             */
            _file.Flush();
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warning(string message) => Write(LogLevel.Warning, message);

    public void Error(string message) => Write(LogLevel.Error, message);

    private static string Describe(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _file?.Flush();
            _file?.Dispose();
        }
    }
}
