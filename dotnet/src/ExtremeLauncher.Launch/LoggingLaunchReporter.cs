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
 * PUTS A LAUNCH IN THE LAUNCHER'S OWN LOG, on the way to wherever it was already going.
 *
 * This is what makes the log worth having. Startup lines and a fatal error say that something went
 * wrong; what answers the question is the sequence -- which components resolved, which JVM was chosen
 * and why, what the pipeline was doing when it stopped. All of that was already being reported; it
 * simply had nowhere durable to go.
 *
 * PROGRESS IS NOT LOGGED, deliberately. A download reports progress per chunk, and a file with forty
 * thousand lines of "37 of 65" in it is not a log, it is a denial-of-service on whoever opens it. The
 * launcher's log records what HAPPENED; the window shows how far along it is.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Launch;

/// <summary>Reports to somewhere, and to the launcher's log on the way.</summary>
public sealed class LoggingLaunchReporter : ILaunchReporter
{
    private readonly ILaunchReporter _inner;

    private readonly LauncherLog _log;

    private string _lastStatus = string.Empty;

    public LoggingLaunchReporter(ILaunchReporter inner, LauncherLog log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(log);

        _inner = inner;
        _log = log;
    }

    public void Status(string status)
    {
        /*
         * COMPARED WITH THE NUMBERS TAKEN OUT, which is not the obvious rule and is the one that works.
         *
         * ConcurrentTask reports "Executing 5 task(s) (3 out of 65 are done)" -- the progress is INSIDE
         * the status text, so no two are equal and a plain repeat check drops nothing. A real dry run
         * of a 65-library instance produced 137 log lines, 130 of them that.
         *
         * Stripping digits before comparing collapses the whole run to its first line, while a status
         * that differs in any other way still gets written. A status distinguished ONLY by a number is
         * progress wearing a different hat, and progress does not belong in a log file.
         */
        if (!string.Equals(WithoutNumbers(status), _lastStatus, StringComparison.Ordinal))
        {
            _lastStatus = WithoutNumbers(status);

            _log.Info(status);
        }

        _inner.Status(status);
    }

    /// <summary>The status with every run of digits removed, for comparison only.</summary>
    private static string WithoutNumbers(string status)
    {
        Span<char> buffer = status.Length <= 256 ? stackalloc char[status.Length] : new char[status.Length];

        var length = 0;

        foreach (var c in status)
        {
            if (!char.IsAsciiDigit(c))
            {
                buffer[length++] = c;
            }
        }

        return new string(buffer[..length]);
    }

    public void Progress(long current, long total)
    {
        // Not logged. See the note at the top of this file.
        _inner.Progress(current, total);
    }

    public void Line(string text, bool isError = false)
    {
        /*
         * The game's own output goes in too. It is duplicated in the game's logs/latest.log, but only
         * while that file survives -- a crash before the game opens its log leaves this as the only
         * record, and that is exactly the crash somebody needs help with.
         */
        _log.Write(isError ? LogLevel.Error : LogLevel.Info, text);

        _inner.Line(text, isError);
    }
}
