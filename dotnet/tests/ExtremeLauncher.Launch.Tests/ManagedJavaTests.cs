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
 * Finding the Java runtimes the launcher installed for itself.
 *
 * The layouts here are the REAL ones. A real Temurin archive install was inspected to write these:
 * its java.exe landed several directories down, which is why the search is recursive and why a fixed
 * depth would have found nothing.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ManagedJavaTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "el-mjava-" + Guid.NewGuid().ToString("N"));

    private readonly string _java;

    public ManagedJavaTests()
    {
        _java = Path.Combine(_root, "java");

        Directory.CreateDirectory(_java);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string BinaryName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>Creates a java binary at a relative path under the java folder.</summary>
    private string Place(string relativeDirectory)
    {
        var directory = Path.Combine(_java, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, BinaryName);

        File.WriteAllText(path, "pretend jvm");

        return path;
    }

    [Fact]
    public void AManifestInstallIsFound()
    {
        // Mojang's manifest downloads write straight into the runtime folder.
        var expected = Place("mojang-17.0.15-abc1234/bin");

        Assert.Equal([expected], ManagedJava.Find(_java));
    }

    [Fact]
    public void AnArchiveInstallIsFoundSeveralDirectoriesDown()
    {
        /*
         * The layout a REAL Temurin archive produced. A fixed-depth search finds a manifest install
         * and misses this one entirely, which is most of them -- only Mojang uses manifest.
         */
        var expected = Place("eclipse-17.0.20-7fe2324/jdk-17.0.20+8-jre/bin");

        Assert.Equal([expected], ManagedJava.Find(_java));
    }

    [Fact]
    public void SomethingCalledJavaOutsideABinFolderIsIgnored()
    {
        /*
         * A JDK ships more than one thing called java -- older layouts have a copy under jre/bin, and
         * an unpacked source tree can contain others. "bin" is where the one meant to be run lives.
         */
        Place("runtime/bin");

        var strayDirectory = Path.Combine(_java, "runtime", "legal");

        Directory.CreateDirectory(strayDirectory);
        File.WriteAllText(Path.Combine(strayDirectory, BinaryName), "not a jvm");

        var found = ManagedJava.Find(_java);

        Assert.Single(found);
        Assert.Contains("bin", found[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralInstalledRuntimesAreAllFound()
    {
        Place("mojang-17.0.15-abc1234/bin");
        Place("eclipse-21.0.12-def5678/jdk-21.0.12+7-jre/bin");

        Assert.Equal(2, ManagedJava.Find(_java).Count);
    }

    [Fact]
    public void AnEmptyJavaFolderFindsNothingRatherThanThrowing()
    {
        Assert.Empty(ManagedJava.Find(_java));
    }

    [Fact]
    public void NoJavaFolderAtAllFindsNothing()
    {
        // Every launcher that has never downloaded a runtime, which is most of them.
        Assert.Empty(ManagedJava.Find(Path.Combine(_root, "never-created")));
    }

    [Fact]
    public void TheLauncherOwnRuntimesComeBeforeTheSystemOnes()
    {
        /*
         * One the launcher fetched for a specific instance is a more deliberate answer than whichever
         * JDK happens to be on the PATH -- and the machine that needed the download is exactly the
         * machine whose system Java was wrong.
         */
        var managed = Place("mojang-17.0.15-abc1234/bin");

        var candidates = ManagedJava.Candidates(_java, ["C:/system/java.exe", "/usr/bin/java"]);

        Assert.Equal(managed, candidates[0]);
        Assert.Equal(3, candidates.Count);
    }

    [Fact]
    public void ARuntimeThatIsAlsoOnTheSystemPathIsNotProbedTwice()
    {
        // Probing is the slow part of choosing a Java, and a duplicate costs a whole JVM start.
        var managed = Place("mojang-17.0.15-abc1234/bin");

        var candidates = ManagedJava.Candidates(_java, [managed, "/usr/bin/java"]);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(managed, candidates[0]);
    }

    [Fact]
    public void WithNothingInstalledTheSystemListIsUsedUnchanged()
    {
        var system = new[] { "/usr/bin/java", "/opt/jdk/bin/java" };

        Assert.Equal(system, ManagedJava.Candidates(_java, system));
    }
}
