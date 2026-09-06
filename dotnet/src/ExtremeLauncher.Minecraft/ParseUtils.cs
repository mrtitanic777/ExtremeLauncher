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
 * The implementation moved to Core/S3Time.cs so the Java subsystem can reach it without depending on
 * Minecraft -- see the header there. This forwards, so call sites and the ported tests are unchanged.
 */

namespace ExtremeLauncher.Minecraft;

public static class ParseUtils
{
    /// <inheritdoc cref="Core.S3Time.TimeFromS3Time"/>
    public static DateTimeOffset TimeFromS3Time(string value) => Core.S3Time.TimeFromS3Time(value);

    /// <inheritdoc cref="Core.S3Time.TryTimeFromS3Time"/>
    public static bool TryTimeFromS3Time(string value, out DateTimeOffset result)
        => Core.S3Time.TryTimeFromS3Time(value, out result);

    /// <inheritdoc cref="Core.S3Time.TimeToS3Time"/>
    public static string TimeToS3Time(DateTimeOffset time) => Core.S3Time.TimeToS3Time(time);
}
