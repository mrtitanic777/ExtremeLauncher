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
 * Path tests ported from tests/FileSystem_test.cpp (test_pathCombine, test_PathCombine1,
 * test_PathCombine2, test_path_depth, test_path_trunc). The upstream copy/link tests are not ported
 * yet -- they exercise FS::copy and FS::create_link, which are deferred to wave 2.
 *
 * I/O tests below are characterization tests written for this port.
 */

using System.Text;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class FileSystemPathTests
{
    private const string BothSlash = "/foo/";
    private const string TrailingSlash = "foo/";
    private const string LeadingSlash = "/foo";

    [Fact]
    public void PathCombineHandlesSurroundingSlashes()
    {
        Assert.Equal("/foo/foo", FileSystem.PathCombine(BothSlash, BothSlash));
        Assert.Equal("foo/foo", FileSystem.PathCombine(TrailingSlash, TrailingSlash));
        Assert.Equal("/foo/foo", FileSystem.PathCombine(LeadingSlash, LeadingSlash));

        Assert.Equal("/foo/foo/foo", FileSystem.PathCombine(BothSlash, BothSlash, BothSlash));
        Assert.Equal("foo/foo/foo", FileSystem.PathCombine(TrailingSlash, TrailingSlash, TrailingSlash));
        Assert.Equal("/foo/foo/foo", FileSystem.PathCombine(LeadingSlash, LeadingSlash, LeadingSlash));
    }

    [Theory]
    [InlineData("/abc/def/ghi/jkl", "/abc/def", "ghi/jkl")]
    [InlineData("/abc/def/ghi/jkl", "/abc/def/", "ghi/jkl")]
    public void PathCombineTwoParts(string expected, string path1, string path2)
        => Assert.Equal(expected, FileSystem.PathCombine(path1, path2));

    [SkippableTheory]
    [InlineData("C:/abc", "C:", "abc")]
    [InlineData("C:/abc/def/ghi/jkl", @"C:\abc\def", @"ghi\jkl")]
    [InlineData("C:/abc/def/ghi/jkl", @"C:\abc\def\", @"ghi\jkl")]
    public void PathCombineTwoPartsWindows(string expected, string path1, string path2)
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.Equal(expected, FileSystem.PathCombine(path1, path2));
    }

    [Theory]
    [InlineData("/abc/def/ghi/jkl", "/abc", "def", "ghi/jkl")]
    [InlineData("/abc/def/ghi/jkl", "/abc/", "def", "ghi/jkl")]
    [InlineData("/abc/def/ghi/jkl", "/abc", "def/", "ghi/jkl")]
    [InlineData("/abc/def/ghi/jkl", "/abc/", "def/", "ghi/jkl")]
    public void PathCombineThreeParts(string expected, string path1, string path2, string path3)
        => Assert.Equal(expected, FileSystem.PathCombine(path1, path2, path3));

    [SkippableTheory]
    [InlineData("C:/abc/def/ghi/jkl", @"C:\abc", "def", @"ghi\jkl")]
    [InlineData("C:/abc/def/ghi/jkl", @"C:\abc\", "def", @"ghi\jkl")]
    [InlineData("C:/abc/def/ghi/jkl", @"C:\abc", @"def\", @"ghi\jkl")]
    public void PathCombineThreePartsWindows(string expected, string path1, string path2, string path3)
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.Equal(expected, FileSystem.PathCombine(path1, path2, path3));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData(".", 0)]
    [InlineData("foo.txt", 0)]
    [InlineData("./foo.txt", 0)]
    [InlineData("./bar/foo.txt", 1)]
    [InlineData("../bar/foo.txt", 0)]
    [InlineData("/bar/foo.txt", 1)]
    [InlineData("baz/bar/foo.txt", 2)]
    [InlineData("/baz/bar/foo.txt", 2)]
    [InlineData("./baz/bar/foo.txt", 2)]
    [InlineData("/baz/../bar/foo.txt", 1)]
    public void PathDepthCountsComponents(string path, int expected)
        => Assert.Equal(expected, FileSystem.PathDepth(path));

    [Theory]
    [InlineData("", 0, "")]
    [InlineData("foo.txt", 0, "")]
    [InlineData("foo.txt", 1, "")]
    [InlineData("./bar/foo.txt", 0, "./bar")]
    [InlineData("./bar/foo.txt", 1, "./bar")]
    [InlineData("/bar/foo.txt", 1, "/bar")]
    [InlineData("bar/foo.txt", 1, "bar")]
    [InlineData("baz/bar/foo.txt", 2, "baz/bar")]
    public void PathTruncateCutsToDepth(string path, int depth, string expected)
    {
        // Upstream compares against QDir::toNativeSeparators(expected): PathTruncate returns native
        // separators, unlike PathCombine which returns forward slashes.
        Assert.Equal(FileSystem.ToNativeSeparators(expected), FileSystem.PathTruncate(path, depth));
    }

    [SkippableFact]
    public void PathTruncateHandlesDriveLetters()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.Equal(FileSystem.ToNativeSeparators(@"C:\bar"), FileSystem.PathTruncate(@"C:\bar\foo.txt", 1));
    }

    [Fact]
    public void CleanPathResolvesDotSegments()
    {
        Assert.Equal("/a/b", FileSystem.CleanPath("/a/./b"));
        Assert.Equal("/a/c", FileSystem.CleanPath("/a/b/../c"));
        Assert.Equal("/a", FileSystem.CleanPath("/a/b/.."));
        Assert.Equal("a/b", FileSystem.CleanPath("a//b"));
        Assert.Equal(".", FileSystem.CleanPath("a/.."));

        // Cannot ascend past the root.
        Assert.Equal("/a", FileSystem.CleanPath("/../a"));

        // A relative path can keep leading "..".
        Assert.Equal("../a", FileSystem.CleanPath("../a"));
    }

    [Fact]
    public void RemoveInvalidFilenameCharsReplacesReservedCharacters()
    {
        Assert.Equal("my-pack-v1", FileSystem.RemoveInvalidFilenameChars("my/pack:v1"));
        Assert.Equal("a-b", FileSystem.RemoveInvalidFilenameChars("a\nb"));
        Assert.Equal("a_b", FileSystem.RemoveInvalidFilenameChars("a<b", '_'));
        Assert.Equal("perfectly fine", FileSystem.RemoveInvalidFilenameChars("perfectly fine"));
    }

    [Fact]
    public void CheckProblematicPathJavaDetectsBang()
    {
        Assert.True(FileSystem.CheckProblematicPathJava("/home/user/my!instance"));
        Assert.False(FileSystem.CheckProblematicPathJava("/home/user/instance"));
    }

    [Fact]
    public void FilesystemTypeNameLookupIsExact()
    {
        Assert.Equal(FilesystemType.Ntfs, FileSystem.GetFilesystemType("ntfs"));
        Assert.Equal(FilesystemType.Ext234, FileSystem.GetFilesystemType("ext4"));
        Assert.Equal(FilesystemType.Unknown, FileSystem.GetFilesystemType("something else"));

        Assert.Equal("NTFS", FileSystem.GetFilesystemTypeName(FilesystemType.Ntfs));
        Assert.Equal("UNKNOWN", FileSystem.GetFilesystemTypeName(FilesystemType.Unknown));
    }

    [Fact]
    public void FuzzyFilesystemLookupResolvesExt4ToExt()
    {
        // QUIRK preserved from upstream: Ext precedes Ext234 in the enum and "EXT4" contains "EXT",
        // so the fuzzy lookup stops at Ext. The exact lookup above gets it right.
        Assert.Equal(FilesystemType.Ext, FileSystem.GetFilesystemTypeFuzzy("ext4"));

        Assert.Equal(FilesystemType.Ntfs, FileSystem.GetFilesystemTypeFuzzy("NTFS volume"));
        Assert.Equal(FilesystemType.Unknown, FileSystem.GetFilesystemTypeFuzzy("nothing matches"));
    }

    [Fact]
    public void CloneAndLinkCapabilityFollowTheFilesystem()
    {
        Assert.True(FileSystem.CanCloneOnFs(FilesystemType.Btrfs));
        Assert.False(FileSystem.CanCloneOnFs(FilesystemType.Ntfs));

        // FAT is the only filesystem upstream treats as link-incapable.
        Assert.False(FileSystem.CanLinkOnFs(FilesystemType.Fat));
        Assert.True(FileSystem.CanLinkOnFs(FilesystemType.Ntfs));
    }
}

public sealed class FileSystemIoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "el-fs-tests-" + Guid.NewGuid().ToString("N"));

    public FileSystemIoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public void WriteCreatesMissingDirectories()
    {
        var target = At("deep", "nested", "file.txt");

        FileSystem.Write(target, Encoding.UTF8.GetBytes("hello"));

        Assert.True(File.Exists(target));
        Assert.Equal("hello", File.ReadAllText(target));
    }

    [Fact]
    public void WriteOverwritesAtomicallyAndLeavesNoTempFiles()
    {
        var target = At("file.txt");

        FileSystem.Write(target, Encoding.UTF8.GetBytes("first"));
        FileSystem.Write(target, Encoding.UTF8.GetBytes("second"));

        Assert.Equal("second", File.ReadAllText(target));

        // The temp file used for the atomic swap must not survive.
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void ReadRoundTripsWrite()
    {
        var target = At("data.bin");
        var data = new byte[] { 1, 2, 3, 250, 251 };

        FileSystem.Write(target, data);

        Assert.Equal(data, FileSystem.Read(target));
    }

    [Fact]
    public void ReadThrowsFileSystemExceptionForMissingFile()
        => Assert.Throws<FileSystemException>(() => FileSystem.Read(At("nope.txt")));

    [Fact]
    public void AppendAddsToExistingContent()
    {
        var target = At("log.txt");

        FileSystem.Append(target, Encoding.UTF8.GetBytes("a"));
        FileSystem.Append(target, Encoding.UTF8.GetBytes("b"));

        Assert.Equal("ab", File.ReadAllText(target));
    }

    [Fact]
    public void AppendSafeStartsFromEmptyWhenTheFileIsMissing()
    {
        var target = At("safe.txt");

        FileSystem.AppendSafe(target, Encoding.UTF8.GetBytes("x"));
        FileSystem.AppendSafe(target, Encoding.UTF8.GetBytes("y"));

        Assert.Equal("xy", File.ReadAllText(target));
    }

    [Fact]
    public void DeletePathRemovesTreesAndFiles()
    {
        var directory = At("tree");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        File.WriteAllText(Path.Combine(directory, "sub", "f.txt"), "x");

        Assert.True(FileSystem.DeletePath(directory));
        Assert.False(Directory.Exists(directory));

        // Deleting something that is already gone is not a failure.
        Assert.True(FileSystem.DeletePath(At("never-existed")));
    }

    [Fact]
    public void MoveRenamesFiles()
    {
        var source = At("from.txt");
        var dest = At("to.txt");
        File.WriteAllText(source, "content");

        Assert.True(FileSystem.Move(source, dest));
        Assert.False(File.Exists(source));
        Assert.Equal("content", File.ReadAllText(dest));
    }

    [Fact]
    public void OverrideFolderKeepsDestinationOnlyFiles()
    {
        var destination = At("dest");
        var overrides = At("over");

        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(overrides);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(destination, "shared.txt"), "old");
        File.WriteAllText(Path.Combine(overrides, "shared.txt"), "new");

        Assert.True(FileSystem.OverrideFolder(destination, overrides));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "shared.txt")));
    }

    [Fact]
    public void UpdateTimestampTouchesExistingFiles()
    {
        var target = At("stamp.txt");
        File.WriteAllText(target, "x");
        File.SetLastWriteTimeUtc(target, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(FileSystem.UpdateTimestamp(target));
        Assert.True(File.GetLastWriteTimeUtc(target).Year > 2000);

        Assert.False(FileSystem.UpdateTimestamp(At("missing.txt")));
    }

    [Fact]
    public void EnsureFilePathExistsCreatesParentsOnly()
    {
        var target = At("a", "b", "file.txt");

        Assert.True(FileSystem.EnsureFilePathExists(target));
        Assert.True(Directory.Exists(At("a", "b")));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void EnsureFolderPathExistsCreatesTheLastSegment()
    {
        var target = At("a", "b", "c");

        Assert.True(FileSystem.EnsureFolderPathExists(target));
        Assert.True(Directory.Exists(target));

        // Already existing is still success.
        Assert.True(FileSystem.EnsureFolderPathExists(target));
    }

    [Fact]
    public void DirNameFromStringAvoidsCollisions()
    {
        Assert.Equal("pack", FileSystem.DirNameFromString("pack", _root));

        Directory.CreateDirectory(At("pack"));
        Assert.Equal("pack(1)", FileSystem.DirNameFromString("pack", _root));

        Directory.CreateDirectory(At("pack(1)"));
        Assert.Equal("pack(2)", FileSystem.DirNameFromString("pack", _root));
    }

    [Fact]
    public void DirNameFromStringSanitizesFirst()
        => Assert.Equal("my-pack", FileSystem.DirNameFromString("my/pack", _root));

    [Fact]
    public void NearestExistentAncestorWalksUp()
    {
        var deep = At("x", "y", "z");

        Assert.Equal(_root, FileSystem.NearestExistentAncestor(deep));
        Assert.Equal(_root, FileSystem.NearestExistentAncestor(_root));
    }

    [Fact]
    public void GetUniqueResourceNamePrefersEnabledMods()
    {
        // A non-.disabled path is always returned as-is.
        var enabled = At("mod.jar");
        Assert.Equal(enabled, FileSystem.GetUniqueResourceName(enabled));

        // .disabled with no enabled counterpart is also left alone.
        var disabled = At("other.jar.disabled");
        Assert.Equal(disabled, FileSystem.GetUniqueResourceName(disabled));
    }

    [Fact]
    public void GetUniqueResourceNameDisambiguatesAgainstEnabledCounterpart()
    {
        File.WriteAllText(At("mod.jar"), "enabled");
        var disabled = At("mod.jar.disabled");

        var result = FileSystem.GetUniqueResourceName(disabled);

        Assert.EndsWith(".duplicate", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StatFsReportsTheContainingVolume()
    {
        var info = FileSystem.StatFs(_root);

        Assert.NotEqual(string.Empty, info.RootPath);
        Assert.True(info.BytesTotal > 0);
    }
}
