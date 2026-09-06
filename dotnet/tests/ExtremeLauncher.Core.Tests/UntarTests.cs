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
 * Characterization tests for the tar reader. Upstream has no Qt test for it.
 *
 * The archives here are built BYTE BY BYTE rather than by shelling out to tar, so the header layout
 * itself is under test and the suite does not depend on a tar binary being installed. TarBuilder below
 * is the format description in executable form; if it and Untar.cs ever disagree about an offset, one
 * of them is wrong and the tests will say so.
 */

using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class UntarTests : IDisposable
{
    private const int BlockSize = 512;

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-tar-" + Guid.NewGuid().ToString("N"));

    public UntarTests() => Directory.CreateDirectory(_temp);

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

    // ================================================================== building archives

    /// <summary>Writes tar headers and payloads at the offsets the format specifies.</summary>
    private sealed class TarBuilder
    {
        private readonly MemoryStream _stream = new();

        public TarBuilder File(string name, string content, string mode = "0000644")
        {
            var bytes = Encoding.UTF8.GetBytes(content);

            WriteHeader(name, bytes.Length, '0', mode);
            WritePadded(bytes);

            return this;
        }

        public TarBuilder Directory(string name, string mode = "0000755")
        {
            WriteHeader(name.EndsWith('/') ? name : name + "/", 0, '5', mode);
            return this;
        }

        public TarBuilder Link(string name, string target, char type = '1')
        {
            WriteHeader(name, 0, type, "0000777", target);
            return this;
        }

        /// <summary>A GNU long-name member: its contents are the name of the member that follows.</summary>
        public TarBuilder LongName(string longName)
        {
            var bytes = Encoding.UTF8.GetBytes(longName + "\0");

            WriteHeader("././@LongLink", bytes.Length, 'L', "0000644");
            WritePadded(bytes);

            return this;
        }

        public TarBuilder LongLink(string longTarget)
        {
            var bytes = Encoding.UTF8.GetBytes(longTarget + "\0");

            WriteHeader("././@LongLink", bytes.Length, 'K', "0000644");
            WritePadded(bytes);

            return this;
        }

        /// <summary>A member kind the reader is expected to skip, contents and all.</summary>
        public TarBuilder Other(string name, char type, string content = "")
        {
            var bytes = Encoding.UTF8.GetBytes(content);

            WriteHeader(name, bytes.Length, type, "0000644");
            WritePadded(bytes);

            return this;
        }

        /// <summary>Two zero blocks: the end-of-archive marker.</summary>
        public byte[] Build()
        {
            _stream.Write(new byte[BlockSize * 2]);
            return _stream.ToArray();
        }

        /// <summary>Ends the archive without its terminator, as a truncated download would.</summary>
        public byte[] BuildTruncated() => _stream.ToArray();

        private void WriteHeader(string name, int size, char type, string mode, string linkName = "")
        {
            var header = new byte[BlockSize];

            Put(header, 0, name, 100);
            Put(header, 100, mode, 8);
            Put(header, 108, "0000000", 8);                                  // uid
            Put(header, 116, "0000000", 8);                                  // gid
            Put(header, 124, Convert.ToString(size, 8).PadLeft(11, '0'), 12); // size, octal
            Put(header, 136, "00000000000", 12);                             // mtime

            header[156] = (byte)type;

            Put(header, 157, linkName, 100);
            Put(header, 257, "ustar", 6);
            Put(header, 263, "00", 2);

            // The checksum is computed with the field itself read as spaces. Nothing in this port
            // verifies it, but a real tar does, and writing it keeps the fixtures honest.
            for (var i = 148; i < 156; i++)
            {
                header[i] = (byte)' ';
            }

            var checksum = header.Aggregate(0, (sum, b) => sum + b);
            Put(header, 148, Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ", 8);

            _stream.Write(header);
        }

        private void WritePadded(byte[] payload)
        {
            _stream.Write(payload);

            var remainder = payload.Length % BlockSize;

            if (remainder != 0)
            {
                _stream.Write(new byte[BlockSize - remainder]);
            }
        }

        private static void Put(byte[] buffer, int offset, string value, int length)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length));
        }
    }

    private bool Extract(byte[] archive, string into, Func<string, string, bool>? createLink = null)
    {
        using var stream = new MemoryStream(archive);
        return Tar.Extract(stream, Path.Combine(_temp, into), createLink);
    }

    private string Read(string into, string relative)
        => File.ReadAllText(Path.Combine(_temp, into, relative.Replace('/', Path.DirectorySeparatorChar)));

    // ================================================================== the ordinary cases

    [Fact]
    public void FilesAndFoldersAreExtracted()
    {
        var archive = new TarBuilder()
            .Directory("jdk-17/")
            .Directory("jdk-17/bin/")
            .File("jdk-17/bin/java", "#!/bin/sh")
            .File("jdk-17/release", "JAVA_VERSION=17")
            .Build();

        Assert.True(Extract(archive, "out"));

        // The first directory is stripped, so a runtime unpacks as bin/... rather than jdk-17/bin/...
        Assert.Equal("#!/bin/sh", Read("out", "bin/java"));
        Assert.Equal("JAVA_VERSION=17", Read("out", "release"));
        Assert.False(Directory.Exists(Path.Combine(_temp, "out", "jdk-17")));
    }

    [Fact]
    public void AnArchiveWithNoTopLevelFolderExtractsAsIs()
    {
        var archive = new TarBuilder()
            .File("a.txt", "A")
            .File("b.txt", "B")
            .Build();

        Assert.True(Extract(archive, "out"));

        Assert.Equal("A", Read("out", "a.txt"));
        Assert.Equal("B", Read("out", "b.txt"));
    }

    [Fact]
    public void OnlyTheFirstFolderIsStripped()
    {
        var archive = new TarBuilder()
            .Directory("jdk-17/")
            .Directory("jdk-17/lib/")
            .Directory("jdk-17/lib/server/")
            .File("jdk-17/lib/server/libjvm.so", "elf")
            .Build();

        Assert.True(Extract(archive, "out"));

        Assert.True(Directory.Exists(Path.Combine(_temp, "out", "lib", "server")));
        Assert.Equal("elf", Read("out", "lib/server/libjvm.so"));
    }

    [Fact]
    public void ContentsSpanningManyBlocksSurviveIntact()
    {
        // Deliberately not a multiple of the block size, so the final partial block is exercised.
        var content = new string('x', (BlockSize * 3) + 137);

        Assert.True(Extract(new TarBuilder().File("big.bin", content).Build(), "out"));

        Assert.Equal(content, Read("out", "big.bin"));
    }

    [Fact]
    public void AnEmptyFileIsStillCreated()
    {
        Assert.True(Extract(new TarBuilder().File("empty.txt", string.Empty).Build(), "out"));

        Assert.True(File.Exists(Path.Combine(_temp, "out", "empty.txt")));
        Assert.Equal(string.Empty, Read("out", "empty.txt"));
    }

    [Fact]
    public void AnEmptyArchiveIsNotAFailure()
        => Assert.True(Extract(new TarBuilder().Build(), "out"));

    // ================================================================== long names

    [Fact]
    public void AGnuLongNameIsUsedForTheFollowingMember()
    {
        // Over the 100-byte header field, which is what forces the extension.
        var longName = "a/" + new string('n', 150) + ".txt";

        var archive = new TarBuilder()
            .LongName(longName)
            .File("ignored-short-name", "content")
            .Build();

        Assert.True(Extract(archive, "out"));

        Assert.Equal("content", Read("out", longName));
    }

    [Fact]
    public void AShortNameIsNotDisturbedByAPrecedingLongOne()
    {
        var longName = new string('n', 150) + ".txt";

        var archive = new TarBuilder()
            .LongName(longName)
            .File("ignored", "long one")
            .File("short.txt", "short one")
            .Build();

        Assert.True(Extract(archive, "out"));

        // The long name applies to exactly one member; the next one uses its own header field.
        Assert.Equal("long one", Read("out", longName));
        Assert.Equal("short one", Read("out", "short.txt"));
    }

    [Fact]
    public void AnExactlyHundredByteNameIsReadWhole()
    {
        // The field is filled with no room for a terminator, which is the case that bit KDE (bug 101472).
        var name = new string('a', 96) + ".txt";
        Assert.Equal(100, name.Length);

        Assert.True(Extract(new TarBuilder().File(name, "content").Build(), "out"));

        Assert.Equal("content", Read("out", name));
    }

    // ================================================================== links

    [Fact]
    public void ALinkTargetComesFromTheLinknameField()
    {
        var requested = new List<(string Target, string Link)>();

        var archive = new TarBuilder()
            .File("real.txt", "content")
            .Link("alias.txt", "real.txt")
            .Build();

        Assert.True(Extract(archive, "out", (target, link) =>
        {
            requested.Add((target, link));
            return true;
        }));

        // UPSTREAM BUG (#9) FIXED: upstream reads the target from offset 0 — the NAME field — so every
        // link would be asked to point at itself. Java runtimes on Linux do contain links.
        var (targetPath, linkPath) = Assert.Single(requested);

        Assert.EndsWith("real.txt", targetPath, StringComparison.Ordinal);
        Assert.EndsWith("alias.txt", linkPath, StringComparison.Ordinal);
        Assert.NotEqual(targetPath, linkPath);
    }

    [Fact]
    public void ALinkTargetIsRelativeToTheLinksOwnDirectory()
    {
        var requested = new List<string>();

        var archive = new TarBuilder()
            .Directory("top/")
            .Directory("top/lib/")
            .File("top/lib/real.so", "elf")
            .Link("top/lib/alias.so", "real.so")
            .Build();

        Extract(archive, "out", (target, _) =>
        {
            requested.Add(target);
            return true;
        });

        var target = Assert.Single(requested);

        Assert.EndsWith(Path.Combine("lib", "real.so"), target, StringComparison.Ordinal);
    }

    [Fact]
    public void AGnuLongLinkSuppliesTheTarget()
    {
        var longTarget = new string('t', 150) + ".so";
        var requested = new List<string>();

        var archive = new TarBuilder()
            .LongLink(longTarget)
            .Link("alias.so", "ignored-short-target")
            .Build();

        Assert.True(Extract(archive, "out", (target, _) =>
        {
            requested.Add(target);
            return true;
        }));

        Assert.EndsWith(longTarget, Assert.Single(requested), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedLinkFailsTheExtraction()
    {
        var archive = new TarBuilder()
            .File("real.txt", "content")
            .Link("alias.txt", "real.txt")
            .Build();

        Assert.False(Extract(archive, "out", (_, _) => false));
    }

    // ================================================================== skipped member kinds

    [Fact]
    public void DeviceNodesAndMetadataHeadersAreSkippedWithTheirContents()
    {
        // The contents of a skipped member must still be consumed, or every later header is read from
        // the middle of a payload and the archive turns to noise.
        var archive = new TarBuilder()
            .Other("pax_global_header", 'g', new string('m', 300))
            .Other("dev/null", '3')
            .File("after.txt", "still here")
            .Build();

        Assert.True(Extract(archive, "out"));

        Assert.Equal("still here", Read("out", "after.txt"));
        Assert.False(File.Exists(Path.Combine(_temp, "out", "pax_global_header")));
    }

    // ================================================================== refusals

    [Fact]
    public void AnEntryEscapingTheDestinationIsRefused()
    {
        // UPSTREAM HAS NO SUCH CHECK, and this code unpacks archives fetched over the network.
        var archive = new TarBuilder()
            .File("../escaped.txt", "pwned")
            .Build();

        Assert.False(Extract(archive, "out"));
        Assert.False(File.Exists(Path.Combine(_temp, "escaped.txt")));
    }

    [Fact]
    public void ADeeplyTraversingEntryIsRefused()
    {
        var archive = new TarBuilder()
            .File("a/b/../../../../escaped.txt", "pwned")
            .Build();

        Assert.False(Extract(archive, "out"));
    }

    [Fact]
    public void AnAbsoluteEntryNameIsClampedIntoTheDestination()
    {
        // A leading slash is stripped rather than refused: tars built with absolute paths are common
        // and harmless once rooted.
        Assert.True(Extract(new TarBuilder().File("/etc/thing.conf", "x").Build(), "out"));

        Assert.Equal("x", Read("out", "etc/thing.conf"));
    }

    [Fact]
    public void ALinkPointingOutsideTheDestinationIsRefused()
    {
        var archive = new TarBuilder()
            .Link("alias", "../../../../etc/passwd")
            .Build();

        // The same escape as a traversing name, spelled differently.
        Assert.False(Extract(archive, "out", (_, _) => true));
    }

    [Fact]
    public void ATruncatedArchiveIsAFailureRatherThanAnEnd()
    {
        var archive = new TarBuilder().File("a.txt", "A").BuildTruncated();

        // Half a Java runtime that reports success is worse than a failed download.
        Assert.False(Extract(archive[..(archive.Length - 200)], "out"));
    }

    [Fact]
    public void AHeaderWithAnUnreadableSizeIsRefused()
    {
        var archive = new TarBuilder().File("a.txt", "A").Build();

        // Corrupt the size field into something that is not octal.
        "zzzz"u8.CopyTo(archive.AsSpan(124, 4));

        Assert.False(Extract(archive, "out"));
    }

    // ================================================================== gzip

    [Fact]
    public void AGzippedTarIsUnpacked()
    {
        var archive = new TarBuilder()
            .Directory("jdk-21/")
            .Directory("jdk-21/bin/")
            .File("jdk-21/bin/java", "#!/bin/sh")
            .Build();

        var path = Path.Combine(_temp, "jre.tar.gz");

        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            gzip.Write(archive);
        }

        Assert.True(Tar.ExtractGz(path, Path.Combine(_temp, "out")));
        Assert.Equal("#!/bin/sh", Read("out", "bin/java"));
    }

    [Fact]
    public void ACorruptGzipIsAFailure()
    {
        var path = Path.Combine(_temp, "bogus.tar.gz");
        File.WriteAllText(path, "this is definitely not gzip");

        Assert.False(Tar.ExtractGz(path, Path.Combine(_temp, "out")));
        Assert.False(Tar.ExtractGz(Path.Combine(_temp, "missing.tar.gz"), Path.Combine(_temp, "out")));
    }

    [Fact]
    public void ABlockSpanningAGzipReadBoundaryIsStillReadWhole()
    {
        // GZipStream routinely returns less than asked for. A reader that trusted one Read call would
        // corrupt every archive larger than its internal buffer.
        var builder = new TarBuilder();

        for (var i = 0; i < 40; i++)
        {
            builder.File($"file{i}.txt", new string((char)('a' + (i % 26)), 700));
        }

        var path = Path.Combine(_temp, "many.tar.gz");

        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            gzip.Write(builder.Build());
        }

        Assert.True(Tar.ExtractGz(path, Path.Combine(_temp, "out")));

        Assert.Equal(new string('a', 700), Read("out", "file0.txt"));
        Assert.Equal(new string('n', 700), Read("out", "file39.txt"));
    }

    // ================================================================== permissions

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]  // The Skip below is the real guard; this tells the analyzer.
    public void TheExecuteBitSurvives()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix mode bits.");

        Assert.True(Extract(new TarBuilder().File("bin/java", "#!/bin/sh", mode: "0000755").Build(), "out"));

        var mode = File.GetUnixFileMode(Path.Combine(_temp, "out", "bin", "java"));

        // UPSTREAM BUG (#10) FIXED: upstream hands the octal-parsed value straight to QFile::Permissions,
        // whose flags are one hex nibble per class rather than one octal digit, so 0755 arrives as an
        // unrelated set of bits. bin/java without its execute bit is a runtime that cannot be launched.
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]  // The Skip below is the real guard; this tells the analyzer.
    public void OwnerReadAndWriteAreForcedOn()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix mode bits.");

        // Upstream's hack, kept: an archive with a read-only file in it would otherwise leave the
        // launcher unable to replace what it just wrote.
        Assert.True(Extract(new TarBuilder().File("locked.txt", "x", mode: "0000444").Build(), "out"));

        var mode = File.GetUnixFileMode(Path.Combine(_temp, "out", "locked.txt"));

        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
    }
}
