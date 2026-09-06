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
 * Characterization tests for native extraction. There is no upstream Qt test for ExtractNatives.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Java;
using ExtremeLauncher.Tasks;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class NativesExtractorTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-natives-" + Guid.NewGuid().ToString("N"));

    public NativesExtractorTests()
    {
        Directory.CreateDirectory(_temp);
        Target = Path.Combine(_temp, "natives");
    }

    private string Target { get; }

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

    /// <summary>Writes a jar containing the given entries.</summary>
    private string MakeJar(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_temp, name);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entryPath, content) in entries)
        {
            using var stream = archive.CreateEntry(entryPath).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }

    private NativesExtractor Extractor(IReadOnlyList<string> jars, string javaVersion = "17", IReadOnlyList<string>? excludes = null)
        => new(jars, Target, new JavaVersion(javaVersion), excludes);

    // ================================================================== basics

    [Fact]
    public async Task ExtractsEveryEntry()
    {
        var jar = MakeJar("lwjgl-natives.jar", ("liblwjgl.so", "binary"), ("libopenal.so", "binary"));

        var extractor = Extractor([jar]);

        Assert.True(await extractor.RunAsync());
        Assert.Equal(2, extractor.ExtractedFiles);
        Assert.True(File.Exists(Path.Combine(Target, "liblwjgl.so")));
        Assert.True(File.Exists(Path.Combine(Target, "libopenal.so")));
    }

    [Fact]
    public async Task NestedEntriesKeepTheirStructure()
    {
        var jar = MakeJar("nested.jar", ("linux/x64/liblwjgl.so", "binary"));

        Assert.True(await Extractor([jar]).RunAsync());
        Assert.True(File.Exists(Path.Combine(Target, "linux", "x64", "liblwjgl.so")));
    }

    [Fact]
    public async Task NothingToExtractSucceedsWithoutCreatingAnything()
    {
        Assert.True(await Extractor([]).RunAsync());
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task SeveralJarsAreMergedIntoOneDirectory()
    {
        var a = MakeJar("a.jar", ("liba.so", "x"));
        var b = MakeJar("b.jar", ("libb.so", "x"));

        var extractor = Extractor([a, b]);

        Assert.True(await extractor.RunAsync());
        Assert.Equal(2, extractor.ExtractedFiles);
    }

    // ================================================================== the .jnilib hack

    [Fact]
    public async Task JniLibEntriesAreRenamedOnJavaEightAndLater()
    {
        var jar = MakeJar("mac.jar", ("liblwjgl.jnilib", "binary"));

        Assert.True(await Extractor([jar], javaVersion: "17").RunAsync());

        // Java 8+ only looks for .dylib; without the rename the game cannot find its graphics library.
        Assert.True(File.Exists(Path.Combine(Target, "liblwjgl.dylib")));
        Assert.False(File.Exists(Path.Combine(Target, "liblwjgl.jnilib")));
    }

    [Fact]
    public async Task JniLibEntriesAreLeftAloneOnJavaSeven()
    {
        var jar = MakeJar("mac7.jar", ("liblwjgl.jnilib", "binary"));

        Assert.True(await Extractor([jar], javaVersion: "1.7.0_80").RunAsync());

        Assert.True(File.Exists(Path.Combine(Target, "liblwjgl.jnilib")));
    }

    [Theory]
    [InlineData("liblwjgl.jnilib", "liblwjgl.dylib")]
    [InlineData("liblwjgl.so", "liblwjgl.so")]
    [InlineData("jnilib.txt", "jnilib.txt")]
    [InlineData("", "")]
    public void SuffixReplacementOnlyTouchesTheEnd(string input, string expected)
        => Assert.Equal(expected, NativesExtractor.ReplaceSuffix(input, ".jnilib", ".dylib"));

    // ================================================================== security

    [Fact]
    public async Task AnEntryEscapingTheTargetDirectoryIsRefused()
    {
        // Zip-slip. Native jars arrive over the network, so this is reachable, and upstream has no
        // guard at all — it extracts straight to directory.absoluteFilePath(name).
        var path = Path.Combine(_temp, "evil.jar");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("../../escaped.txt").Open();
            stream.Write(Encoding.UTF8.GetBytes("pwned"));
        }

        var extractor = Extractor([path]);

        Assert.False(await extractor.RunAsync());
        Assert.Contains("escapes the target directory", extractor.FailReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_temp, "escaped.txt")));
    }

    // ================================================================== excludes

    [Fact]
    public async Task ExcludesAreIgnoredByDefault()
    {
        var jar = MakeJar("signed.jar", ("META-INF/MANIFEST.MF", "x"), ("liblwjgl.so", "binary"));

        Assert.True(await Extractor([jar]).RunAsync());

        // Upstream parses extract/exclude and then never reads it, so META-INF lands in natives/.
        Assert.True(File.Exists(Path.Combine(Target, "META-INF", "MANIFEST.MF")));
    }

    [Fact]
    public async Task ExcludesAreHonouredWhenAskedFor()
    {
        var jar = MakeJar("signed2.jar", ("META-INF/MANIFEST.MF", "x"), ("liblwjgl.so", "binary"));

        var extractor = Extractor([jar], excludes: ["META-INF/"]);

        Assert.True(await extractor.RunAsync());

        Assert.False(File.Exists(Path.Combine(Target, "META-INF", "MANIFEST.MF")));
        Assert.True(File.Exists(Path.Combine(Target, "liblwjgl.so")));
        Assert.Equal(1, extractor.ExtractedFiles);
    }

    // ================================================================== failure and cleanup

    [Fact]
    public async Task ACorruptJarFailsTheWholeStep()
    {
        var path = Path.Combine(_temp, "corrupt.jar");
        File.WriteAllText(path, "this is not a zip file");

        var extractor = Extractor([path]);

        // Launching with a half-populated natives folder would fail later and more confusingly.
        Assert.False(await extractor.RunAsync());
        Assert.Equal(TaskState.Failed, extractor.State);
    }

    [Fact]
    public async Task AMissingJarFailsRatherThanBeingSkipped()
    {
        Assert.False(await Extractor([Path.Combine(_temp, "not-here.jar")]).RunAsync());
    }

    [Fact]
    public async Task CleanupRemovesTheScratchDirectory()
    {
        var jar = MakeJar("cleanup.jar", ("liblwjgl.so", "binary"));

        Assert.True(await Extractor([jar]).RunAsync());
        Assert.True(Directory.Exists(Target));

        NativesExtractor.Cleanup(Target);
        Assert.False(Directory.Exists(Target));

        // Safe to call again when it is already gone.
        NativesExtractor.Cleanup(Target);
    }

    [Fact]
    public async Task ExtractionIsCancellable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var jar = MakeJar("cancel.jar", ("liblwjgl.so", "binary"));
        var extractor = Extractor([jar]);

        Assert.False(await extractor.RunAsync(cts.Token));
        Assert.Equal(TaskState.AbortedByUser, extractor.State);
    }
}
