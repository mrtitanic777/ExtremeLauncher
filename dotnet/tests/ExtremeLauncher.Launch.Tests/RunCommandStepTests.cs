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
 * Running a pre-launch or post-exit command, against a real process.
 *
 * THE ONE THAT MATTERS is a non-zero exit failing the step: for a pre-launch command that is what
 * stops the game starting, which is the entire reason someone writes "back up my world, and if the
 * backup fails, do not let me play." The rest is substitution actually reaching the process.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class RunCommandStepTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-cmd-" + Guid.NewGuid().ToString("N"));

    public RunCommandStepTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IReadOnlyDictionary<string, string> Vars(string instanceName = "My Pack")
        => LaunchVariables.Build(instanceName, "id", "/inst", "/inst/minecraft", "/usr/bin/java", "-Xmx4G");

    /// <summary>Wraps a snippet in the platform's shell, so one test body works anywhere.</summary>
    private static string Shell(string snippet)
        => OperatingSystem.IsWindows() ? $"cmd /c \"{snippet}\"" : $"/bin/sh -c \"{snippet}\"";

    private RunCommandStep Step(string command)
        => new("Pre-Launch", command, Vars(), _temp);

    [Fact]
    public async Task ASuccessfulCommandLetsTheStepSucceed()
    {
        Assert.True(await Step(Shell("exit 0")).RunAsync());
    }

    [Fact]
    public async Task ANonZeroExitFailsTheStep()
    {
        // The load-bearing case: a failed pre-launch command aborts the launch.
        var step = Step(Shell("exit 3"));

        Assert.False(await step.RunAsync());
        Assert.Contains("code 3", step.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingExecutableFailsWithAReadableReason()
    {
        var step = Step("this-command-does-not-exist-anywhere --please");

        Assert.False(await step.RunAsync());
        Assert.Contains("couldn't be found", step.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyCommandIsANoOpRatherThanAFailure()
    {
        // A whitespace-only setting reaches the step; it must not be treated as an empty-args error.
        Assert.True(await new RunCommandStep("Pre-Launch", "   ", Vars(), _temp).RunAsync());
    }

    [Fact]
    public async Task VariablesAreSubstitutedIntoTheCommandItself()
    {
        /*
         * THE END-TO-END PROOF that substitution reaches a real process: the command writes
         * $INST_NAME to a file, and the file ends up holding the instance's name -- so the launcher
         * replaced it before the shell ever saw it.
         */
        var output = Path.Combine(_temp, "name.txt");

        var command = Shell($"echo $INST_NAME> \"{output}\"");

        var step = new RunCommandStep("Pre-Launch", command, Vars("Fabulously Optimized"), _temp);

        Assert.True(await step.RunAsync(), step.FailReason);
        Assert.Contains("Fabulously Optimized", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VariablesAreAlsoInTheProcessEnvironment()
    {
        /*
         * A script can read $INST_MC_DIR from its environment without the launcher substituting it --
         * upstream puts the variables in the process environment as well as offering substitution.
         */
        // Asserted through the forwarded log rather than a file, to sidestep shell quoting: the
        // child prints its own INST_ID, which it can only know if the launcher put it in the env.
        var command = OperatingSystem.IsWindows() ? "cmd /c set INST_ID" : "/bin/sh -c \"echo id=$INST_ID\"";

        var lines = new List<string>();

        var step = new RunCommandStep("Pre-Launch", command, Vars(), _temp);

        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync(), step.FailReason);
        Assert.Contains(lines, l => l.Contains("INST_ID=id", StringComparison.Ordinal)
                                    || l.Contains("id=id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCommandsOutputIsForwardedToTheLog()
    {
        var lines = new List<string>();

        var step = Step(Shell("echo hello from the hook"));

        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync(), step.FailReason);

        Assert.Contains(lines, l => l.Contains("hello from the hook", StringComparison.Ordinal)
                                    || l.Contains("Pre-Launch command", StringComparison.Ordinal));
    }
}
