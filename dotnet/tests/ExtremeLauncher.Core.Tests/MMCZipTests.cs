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
 * Characterization tests for the archive helpers. Upstream has no Qt test for MMCZip.
 *
 * The two things worth being certain about here are jar-mod precedence — which mod's class the game
 * ends up running — and the zip-slip guard, which is the difference between unpacking a modpack and
 * letting one write to any path it likes.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class MMCZipTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-zip-" + Guid.NewGuid().ToString("N"));

    public MMCZipTests() => Directory.CreateDirectory(_temp);

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

    private string Path_(string name) => Path.Combine(_temp, name);

    /// <summary>Writes a zip from a name → content map.</summary>
    private string MakeZip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path_(name);

        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        return zip.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var reader = new StreamReader(e.Open());
                return reader.ReadToEnd();
            },
            StringComparer.Ordinal);
    }

    private static List<string> EntryNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    // ================================================================== merging

    [Fact]
    public void MergingCopiesEveryEntry()
    {
        var source = MakeZip("a.zip", ("one.txt", "1"), ("dir/two.txt", "2"));
        var target = Path_("out.zip");

        using (var stream = new FileStream(target, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Assert.True(MMCZip.MergeZipFiles(zip, source, []));
        }

        Assert.Equal(new Dictionary<string, string> { ["one.txt"] = "1", ["dir/two.txt"] = "2" }, ReadZip(target));
    }

    [Fact]
    public void TheFirstWriterOfAPathWins()
    {
        var first = MakeZip("first.zip", ("shared.txt", "from first"));
        var second = MakeZip("second.zip", ("shared.txt", "from second"), ("only-second.txt", "x"));
        var target = Path_("out.zip");

        var contained = new HashSet<string>(StringComparer.Ordinal);

        using (var stream = new FileStream(target, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            MMCZip.MergeZipFiles(zip, first, contained);
            MMCZip.MergeZipFiles(zip, second, contained);
        }

        var result = ReadZip(target);

        // This is the whole precedence mechanism: once a path is claimed, later sources cannot take it.
        Assert.Equal("from first", result["shared.txt"]);
        Assert.Equal("x", result["only-second.txt"]);
    }

    [Fact]
    public void AFilterKeepsEntriesOut()
    {
        var source = MakeZip("a.zip", ("keep.txt", "1"), ("META-INF/MANIFEST.MF", "2"), ("META-INF/SIG.RSA", "3"));
        var target = Path_("out.zip");

        using (var stream = new FileStream(target, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            MMCZip.MergeZipFiles(zip, source, [], name => !name.Contains("META-INF", StringComparison.Ordinal));
        }

        Assert.Equal(["keep.txt"], EntryNames(target));
    }

    [Fact]
    public void MergingAMissingArchiveFails()
    {
        var target = Path_("out.zip");

        using var stream = new FileStream(target, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        Assert.False(MMCZip.MergeZipFiles(zip, Path_("nope.zip"), []));
    }

    // ================================================================== jar modding

    [Fact]
    public void TheModdedJarTakesTheOriginalsPlusTheMods()
    {
        var vanilla = MakeZip("minecraft.jar", ("net/minecraft/Main.class", "vanilla"), ("assets/pack.png", "png"));
        var mod = MakeZip("mod.zip", ("net/minecraft/Main.class", "modded"));
        var target = Path_("modded.jar");

        Assert.True(MMCZip.CreateModdedJar(vanilla, target, [new JarMod(mod, JarModKind.ZipFile)]));

        var result = ReadZip(target);

        // The mod's class replaces the original's, and everything untouched comes through.
        Assert.Equal("modded", result["net/minecraft/Main.class"]);
        Assert.Equal("png", result["assets/pack.png"]);
    }

    [Fact]
    public void TheLastModInTheListWins()
    {
        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));
        var first = MakeZip("first.zip", ("Main.class", "first"));
        var last = MakeZip("last.zip", ("Main.class", "last"));
        var target = Path_("modded.jar");

        // The merge walks the list backwards, so the LAST entry claims its paths first and is the one
        // whose classes the game actually runs. Reversing the loop swaps which mod is in effect, and
        // nothing about the resulting jar would look wrong.
        MMCZip.CreateModdedJar(vanilla, target, [
            new JarMod(first, JarModKind.ZipFile),
            new JarMod(last, JarModKind.ZipFile),
        ]);

        Assert.Equal("last", ReadZip(target)["Main.class"]);
    }

    [Fact]
    public void DisabledModsAreLeftOut()
    {
        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));
        var mod = MakeZip("mod.zip", ("Main.class", "modded"));
        var target = Path_("modded.jar");

        MMCZip.CreateModdedJar(vanilla, target, [new JarMod(mod, JarModKind.ZipFile, Enabled: false)]);

        Assert.Equal("vanilla", ReadZip(target)["Main.class"]);
    }

    [Fact]
    public void TheSignatureFilesAreStripped()
    {
        var vanilla = MakeZip(
            "minecraft.jar",
            ("Main.class", "vanilla"),
            ("META-INF/MANIFEST.MF", "manifest"),
            ("META-INF/MOJANGCS.RSA", "signature"));

        var mod = MakeZip("mod.zip", ("Mod.class", "mod"));
        var target = Path_("modded.jar");

        MMCZip.CreateModdedJar(vanilla, target, [new JarMod(mod, JarModKind.ZipFile)]);

        // A modded jar no longer matches Mojang's signatures. Leaving these in makes the JVM reject
        // the very classes the mod replaced.
        Assert.DoesNotContain(EntryNames(target), name => name.Contains("META-INF", StringComparison.Ordinal));
        Assert.Contains("Mod.class", EntryNames(target));
    }

    [Fact]
    public void AModsOwnSignatureFilesAreKept()
    {
        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));
        var mod = MakeZip("mod.zip", ("META-INF/MANIFEST.MF", "the mod's own"));
        var target = Path_("modded.jar");

        MMCZip.CreateModdedJar(vanilla, target, [new JarMod(mod, JarModKind.ZipFile)]);

        // The META-INF filter is applied only to the source jar. A mod that ships a manifest keeps it.
        Assert.Contains("META-INF/MANIFEST.MF", EntryNames(target));
    }

    [Fact]
    public void ALooseFileIsDroppedInAtTheRoot()
    {
        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));
        var loose = Path_("Patch.class");
        File.WriteAllText(loose, "patched");

        var target = Path_("modded.jar");

        MMCZip.CreateModdedJar(vanilla, target, [new JarMod(loose, JarModKind.SingleFile)]);

        Assert.Equal("patched", ReadZip(target)["Patch.class"]);
    }

    [Fact]
    public void AFolderModIsAddedUnderItsOwnName()
    {
        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));

        var folder = Path_("mymod");
        Directory.CreateDirectory(Path.Combine(folder, "net"));
        File.WriteAllText(Path.Combine(folder, "net", "Thing.class"), "thing");

        var target = Path_("modded.jar");

        Assert.True(MMCZip.CreateModdedJar(vanilla, target, [new JarMod(folder, JarModKind.Folder)]));

        Assert.Contains("mymod/net/Thing.class", EntryNames(target));
    }

    [Fact]
    public void AFailedJarLeavesNothingBehind()
    {
        var target = Path_("modded.jar");

        // A stale or half-built jar would be launched as-is next time, so it must not survive.
        Assert.False(MMCZip.CreateModdedJar(
            Path_("no-such-source.jar"),
            target,
            [new JarMod(Path_("nope.zip"), JarModKind.ZipFile)]));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public void AnUnknownModKindStopsTheBuild()
    {
        var vanilla = MakeZip("minecraft.jar", ("Main.class", "vanilla"));
        var target = Path_("modded.jar");

        // Rather than quietly producing a jar with a mod missing from it.
        Assert.False(MMCZip.CreateModdedJar(vanilla, target, [new JarMod("x", (JarModKind)99)]));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void JarModKindIsInferredFromDisk()
    {
        var folder = Path_("afolder");
        Directory.CreateDirectory(folder);

        var jar = Path_("a.jar");
        File.WriteAllText(jar, string.Empty);

        var loose = Path_("a.class");
        File.WriteAllText(loose, string.Empty);

        Assert.Equal(JarModKind.Folder, JarMod.FromPath(folder).Kind);
        Assert.Equal(JarModKind.ZipFile, JarMod.FromPath(jar).Kind);
        Assert.Equal(JarModKind.SingleFile, JarMod.FromPath(loose).Kind);
    }

    // ================================================================== extraction

    [Fact]
    public void ExtractingWritesTheWholeTree()
    {
        var archive = MakeZip("pack.zip", ("a.txt", "A"), ("sub/b.txt", "B"), ("sub/deep/c.txt", "C"));
        var target = Path_("out");

        var extracted = MMCZip.ExtractDir(archive, target);

        Assert.NotNull(extracted);
        Assert.Equal(3, extracted.Count);
        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("C", File.ReadAllText(Path.Combine(target, "sub", "deep", "c.txt")));
    }

    [Fact]
    public void ASubdirectoryCanBeExtractedOnItsOwn()
    {
        var archive = MakeZip("pack.zip", ("overrides/config/a.cfg", "A"), ("manifest.json", "{}"));
        var target = Path_("out");

        var extracted = MMCZip.ExtractDir(archive, "overrides/", target);

        Assert.NotNull(extracted);

        // Rooted at the subdirectory, so the prefix does not appear in the output paths.
        Assert.True(File.Exists(Path.Combine(target, "config", "a.cfg")));
        Assert.False(File.Exists(Path.Combine(target, "manifest.json")));
    }

    [Fact]
    public void AnEntryEscapingTheTargetAbandonsTheWholeExtraction()
    {
        var archive = Path_("evil.zip");

        using (var stream = new FileStream(archive, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("innocent.txt").Open()))
            {
                writer.Write("fine");
            }

            // Zip-slip: the classic path-traversal entry.
            using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open()))
            {
                writer.Write("pwned");
            }
        }

        var target = Path_("out");

        // Abandoned rather than skipped: an archive that tried this once is not to be trusted for the
        // rest of its contents.
        Assert.Null(MMCZip.ExtractDir(archive, target));
        Assert.False(File.Exists(Path.Combine(_temp, "escaped.txt")));
    }

    [Fact]
    public void AnEmptyArchiveExtractsToNothingRatherThanFailing()
    {
        // Twenty-two bytes of end-of-central-directory and nothing else. Some servers send one where a
        // real file was expected, and unpacking it is a no-op rather than an error.
        var archive = Path_("empty.zip");

        using (var stream = new FileStream(archive, FileMode.Create))
        using (var _ = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // Nothing added.
        }

        Assert.Equal(22, new FileInfo(archive).Length);
        Assert.NotNull(MMCZip.ExtractDir(archive, Path_("out")));
    }

    [Fact]
    public void AnUnreadableArchiveFails()
    {
        var bogus = Path_("bogus.zip");
        File.WriteAllText(bogus, "this is definitely not a zip file");

        Assert.Null(MMCZip.ExtractDir(bogus, Path_("out")));
        Assert.Null(MMCZip.ExtractDir(Path_("missing.zip"), Path_("out")));
    }

    [Fact]
    public void ASingleFileCanBeExtractedByName()
    {
        var archive = MakeZip("pack.zip", ("manifest.json", "{}"), ("other.txt", "x"));
        var target = Path_("manifest.json");

        Assert.True(MMCZip.ExtractFile(archive, "manifest.json", target));
        Assert.Equal("{}", File.ReadAllText(target));

        Assert.False(MMCZip.ExtractFile(archive, "not-there.json", Path_("nope")));
    }

    [Fact]
    public void DirectoryEntriesBecomeDirectories()
    {
        var archive = Path_("dirs.zip");

        using (var stream = new FileStream(archive, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // A trailing slash is how a zip spells "directory"; there is no content to read.
            zip.CreateEntry("empty-dir/");
        }

        var target = Path_("out");

        Assert.NotNull(MMCZip.ExtractDir(archive, target));
        Assert.True(Directory.Exists(Path.Combine(target, "empty-dir")));
    }

    // ================================================================== searching

    [Fact]
    public void TheFolderContainingAFileIsFound()
    {
        var archive = MakeZip(
            "pack.zip",
            ("a/b/manifest.json", "{}"),
            ("a/other.txt", "x"));

        using var zip = ZipFile.OpenRead(archive);

        Assert.Equal("a/b/", MMCZip.FindFolderOfFileInZip(zip, "manifest.json"));
        Assert.Equal(string.Empty, MMCZip.FindFolderOfFileInZip(zip, "not-there.json"));
    }

    [Fact]
    public void TheShallowestMatchWins()
    {
        var archive = MakeZip("pack.zip", ("deep/deeper/manifest.json", "{}"), ("manifest.json", "{}"));

        using var zip = ZipFile.OpenRead(archive);

        // Upstream recurses outward from the root, so the closest one is what it returns.
        Assert.Equal(string.Empty, MMCZip.FindFolderOfFileInZip(zip, "manifest.json"));
    }

    [Fact]
    public void IgnoredFoldersAreSkipped()
    {
        var archive = MakeZip("pack.zip", ("skipme/manifest.json", "{}"), ("keep/manifest.json", "{}"));

        using var zip = ZipFile.OpenRead(archive);

        Assert.Equal("keep/", MMCZip.FindFolderOfFileInZip(zip, "manifest.json", ["skipme"]));
    }

    [Fact]
    public void EveryFolderContainingAFileCanBeListed()
    {
        var archive = MakeZip("pack.zip", ("a/pack.mcmeta", "{}"), ("b/pack.mcmeta", "{}"), ("c/other", "x"));

        using var zip = ZipFile.OpenRead(archive);

        var found = new List<string>();

        Assert.True(MMCZip.FindFilesInZip(zip, "pack.mcmeta", found));
        Assert.Equal(2, found.Count);

        Assert.False(MMCZip.FindFilesInZip(zip, "nothing", []));
    }

    // ================================================================== compression

    [Fact]
    public void ATreeIsCollectedAndCompressed()
    {
        var source = Path_("tree");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "top.txt"), "T");
        File.WriteAllText(Path.Combine(source, "sub", "nested.txt"), "N");

        var files = new List<string>();
        Assert.True(MMCZip.CollectFileListRecursively(source, null, files, null));
        Assert.Equal(2, files.Count);

        var archive = Path_("out.zip");
        Assert.True(MMCZip.CompressDirFiles(archive, source, files));

        var result = ReadZip(archive);
        Assert.Equal("T", result["top.txt"]);
        Assert.Equal("N", result["sub/nested.txt"]);
    }

    [Fact]
    public void AnExcludeFilterIsTestedAgainstRelativePaths()
    {
        var source = Path_("tree");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "keep.txt"), "K");
        File.WriteAllText(Path.Combine(source, "sub", "drop.txt"), "D");

        var files = new List<string>();

        MMCZip.CollectFileListRecursively(
            source,
            null,
            files,
            relative => relative.StartsWith("sub/", StringComparison.Ordinal));

        Assert.Single(files);
        Assert.EndsWith("keep.txt", files[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CollectingFromAMissingDirectoryFails()
        => Assert.False(MMCZip.CollectFileListRecursively(Path_("nope"), null, [], null));

    [Fact]
    public void AFailedArchiveIsNotLeftOnDisk()
    {
        var source = Path_("tree");
        Directory.CreateDirectory(source);

        var archive = Path_("out.zip");

        // A file that is not there fails the write; a half-built archive looks like a valid result, so
        // it must not survive.
        Assert.False(MMCZip.CompressDirFiles(archive, source, [Path.Combine(source, "missing.txt")]));
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public void CompressingIntoAMissingParentCreatesIt()
    {
        var source = Path_("tree");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");

        var archive = Path_("deeply/nested/out.zip");

        Assert.True(MMCZip.CompressDirFiles(archive, source, [Path.Combine(source, "a.txt")]));
        Assert.True(File.Exists(archive));
    }

    // ================================================================== round trip

    [Fact]
    public void ATreeSurvivesCompressionAndExtraction()
    {
        var source = Path_("tree");
        Directory.CreateDirectory(Path.Combine(source, "config", "deep"));
        File.WriteAllText(Path.Combine(source, "options.txt"), "lang:en_us");
        File.WriteAllText(Path.Combine(source, "config", "deep", "mod.cfg"), "setting=1");

        var files = new List<string>();
        MMCZip.CollectFileListRecursively(source, null, files, null);

        var archive = Path_("pack.zip");
        Assert.True(MMCZip.CompressDirFiles(archive, source, files));

        var target = Path_("restored");
        Assert.NotNull(MMCZip.ExtractDir(archive, target));

        Assert.Equal("lang:en_us", File.ReadAllText(Path.Combine(target, "options.txt")));
        Assert.Equal("setting=1", File.ReadAllText(Path.Combine(target, "config", "deep", "mod.cfg")));
    }
}
