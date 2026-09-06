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
 * Characterization tests for JVM probing. Upstream has no Qt test for JavaChecker.
 *
 * The parsing is tested directly rather than through a process, so it runs everywhere; the process
 * handling is tested against a stub program, and there is one end-to-end check against a real JVM that
 * skips when none is installed.
 */

using ExtremeLauncher.Java;
using Xunit;

namespace ExtremeLauncher.Java.Tests;

public sealed class JavaCheckerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-jcheck-" + Guid.NewGuid().ToString("N"));

    public JavaCheckerTests() => Directory.CreateDirectory(_temp);

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

    private static JavaChecker Checker(string path = "/usr/bin/java", int id = 0)
        => new(path, id: id);

    // ================================================================== parsing

    private const string GoodOutput = """
        os.arch=amd64
        java.version=17.0.9
        java.vendor=Eclipse Adoptium
        """;

    [Fact]
    public void AWellFormedAnswerIsRead()
    {
        var result = Checker("/usr/bin/java", id: 7).Parse(GoodOutput, string.Empty, 0);

        Assert.Equal(JavaCheckValidity.Valid, result.Validity);
        Assert.Equal("17.0.9", result.JavaVersion);
        Assert.Equal("Eclipse Adoptium", result.JavaVendor);
        Assert.Equal("amd64", result.RealPlatform);

        // The id travels with the result, so a caller running many at once knows which is which.
        Assert.Equal(7, result.Id);
        Assert.Equal("/usr/bin/java", result.Path);
    }

    [Theory]
    [InlineData("x86_64", true)]
    [InlineData("amd64", true)]
    [InlineData("aarch64", true)]
    [InlineData("arm64", true)]
    [InlineData("x86", false)]
    [InlineData("i386", false)]
    [InlineData("arm", false)]
    [InlineData("something-new", false)]
    public void TheArchitectureIsMappedToMojangsTwoValueVocabulary(string osArch, bool expected64)
    {
        var result = Checker().Parse(
            $"os.arch={osArch}\njava.version=17\njava.vendor=x",
            string.Empty,
            0);

        Assert.Equal(expected64, result.Is64Bit);
        Assert.Equal(expected64 ? "64" : "32", result.MojangPlatform);

        // The JVM's own spelling is kept alongside, because the rules use both vocabularies.
        Assert.Equal(osArch, result.RealPlatform);
    }

    [Fact]
    public void AnUnknownArchitectureIsTreatedAs32Bit()
    {
        // The safe direction: it caps the heap rather than letting a launch fail at startup.
        var result = Checker().Parse("os.arch=riscv64\njava.version=21\njava.vendor=x", string.Empty, 0);

        Assert.False(result.Is64Bit);
    }

    [Fact]
    public void ANonZeroExitMeansTheJvmCouldNotAnswer()
    {
        // The checker exits 1 when a property came back null.
        var result = Checker().Parse(GoodOutput, string.Empty, 1);

        Assert.Equal(JavaCheckValidity.Errored, result.Validity);
    }

    [Theory]
    [InlineData("java.version=17\njava.vendor=x")]
    [InlineData("os.arch=amd64\njava.vendor=x")]
    [InlineData("os.arch=amd64\njava.version=17")]
    [InlineData("")]
    public void AMissingPropertyMakesTheAnswerUnusable(string stdout)
    {
        var result = Checker().Parse(stdout, string.Empty, 0);

        Assert.Equal(JavaCheckValidity.ReturnedInvalidData, result.Validity);
    }

    [Fact]
    public void CarriageReturnsAndBlankLinesAreTolerated()
    {
        var result = Checker().Parse("\r\nos.arch=amd64\r\n\r\njava.version=17\r\njava.vendor=x\r\n", string.Empty, 0);

        Assert.Equal(JavaCheckValidity.Valid, result.Validity);
        Assert.Equal("17", result.JavaVersion);
    }

    [Fact]
    public void BedrockLinuxNoiseIsIgnored()
    {
        // WORKAROUND for GH-4125: the JVM wrapper there prints its own diagnostics into stdout, and
        // they contain '=' often enough to be mistaken for properties.
        var result = Checker().Parse(
            "/bedrock/strata/arch/usr/lib/jvm=whatever\nos.arch=amd64\njava.version=17\njava.vendor=x",
            string.Empty,
            0);

        Assert.Equal(JavaCheckValidity.Valid, result.Validity);
        Assert.Equal("amd64", result.RealPlatform);
    }

    [Fact]
    public void ALineWithNoValueIsSkippedRatherThanStored()
    {
        var result = Checker().Parse(
            "garbage\nos.arch=\nos.arch=amd64\njava.version=17\njava.vendor=x",
            string.Empty,
            0);

        // The empty one does not win, and does not knock out the real one either.
        Assert.Equal("amd64", result.RealPlatform);
    }

    [Fact]
    public void BothStreamsAreKeptForDiagnostics()
    {
        var result = Checker().Parse(GoodOutput, "Picked up _JAVA_OPTIONS: -Xmx1g", 0);

        // A user reporting "it says my Java is wrong" is answerable from these two.
        Assert.Contains("os.arch", result.OutLog, StringComparison.Ordinal);
        Assert.Contains("_JAVA_OPTIONS", result.ErrorLog, StringComparison.Ordinal);
    }

    // ================================================================== arguments

    [Fact]
    public void TheJarIsTheLastArgument()
    {
        var args = new JavaChecker("java").BuildArguments("/tmp/JavaCheck.jar");

        Assert.Equal(["-jar", "/tmp/JavaCheck.jar"], args);
    }

    [Fact]
    public void MemoryFlagsArePassedSoTheyFailHereRatherThanAtLaunch()
    {
        var args = new JavaChecker("java", minMemory: 512, maxMemory: 4096).BuildArguments("check.jar");

        // A 32-bit Java asked for -Xmx4096m refuses to start; finding that out during a check is far
        // better than during a game start.
        Assert.Contains("-Xms512m", args);
        Assert.Contains("-Xmx4096m", args);
    }

    [Fact]
    public void PermGenIsOnlyPassedWhenItIsNotTheDefault()
    {
        Assert.DoesNotContain("-XX:PermSize=64m", new JavaChecker("java", permGen: 64).BuildArguments("c.jar"));
        Assert.DoesNotContain("-XX:PermSize=0m", new JavaChecker("java", permGen: 0).BuildArguments("c.jar"));
        Assert.Contains("-XX:PermSize=128m", new JavaChecker("java", permGen: 128).BuildArguments("c.jar"));
    }

    [Fact]
    public void ExtraArgumentsAreSplitShellStyleAndComeFirst()
    {
        var args = new JavaChecker("java", args: "-Dfoo=\"a b\" -XX:+UseG1GC", maxMemory: 1024).BuildArguments("c.jar");

        Assert.Equal("-Dfoo=a b", args[0]);
        Assert.Equal("-XX:+UseG1GC", args[1]);
        Assert.Equal("-Xmx1024m", args[2]);
    }

    // ================================================================== the bundled jar

    [Fact]
    public void TheCheckerJarIsBundledAndUnpackable()
    {
        var path = JavaChecker.ExtractCheckerJar();

        Assert.NotEqual(string.Empty, path);
        Assert.True(File.Exists(path));

        // A jar, not an empty placeholder: the local file header magic plus real content.
        var bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length > 100);
        Assert.Equal([0x50, 0x4b, 0x03, 0x04], bytes[..4]);
    }

    [Fact]
    public void UnpackingTwiceReusesTheSameFile()
    {
        var first = JavaChecker.ExtractCheckerJar();
        var second = JavaChecker.ExtractCheckerJar();

        // The startup scan runs one check per candidate; each writing its own copy would race.
        Assert.Equal(first, second);
    }

    // ================================================================== running something

    /// <summary>Writes a script that impersonates a JVM well enough to be probed.</summary>
    private string StubJava(string body)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_temp, "fakejava.cmd");
            File.WriteAllText(path, "@echo off\r\n" + body);

            return path;
        }

        var script = Path.Combine(_temp, "fakejava.sh");
        File.WriteAllText(script, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return script;
    }

    [Fact]
    public async Task AStubThatAnswersCorrectlyIsRead()
    {
        var body = OperatingSystem.IsWindows()
            ? "echo os.arch=amd64\r\necho java.version=17.0.9\r\necho java.vendor=Stub\r\n"
            : "echo os.arch=amd64\necho java.version=17.0.9\necho java.vendor=Stub\n";

        var checker = new JavaChecker(StubJava(body), checkerJarPath: "ignored.jar");

        Assert.True(await checker.RunAsync());

        Assert.Equal(JavaCheckValidity.Valid, checker.Result.Validity);
        Assert.Equal("17.0.9", checker.Result.JavaVersion);
        Assert.Equal("Stub", checker.Result.JavaVendor);
    }

    [Fact]
    public async Task AProgramThatIsNotThereIsAnErroredResultRatherThanAFailedTask()
    {
        var checker = new JavaChecker(Path.Combine(_temp, "no-such-java"), checkerJarPath: "ignored.jar");

        // The startup scan probes every candidate it found; one bad path must not stop the sweep.
        Assert.True(await checker.RunAsync());
        Assert.Equal(JavaCheckValidity.Errored, checker.Result.Validity);
    }

    [Fact]
    public async Task AProgramThatSaysNothingUsefulIsInvalidData()
    {
        var body = OperatingSystem.IsWindows() ? "echo hello\r\n" : "echo hello\n";
        var checker = new JavaChecker(StubJava(body), checkerJarPath: "ignored.jar");

        Assert.True(await checker.RunAsync());
        Assert.Equal(JavaCheckValidity.ReturnedInvalidData, checker.Result.Validity);
    }

    [Fact]
    public async Task AMissingCheckerJarIsReportedRatherThanHanging()
    {
        // Upstream returns here without emitting anything at all, leaving its caller waiting forever.
        var checker = new JavaChecker("java", checkerJarPath: string.Empty);

        Assert.True(await checker.RunAsync());
        Assert.Equal(JavaCheckValidity.Errored, checker.Result.Validity);
        Assert.Contains("could not be found", checker.Result.ErrorLog, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ARealJvmAnswersTheRealJar()
    {
        var java = FindRealJava();
        Skip.If(java is null, "No JVM on PATH.");

        var checker = new JavaChecker(java!);

        Assert.True(await checker.RunAsync());

        // End to end: the bundled jar, unpacked, run by an actual JVM, parsed.
        Assert.Equal(JavaCheckValidity.Valid, checker.Result.Validity);
        Assert.NotEqual(string.Empty, checker.Result.JavaVersion);
        Assert.NotEqual(string.Empty, checker.Result.JavaVendor);
        Assert.Contains(checker.Result.MojangPlatform, new[] { "32", "64" });
    }

    private static string? FindRealJava()
    {
        var name = JavaUtils.JavaExecutable;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry. Skip it.
            }
        }

        return null;
    }

    // ============================================================ what gets probed vs what gets run

    /*
     * A javaw.exe candidate is PROBED with the java.exe beside it. javaw reports startup failures in a
     * modal message box rather than on stderr, and probing is exactly the business of running a JVM
     * that might fail -- on a machine carrying Java 5, 6 and 7, looking for a Java 17 put up a dialog
     * per ancient JRE and waited out the 15 second kill timer on each.
     */
    [SkippableFact]
    public void AWindowsCandidateIsProbedWithTheConsoleBinary()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The javaw.exe/java.exe split is a Windows arrangement.");

        var directory = Path.Combine(Path.GetTempPath(), "el-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var windowless = Path.Combine(directory, "javaw.exe");
            var console = Path.Combine(directory, "java.exe");

            File.WriteAllText(windowless, string.Empty);
            File.WriteAllText(console, string.Empty);

            Assert.Equal(console, JavaChecker.ProbeExecutable(windowless));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /*
     * With no java.exe beside it, the original is used rather than a guessed path: a slow probe is a
     * far better outcome than one that fails because the launcher invented a filename.
     */
    [SkippableFact]
    public void WithNoConsoleBinaryTheOriginalIsProbed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The javaw.exe/java.exe split is a Windows arrangement.");

        var directory = Path.Combine(Path.GetTempPath(), "el-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var windowless = Path.Combine(directory, "javaw.exe");
            File.WriteAllText(windowless, string.Empty);

            Assert.Equal(windowless, JavaChecker.ProbeExecutable(windowless));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Anything that is not a javaw.exe is left exactly as it is, on every platform.</summary>
    [Theory]
    [InlineData("/usr/bin/java")]
    [InlineData("/opt/jdk-21/bin/java")]
    public void ANonWindowlessPathIsProbedAsGiven(string path)
        => Assert.Equal(path, JavaChecker.ProbeExecutable(path));
}
