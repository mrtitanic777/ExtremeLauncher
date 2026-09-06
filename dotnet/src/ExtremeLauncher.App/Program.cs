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
 * The entry point, replacing launcher/main.cpp.
 *
 * A GUI PROCESS HAS NOWHERE TO PRINT. On Windows this is a WinExe: there is no console, so an
 * unhandled exception does not produce a stack trace anywhere a person can find -- the launcher simply
 * vanishes. Upstream attaches the parent console when one exists (AttachWindowsConsole) for exactly
 * this reason, and still writes a log.
 *
 * So every path out of the process is caught and written to a file next to the executable. This is not
 * diagnostics scaffolding: "it disappeared and I don't know why" is the single least actionable bug
 * report a launcher can generate, and it is what this build did before the handler existed.
 */

using System.Globalization;
using Avalonia;

namespace ExtremeLauncher.App;

internal static class Program
{
    /*
     * STA and no synchronisation context before AppMain, which Avalonia requires: anything touching a
     * window before the toolkit is initialised fails in a way that is hard to read.
     */
    [STAThread]
    public static int Main(string[] args)
    {
        /*
         * Caught here as well as around AppMain: an exception thrown on a thread pool thread does not
         * pass through the try below, and that is precisely where a launch runs.
         */
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog(e.ExceptionObject as Exception, "unhandled exception");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception, "unobserved task exception");

            // Observed, so it does not also tear the process down on an older runtime policy.
            e.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            WriteCrashLog(e, "fatal");
            return 1;
        }
    }

    /// <summary>Also used by the Avalonia designer, which calls it by name.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Appends a failure to <c>crash.log</c> beside the executable.
    /// </summary>
    /// <remarks>
    /// APPENDED, not overwritten: a crash on startup followed by a crash on the retry would otherwise
    /// leave only the second, and the first is usually the one that explains the second.
    ///
    /// Beside the EXECUTABLE rather than in the data directory, because the data directory may be the
    /// thing that could not be opened.
    /// </remarks>
    private static void WriteCrashLog(Exception? exception, string kind)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            var when = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            File.AppendAllText(
                path,
                $"""

                ==== {when}  {kind} ====
                {exception?.ToString() ?? "(no exception object)"}

                """);
        }
        catch (IOException)
        {
            // Nothing useful is left to do: the reporting of a failure has itself failed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
