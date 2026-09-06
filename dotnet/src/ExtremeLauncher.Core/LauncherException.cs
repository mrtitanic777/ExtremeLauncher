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
 * Ported from launcher/Exception.h.
 *
 * Renamed from the upstream `Exception` to avoid shadowing System.Exception. The upstream constructor
 * also writes to qCritical(); that side effect is dropped here -- logging belongs to the logging
 * layer, not to exception construction.
 */

namespace ExtremeLauncher.Core;

public class LauncherException : Exception
{
    public LauncherException(string message) : base(message)
    {
    }

    public LauncherException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Upstream spelling of <see cref="Exception.Message"/>.</summary>
    public string Cause => Message;
}
