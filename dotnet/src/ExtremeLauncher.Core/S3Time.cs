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
 * Ported from launcher/minecraft/ParseUtils.{h,cpp}.
 *
 * LIVES IN CORE, not with the rest of the Minecraft code, because upstream's java/JavaMetadata.cpp
 * includes minecraft/ParseUtils.h to read release timestamps -- a layering inversion that C++ headers
 * tolerate and C# projects do not. These are pure string-to-date functions with no Minecraft in them,
 * so Core is where they belong; ExtremeLauncher.Minecraft.ParseUtils forwards to them so every
 * existing call site and the ported ParseUtils_test.cpp assertions are untouched.
 *
 * Mojang's version metadata carries ISO 8601 timestamps with genuine, non-round UTC offsets --
 * "+00:01", "+09:22", "-05:33" all appear in the upstream test vector. The launcher writes these
 * back out verbatim, so the offset must be PRESERVED rather than normalised to UTC. That is the whole
 * reason this file exists; upstream's own comment is "this all because Qt can't format timestamps
 * right".
 *
 * QUIRK preserved: sub-minute offset seconds are silently dropped, since the output format is only
 * +HH:MM. No real timezone has them, and the upstream vector has none.
 */

using System.Globalization;

namespace ExtremeLauncher.Core;

public static class S3Time
{
    /// <summary>Parses an ISO 8601 timestamp, keeping its UTC offset intact.</summary>
    public static DateTimeOffset TimeFromS3Time(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);

    public static bool TryTimeFromS3Time(string value, out DateTimeOffset result)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

    /// <summary>Formats a timestamp as <c>yyyy-MM-ddTHH:mm:ss±HH:mm</c>, preserving the offset.</summary>
    public static string TimeToS3Time(DateTimeOffset time)
    {
        var offset = time.Offset;
        var negative = offset < TimeSpan.Zero;
        var absolute = offset.Duration();

        // Hours must come from TotalHours: TimeSpan.Hours alone would wrap past 24, and offsets
        // never exceed 14 hours in practice but the arithmetic should not depend on that.
        var hours = (int)absolute.TotalHours;
        var minutes = absolute.Minutes;

        return time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
               + (negative ? '-' : '+')
               + hours.ToString("00", CultureInfo.InvariantCulture)
               + ':'
               + minutes.ToString("00", CultureInfo.InvariantCulture);
    }
}
