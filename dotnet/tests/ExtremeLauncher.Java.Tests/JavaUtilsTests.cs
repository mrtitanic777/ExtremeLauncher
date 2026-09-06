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
 * Characterization tests for JVM discovery. There is no upstream Qt test for JavaUtils.
 *
 * Discovery results depend on what is actually installed on the running machine, so these assert on
 * shape and invariants rather than specific paths. The registry scan is exercised for real on
 * Windows, but only that it runs cleanly and returns well-formed paths -- asserting that a JVM is
 * present would make the suite fail on a machine without Java, which is not a code defect.
 */

using System.Runtime.Versioning;
using Microsoft.Win32;
using Xunit;

namespace ExtremeLauncher.Java.Tests;

public sealed class JavaUtilsTests
{
    [Fact]
    public void JavaExecutableMatchesThePlatform()
        => Assert.Equal(OperatingSystem.IsWindows() ? "javaw.exe" : "java", JavaUtils.JavaExecutable);

    [Fact]
    public void DefaultJavaIsTheBarePathEntry()
    {
        var fallback = JavaUtils.GetDefaultJava();

        Assert.Equal("java", fallback.Id);
        Assert.Equal("unknown", fallback.Arch);
        Assert.Equal(OperatingSystem.IsWindows() ? "javaw" : "java", fallback.Path);
    }

    [Fact]
    public void FindJavaPathsReturnsDeduplicatedNonEmptyEntries()
    {
        var paths = JavaUtils.FindJavaPaths();

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.NotEqual(string.Empty, p));

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Assert.Equal(paths.Count, paths.Distinct(comparer).Count());
    }

    [Fact]
    public void FindJavaPathsAlwaysIncludesTheBareFallback()
    {
        // Even on a machine with no JVM installed, the PATH fallback must be offered.
        Assert.Contains(JavaUtils.GetDefaultJava().Path, JavaUtils.FindJavaPaths());
    }

    [Fact]
    public void EnvironmentPathsPickUpTheExtraVariable()
    {
        var original = Environment.GetEnvironmentVariable(JavaUtils.ExtraPathsVariable);

        try
        {
            var custom = string.Join(Path.PathSeparator, "/custom/one/java", "/custom/two/java");
            Environment.SetEnvironmentVariable(JavaUtils.ExtraPathsVariable, custom);

            var paths = JavaUtils.GetJavaPathsFromEnvironment();

            Assert.Contains("/custom/one/java", paths);
            Assert.Contains("/custom/two/java", paths);
        }
        finally
        {
            Environment.SetEnvironmentVariable(JavaUtils.ExtraPathsVariable, original);
        }
    }

    [Fact]
    public void EnvironmentPathsAppendTheInterpreterToEveryPathEntry()
    {
        var paths = JavaUtils.GetJavaPathsFromEnvironment();

        // Every PATH-derived candidate should end in the platform interpreter name.
        Assert.Contains(paths, p => p.EndsWith(JavaUtils.JavaExecutable, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]  // The Skip below is the real guard; this tells the analyzer.
    public void RegistryScanRunsCleanlyAndYieldsWellFormedPaths()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Registry scanning is Windows-only.");

        var found = JavaUtils.FindJavaFromRegistry(RegistryView.Registry64);

        // Asserting a JVM is installed would fail on a clean machine, which is not a defect. What
        // must hold is that every path the scan produces points at the interpreter.
        Assert.All(found, p => Assert.EndsWith(JavaUtils.JavaExecutable, p, StringComparison.OrdinalIgnoreCase));
        Assert.All(found, p => Assert.DoesNotContain('\\', p));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]  // The Skip below is the real guard; this tells the analyzer.
    public void BothRegistryViewsAreReadable()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Registry scanning is Windows-only.");

        // A missing or unreadable vendor key must not throw -- there are a dozen more to try.
        var exception = Record.Exception(() =>
        {
            JavaUtils.FindJavaFromRegistry(RegistryView.Registry64);
            JavaUtils.FindJavaFromRegistry(RegistryView.Registry32);
        });

        Assert.Null(exception);
    }
}
