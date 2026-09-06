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
 * Writing a shortcut that starts one instance.
 *
 * THE PLATFORM TESTS ARE SKIPPED RATHER THAN FAKED. A .desktop file and a .lnk have nothing in
 * common, and a "shortcut helper" abstraction that let all three be tested everywhere would be
 * testing the abstraction. Each platform's test runs where that platform is.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class ShortcutsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-lnk-" + Guid.NewGuid().ToString("N"));

    private readonly string _target;

    public ShortcutsTests()
    {
        Directory.CreateDirectory(_temp);

        // A real file, because Create refuses a target that is not there -- see the test below.
        _target = Path.Combine(_temp, OperatingSystem.IsWindows() ? "launcher.exe" : "launcher");

        File.WriteAllText(_target, "pretend this is the launcher");
    }

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

    private string Destination(string name) => Path.Combine(_temp, Shortcuts.FileNameFor(name));

    [Fact]
    public void TheFileNameGetsThePlatformsExtension()
    {
        var name = Shortcuts.FileNameFor("My Instance");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("My Instance.lnk", name);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("My Instance.app", name);
        }
        else
        {
            Assert.Equal("My Instance.desktop", name);
        }
    }

    [Fact]
    public void AnInstanceNameThatIsNotAValidFileNameIsMadeIntoOne()
    {
        // "1.20.1 / Fabric" is an ordinary instance name and not an acceptable file name.
        var name = Shortcuts.FileNameFor("1.20.1 / Fabric");

        Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Fact]
    public void ANameOfNothingStillProducesAUsableFileName()
    {
        // Not reachable through the UI, which refuses a blank instance name -- but a helper that
        // returns ".desktop" for it would be a trap for the next caller.
        Assert.StartsWith("instance", Shortcuts.FileNameFor("   "), StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetThatIsNotThereIsRefused()
    {
        /*
         * Checked up front rather than left to fail later: a shortcut pointing at a program that does
         * not exist is a file that does nothing when double-clicked, with no clue why.
         */
        var error = Shortcuts.Create(
            Destination("Missing"),
            Path.Combine(_temp, "no-such-program"),
            ["--launch", "one"],
            "Missing");

        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void AnEmptyDestinationIsRefused()
        => Assert.NotEqual(string.Empty, Shortcuts.Create(string.Empty, _target, [], "x"));

    [SkippableFact]
    public void ALinuxShortcutIsADesktopEntryThatNamesTheArguments()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Writes a freedesktop .desktop file.");

        var destination = Destination("My Instance");

        Assert.Equal(string.Empty, Shortcuts.Create(destination, _target, ["--dir", "/data", "--launch", "one"], "My Instance"));

        var text = File.ReadAllText(destination);

        Assert.StartsWith("[Desktop Entry]", text, StringComparison.Ordinal);
        Assert.Contains("Type=Application", text, StringComparison.Ordinal);
        Assert.Contains("Name=My Instance", text, StringComparison.Ordinal);

        // Single-quoted, as upstream writes them: an instance id or a data directory can contain
        // spaces, and an unquoted Exec line would split it into two arguments.
        Assert.Contains("'--launch' 'one'", text, StringComparison.Ordinal);

        // Categories put it under Games rather than in the "Other" bucket nobody looks in.
        Assert.Contains("Categories=Game;", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void ALinuxShortcutIsExecutable()
    {
        /*
         * A .desktop file without the execute bit is refused by every modern desktop with a warning
         * about untrusted launchers -- which reads as this launcher having produced something
         * suspicious.
         */
        Skip.IfNot(OperatingSystem.IsLinux(), "Unix permission bits.");

        var destination = Destination("Runnable");

        Shortcuts.Create(destination, _target, [], "Runnable");

#pragma warning disable CA1416
        Assert.NotEqual(UnixFileMode.None, File.GetUnixFileMode(destination) & UnixFileMode.UserExecute);
#pragma warning restore CA1416
    }

    [SkippableFact]
    public void AMacShortcutIsAnApplicationBundle()
    {
        /*
         * A bare shell script would be simpler and would not appear in Launchpad, take an icon, or
         * behave like an application when double-clicked -- which is the entire point.
         */
        Skip.IfNot(OperatingSystem.IsMacOS(), "Writes a .app bundle.");

        var destination = Destination("My Instance");

        Assert.Equal(string.Empty, Shortcuts.Create(destination, _target, ["--launch", "one"], "My Instance"));

        Assert.True(File.Exists(Path.Combine(destination, "Contents", "Info.plist")));
        Assert.True(File.Exists(Path.Combine(destination, "Contents", "MacOS", "Run.command")));

        Assert.Contains(
            "<string>My Instance</string>",
            File.ReadAllText(Path.Combine(destination, "Contents", "Info.plist")),
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public void AWindowsShortcutIsARealLnkTheShellCanRead()
    {
        /*
         * Written through WScript.Shell rather than hand-rolled COM interop -- see the note in
         * Shortcuts.cs about the P/Invoke that crashed the test host in an earlier wave.
         *
         * Read back through the SAME shell rather than by parsing the binary: a .lnk this port could
         * parse and Explorer could not is exactly the failure worth catching.
         */
        Skip.IfNot(OperatingSystem.IsWindows(), "Writes a Windows .lnk.");

        var destination = Destination("My Instance");

        Assert.Equal(
            string.Empty,
            Shortcuts.Create(destination, _target, ["--dir", _temp, "--launch", "one"], "My Instance"));

        Assert.True(File.Exists(destination));

        // Skip.IfNot threw already if this is not Windows; the analyser cannot see through it.
#pragma warning disable CA1416
        var read = ReadLinkWithShell(destination);
#pragma warning restore CA1416

        Assert.Equal(_target, read.Target, ignoreCase: true);
        Assert.Contains("--launch one", read.Arguments, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void AWindowsShortcutSurvivesAQuoteInThePath()
    {
        /*
         * The script is built from single-quoted PowerShell literals with any quote doubled, so a
         * path containing one cannot end the string and become script. An instance called
         * "Bob's World" is entirely ordinary.
         */
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only quoting rules.");

        var awkward = Path.Combine(_temp, "Bob's Folder");

        Directory.CreateDirectory(awkward);

        var destination = Path.Combine(awkward, Shortcuts.FileNameFor("Bob's Instance"));

        Assert.Equal(string.Empty, Shortcuts.Create(destination, _target, ["--launch", "one"], "Bob's Instance"));
        Assert.True(File.Exists(destination));
    }

    /// <summary>Asks the shell what a .lnk actually points at.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string Target, string Arguments) ReadLinkWithShell(string path)
    {
        var script =
            "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('"
            + path.Replace("'", "''", StringComparison.Ordinal)
            + "');Write-Output $s.TargetPath;Write-Output $s.Arguments";

        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);

        using var process = System.Diagnostics.Process.Start(start)!;

        var output = process.StandardOutput.ReadToEnd().Split('\n');

        process.WaitForExit();

        return (output.ElementAtOrDefault(0)?.Trim() ?? string.Empty, output.ElementAtOrDefault(1)?.Trim() ?? string.Empty);
    }
}
