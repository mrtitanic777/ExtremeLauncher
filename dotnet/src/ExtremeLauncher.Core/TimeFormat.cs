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
 * Ported from launcher/MMCTime.{h,cpp}.
 *
 * RENAMED from Time, which collides with far too much and said nothing about what it does.
 *
 * TWO FORMATTERS THAT LOOK LIKE ONE. PrettifyDuration reports how long someone has played -- always
 * exactly two units, largest first, and never seconds once there are hours. HumanReadableDuration
 * reports how long an operation took -- every non-zero unit, down to milliseconds. They read
 * similarly and answer different questions, which is why they are both here rather than merged.
 */

using System.Globalization;
using System.Text;

namespace ExtremeLauncher.Core;

public static class TimeFormat
{
    /// <summary>
    /// Formats a play time, in seconds.
    /// </summary>
    /// <param name="noDays">Report hours past 24 rather than rolling them into days.</param>
    /// <remarks>
    /// ALWAYS TWO UNITS, and the pair shifts as the duration grows: minutes and seconds, then hours
    /// and minutes, then days, hours and minutes. Seconds disappear once there is an hour to report,
    /// which is the point -- nobody reads "14h 3min 22s" for a play time.
    /// </remarks>
    public static string PrettifyDuration(long duration, bool noDays = false)
    {
        var seconds = (int)(duration % 60);
        duration /= 60;

        var minutes = (int)(duration % 60);
        duration /= 60;

        var hours = (int)(noDays ? duration : duration % 24);
        var days = (int)(noDays ? 0 : duration / 24);

        if (hours == 0 && days == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes}min {seconds}s");
        }

        if (days == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hours}h {minutes}min");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{days}d {hours}h {minutes}min");
    }

    /// <summary>
    /// Formats an elapsed time, in seconds, for a log or a progress report.
    /// </summary>
    /// <param name="precision">
    /// Ignored beyond "is it more than zero". Upstream passes it to a real-number precision setting
    /// that never applies, because the value printed is an integer count of milliseconds -- so its
    /// only effect is whether milliseconds appear at all. Kept with that meaning rather than a
    /// pretence of decimal places that were never produced.
    /// </param>
    /// <remarks>
    /// EVERY NON-ZERO UNIT, skipping the ones that are zero -- so 3661 seconds is "1h 1m 1s" and 3600
    /// is "1h", with no run of zeroes between. A duration that rounds to nothing still prints "0ms",
    /// because an empty string would read as a missing measurement rather than an instant one.
    /// </remarks>
    public static string HumanReadableDuration(double duration, int precision = 0)
    {
        var builder = new StringBuilder();

        if (duration < 0)
        {
            builder.Append('-');
            duration = -duration;
        }

        var remaining = TimeSpan.FromSeconds(duration);

        var days = remaining.Days;
        var hours = remaining.Hours;
        var minutes = remaining.Minutes;
        var seconds = remaining.Seconds;
        var milliseconds = remaining.Milliseconds;

        if (days != 0)
        {
            builder.Append(days.ToString(CultureInfo.InvariantCulture)).Append("days");
        }

        Append(builder, hours, "h", days != 0);
        Append(builder, minutes, "m", days != 0 || hours != 0);

        // Seconds print whenever anything larger did, so "1h 0m 0s" cannot happen but "1h 1s" can.
        if (days != 0 || hours != 0 || minutes != 0 || seconds != 0)
        {
            AppendAlways(builder, seconds, "s", days != 0 || hours != 0 || minutes != 0);
        }

        var anything = days != 0 || hours != 0 || minutes != 0 || seconds != 0;

        // The "0ms" case: nothing above registered, so something has to be said.
        if ((milliseconds != 0 && precision > 0) || !anything)
        {
            AppendAlways(builder, milliseconds, "ms", anything);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, int value, string unit, bool needsSpace)
    {
        if (value == 0)
        {
            return;
        }

        AppendAlways(builder, value, unit, needsSpace);
    }

    private static void AppendAlways(StringBuilder builder, int value, string unit, bool needsSpace)
    {
        if (needsSpace)
        {
            builder.Append(' ');
        }

        builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append(unit);
    }
}
