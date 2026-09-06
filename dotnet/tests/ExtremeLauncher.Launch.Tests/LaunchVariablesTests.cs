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
 * The $INST_* variables a pre-launch or post-exit command can use.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LaunchVariablesTests
{
    private static IReadOnlyDictionary<string, string> Vars()
        => LaunchVariables.Build(
            instanceName: "My Pack",
            instanceId: "mypack1",
            instanceRoot: "/instances/mypack1",
            gameRoot: "/instances/mypack1/minecraft",
            javaPath: "/usr/bin/java",
            javaArguments: "-Xmx4G -XX:+UseZGC");

    [Fact]
    public void TheMapCarriesUpstreamsVariables()
    {
        var vars = Vars();

        Assert.Equal("My Pack", vars["INST_NAME"]);
        Assert.Equal("mypack1", vars["INST_ID"]);
        Assert.Equal("/usr/bin/java", vars["INST_JAVA"]);
        Assert.Equal("-Xmx4G -XX:+UseZGC", vars["INST_JAVA_ARGS"]);

        // NO_COLOR is upstream's, so a command whose output is piped into the log does not emit ANSI
        // escapes with nowhere to render.
        Assert.Equal("1", vars["NO_COLOR"]);
    }

    [Fact]
    public void PathsAreMadeAbsolute()
    {
        // Upstream hands commands absolute, native paths -- a relative one would resolve against
        // whatever the command's own working directory turned out to be.
        var vars = Vars();

        Assert.True(Path.IsPathRooted(vars["INST_DIR"]));
        Assert.True(Path.IsPathRooted(vars["INST_MC_DIR"]));
    }

    [Fact]
    public void SubstitutionReplacesEveryOccurrence()
    {
        var result = LaunchVariables.Substitute("echo $INST_NAME in $INST_NAME", Vars());

        Assert.Equal("echo My Pack in My Pack", result);
    }

    [Fact]
    public void TheLongerKeyWinsWhereOneIsAPrefixOfAnother()
    {
        /*
         * THE UPSTREAM BUG THIS PORT DOES NOT REPRODUCE. "INST_JAVA" is a prefix of "INST_JAVA_ARGS",
         * and upstream substitutes in sorted-key order, so it replaces $INST_JAVA first and turns
         * "$INST_JAVA_ARGS" into "<the java path>_ARGS" before the longer key is ever tried.
         *
         * Substituting longest-key-first is a one-line fix and nobody could want the mangled result,
         * so this asserts the whole variable comes through intact.
         */
        var result = LaunchVariables.Substitute("$INST_JAVA_ARGS", Vars());

        Assert.Equal("-Xmx4G -XX:+UseZGC", result);

        // And $INST_JAVA on its own still resolves to the path, not left behind.
        Assert.Equal("/usr/bin/java", LaunchVariables.Substitute("$INST_JAVA", Vars()));
    }

    [Fact]
    public void AnUnknownVariableIsLeftUntouched()
    {
        // Not every "$" is ours. A command that legitimately contains $HOME or a shell variable must
        // come out the other side unchanged.
        Assert.Equal("echo $HOME", LaunchVariables.Substitute("echo $HOME", Vars()));
    }
}
