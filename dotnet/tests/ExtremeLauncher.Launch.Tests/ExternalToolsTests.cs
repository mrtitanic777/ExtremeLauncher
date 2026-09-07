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
 * For the pure parts of the external-tool ports (tools/{MCEditTool,JProfiler,JVisualVM}): the path
 * validators, the OS-specific program-path resolution, and the argument builders. Path checks run
 * against hand-built directories; the OS resolvers are exercised for every platform by passing the
 * ToolPlatform explicitly, which is exactly why they take one.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ExternalToolsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-tools-" + Guid.NewGuid().ToString("N"));

    public ExternalToolsTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_temp, name);
        Directory.CreateDirectory(path);

        return path;
    }

    private static void Touch(string dir, string name) => File.WriteAllText(Path.Combine(dir, name), string.Empty);

    // ================================================================== MCEdit

    [Fact]
    public void McEditCheckRejectsAnEmptyPath()
    {
        Assert.False(McEditTool.Check(string.Empty, out var error));
        Assert.Equal("Path is empty", error);
    }

    [Fact]
    public void McEditCheckRejectsAMissingPath()
    {
        Assert.False(McEditTool.Check(Path.Combine(_temp, "nope"), out var error));
        Assert.Equal("Path does not exist", error);
    }

    [Fact]
    public void McEditCheckRejectsADirectoryWithoutAnyMarker()
    {
        var dir = Dir("empty-mcedit");

        Assert.False(McEditTool.Check(dir, out var error));
        Assert.Equal("Path does not seem to be a MCEdit path", error);
    }

    [Theory]
    [InlineData("mcedit.sh")]
    [InlineData("mcedit.py")]
    [InlineData("mcedit.exe")]
    [InlineData("mcedit2.exe")]
    public void McEditCheckAcceptsAnyKnownMarker(string marker)
    {
        var dir = Dir("mcedit-" + marker);
        Touch(dir, marker);

        Assert.True(McEditTool.Check(dir, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void McEditCheckAcceptsAMacContentsDirectory()
    {
        var dir = Dir("mcedit-mac");
        Directory.CreateDirectory(Path.Combine(dir, "Contents"));

        Assert.True(McEditTool.Check(dir, out _));
    }

    [Fact]
    public void McEditProgramPathIsTheInstallItselfOnMac()
    {
        var dir = Dir("mcedit-macapp");

        Assert.Equal(dir, McEditTool.GetProgramPath(dir, ToolPlatform.MacOs));
    }

    [Fact]
    public void McEditProgramPathPrefersTheShellScriptOnLinux()
    {
        var dir = Dir("mcedit-linux");
        Touch(dir, "mcedit.sh");
        Touch(dir, "mcedit.py");

        Assert.Equal(Path.Combine(dir, "mcedit.sh"), McEditTool.GetProgramPath(dir, ToolPlatform.Linux));
    }

    [Fact]
    public void McEditProgramPathFallsBackToPythonOnLinux()
    {
        var dir = Dir("mcedit-linux-py");
        Touch(dir, "mcedit.py");

        Assert.Equal(Path.Combine(dir, "mcedit.py"), McEditTool.GetProgramPath(dir, ToolPlatform.Linux));
    }

    [Fact]
    public void McEditProgramPathPrefersMceditExeOnWindows()
    {
        var dir = Dir("mcedit-win");
        Touch(dir, "mcedit.exe");
        Touch(dir, "mcedit2.exe");

        Assert.Equal(Path.Combine(dir, "mcedit.exe"), McEditTool.GetProgramPath(dir, ToolPlatform.Windows));
    }

    [Fact]
    public void McEditProgramPathIsEmptyWhenNothingRunnableIsPresent()
    {
        var dir = Dir("mcedit-none");
        Directory.CreateDirectory(Path.Combine(dir, "Contents"));

        // "Contents" validates the install but is not a runnable file on Linux or Windows.
        Assert.Equal(string.Empty, McEditTool.GetProgramPath(dir, ToolPlatform.Linux));
        Assert.Equal(string.Empty, McEditTool.GetProgramPath(dir, ToolPlatform.Windows));
    }

    // ================================================================== JProfiler

    [Fact]
    public void JProfilerCheckNeedsBinBinaryAndAgent()
    {
        var dir = Dir("jprofiler");
        var bin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(bin);
        Touch(bin, "jprofiler");
        Touch(bin, "agent.jar");

        Assert.True(JProfilerTool.Check(dir));
    }

    [Fact]
    public void JProfilerCheckAcceptsTheWindowsBinaryName()
    {
        var dir = Dir("jprofiler-win");
        var bin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(bin);
        Touch(bin, "jprofiler.exe");
        Touch(bin, "agent.jar");

        Assert.True(JProfilerTool.Check(dir));
    }

    [Fact]
    public void JProfilerCheckFailsWithoutTheAgent()
    {
        var dir = Dir("jprofiler-noagent");
        var bin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(bin);
        Touch(bin, "jprofiler");

        Assert.False(JProfilerTool.Check(dir));
    }

    [Fact]
    public void JProfilerCheckFailsOnEmptyOrMissingPath()
    {
        Assert.False(JProfilerTool.Check(string.Empty));
        Assert.False(JProfilerTool.Check(Path.Combine(_temp, "nope")));
    }

    [Fact]
    public void JProfilerProgramPathIsOsSpecific()
    {
        Assert.Equal(Path.Combine("/opt/jp", "bin", "jpenable"), JProfilerTool.ProgramPath("/opt/jp", ToolPlatform.Linux));
        Assert.Equal(Path.Combine("/opt/jp", "bin", "jpenable.exe"), JProfilerTool.ProgramPath("/opt/jp", ToolPlatform.Windows));
    }

    [Fact]
    public void JProfilerArgumentsAttachToThePidOnThePort()
        => Assert.Equal(["-d", "1234", "--gui", "-p", "42042"], JProfilerTool.BuildArguments(1234, 42042));

    // ================================================================== VisualVM

    [Fact]
    public void JVisualVmCheckAcceptsAnExecutableNamedForVisualVm()
    {
        var dir = Dir("visualvm");
        Touch(dir, "jvisualvm.exe");

        Assert.True(JVisualVmTool.Check(Path.Combine(dir, "jvisualvm.exe"), ToolPlatform.Windows));
    }

    [Fact]
    public void JVisualVmCheckRejectsAnExecutableWithTheWrongName()
    {
        var dir = Dir("visualvm-wrongname");
        Touch(dir, "editor.exe");

        Assert.False(JVisualVmTool.Check(Path.Combine(dir, "editor.exe"), ToolPlatform.Windows));
    }

    [Fact]
    public void JVisualVmCheckRejectsANonExecutableOnWindows()
    {
        var dir = Dir("visualvm-notexe");
        Touch(dir, "visualvm.txt");

        Assert.False(JVisualVmTool.Check(Path.Combine(dir, "visualvm.txt"), ToolPlatform.Windows));
    }

    [Fact]
    public void JVisualVmCheckFailsOnEmptyOrMissingPath()
    {
        Assert.False(JVisualVmTool.Check(string.Empty, ToolPlatform.Windows));
        Assert.False(JVisualVmTool.Check(Path.Combine(_temp, "nope.exe"), ToolPlatform.Windows));
    }

    [Fact]
    public void JVisualVmArgumentsOpenThePid()
        => Assert.Equal(["--openpid", "9999"], JVisualVmTool.BuildArguments(9999));
}
