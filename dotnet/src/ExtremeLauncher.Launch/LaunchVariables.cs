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
 * Ported from MinecraftInstance::getVariables and LaunchTask::substituteVariables.
 *
 * THE $INST_* VARIABLES a pre-launch or post-exit command can use. Somebody's backup script needs to
 * know which instance is launching and where its files are, and these are how it finds out -- both by
 * $-substitution into the command line and as environment variables on the process.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Launch;

public static class LaunchVariables
{
    /// <summary>Builds the variable map upstream's getVariables exposes.</summary>
    /// <remarks>
    /// NO_COLOR is upstream's, and worth keeping: a command whose output is piped into the launcher's
    /// log has nowhere to render ANSI colour, so telling it not to emit any keeps the log readable.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Build(
        string instanceName,
        string instanceId,
        string instanceRoot,
        string gameRoot,
        string javaPath,
        string javaArguments)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["INST_NAME"] = instanceName,
            ["INST_ID"] = instanceId,
            ["INST_DIR"] = FileSystem.AbsolutePath(instanceRoot),
            ["INST_MC_DIR"] = FileSystem.AbsolutePath(gameRoot),
            ["INST_JAVA"] = javaPath,
            ["INST_JAVA_ARGS"] = javaArguments,
            ["NO_COLOR"] = "1",
        };

    /// <summary>Replaces every <c>$KEY</c> in <paramref name="command"/> with its value.</summary>
    /// <remarks>
    /// LONGEST KEY FIRST, which is a deliberate DIVERGENCE from upstream and a bug fix. Upstream
    /// iterates the keys in the environment's own order — sorted, so "INST_JAVA" comes before
    /// "INST_JAVA_ARGS" — and replaces "$INST_JAVA" first, which turns "$INST_JAVA_ARGS" into
    /// "&lt;the java path&gt;_ARGS" before the longer key is ever seen. No test pins that, nobody
    /// could want it, and the fix costs one sort: replace the longer key before any key it is a
    /// prefix of.
    /// </remarks>
    public static string Substitute(string command, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(variables);

        foreach (var key in variables.Keys.OrderByDescending(k => k.Length))
        {
            command = command.Replace("$" + key, variables[key], StringComparison.Ordinal);
        }

        return command;
    }
}
