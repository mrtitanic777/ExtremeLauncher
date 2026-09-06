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
 * Ported from launcher/minecraft/Agent.h.
 */

namespace ExtremeLauncher.Minecraft;

/// <summary>
/// A Java agent to attach at launch, becoming a <c>-javaagent:&lt;jar&gt;=&lt;argument&gt;</c> flag.
/// </summary>
public sealed class Agent
{
    public Agent(Library library, string argument = "")
    {
        Library = library;
        Argument = argument;
    }

    /// <summary>The jar containing the agent.</summary>
    public Library Library { get; }

    /// <summary>Passed after an <c>=</c> when non-empty.</summary>
    public string Argument { get; }

    public override string ToString()
        => Argument.Length != 0 ? $"{Library.Name.Serialize()}={Argument}" : Library.Name.Serialize();
}
