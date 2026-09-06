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
 * Characterization tests for Java runtime metadata and installation. Upstream has no Qt test for any
 * of it, and none of it is testable against the real meta server, so everything here runs against a
 * stub handler and archives built in memory.
 */

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using Xunit;

namespace ExtremeLauncher.Java.Tests;

public sealed class JavaDownloadTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-jre-" + Guid.NewGuid().ToString("N"));

    public JavaDownloadTests() => Directory.CreateDirectory(_temp);

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

    /// <summary>Serves a fixed body per URL path, and counts what was asked for.</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = [];

        public RouteHandler Add(string path, byte[] body)
        {
            _routes[path] = body;
            return this;
        }

        public RouteHandler Add(string path, string body) => Add(path, Encoding.UTF8.GetBytes(body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;

            lock (Requested)
            {
                Requested.Add(path);
            }

            return Task.FromResult(_routes.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
        }
    }

    // ================================================================== metadata parsing

    private const string MetadataJson = """
        {
            "name": "jre-17.0.9",
            "vendor": "Eclipse Adoptium",
            "url": "https://example.invalid/jre-17.0.9.tar.gz",
            "releaseTime": "2023-10-17T12:00:00+00:00",
            "downloadType": "archive",
            "packageType": "jre",
            "runtimeOS": "linux-x64",
            "checksum": { "type": "sha256", "hash": "abcdef" },
            "version": { "name": "17.0.9+9", "major": 17, "minor": 0, "security": 9, "build": 9 }
        }
        """;

    [Fact]
    public void MetadataIsReadWhole()
    {
        var meta = JavaMetadata.Parse((JsonObject)JsonNode.Parse(MetadataJson)!);

        Assert.Equal("jre-17.0.9", meta.Name);
        Assert.Equal("Eclipse Adoptium", meta.Vendor);
        Assert.Equal(DownloadType.Archive, meta.DownloadType);
        Assert.Equal("jre", meta.PackageType);
        Assert.Equal("linux-x64", meta.RuntimeOS);
        Assert.Equal("sha256", meta.ChecksumType);
        Assert.Equal("abcdef", meta.ChecksumHash);

        Assert.Equal(17, meta.Version.Major);
        Assert.Equal(9, meta.Version.Security);
        Assert.Equal(2023, meta.ReleaseTime.Year);
    }

    [Theory]
    [InlineData("manifest", DownloadType.Manifest)]
    [InlineData("archive", DownloadType.Archive)]
    [InlineData("", DownloadType.Unknown)]
    [InlineData("something-else", DownloadType.Unknown)]
    public void TheDownloadShapeDecidesWhichInstallerRuns(string text, DownloadType expected)
    {
        Assert.Equal(expected, JavaMetadata.ParseDownloadType(text));

        // Round-trips for the two it knows; everything else collapses to "unknown".
        if (expected != DownloadType.Unknown)
        {
            Assert.Equal(text, JavaMetadata.DownloadTypeToString(expected));
        }
    }

    [Fact]
    public void AnEntryWithNothingInItStillParses()
    {
        var meta = JavaMetadata.Parse([]);

        // Defaulted rather than left blank, so a comparison against a real OS string simply misses.
        Assert.Equal("unknown", meta.RuntimeOS);
        Assert.Equal(DownloadType.Unknown, meta.DownloadType);
        Assert.Equal(default, meta.ReleaseTime);
    }

    [Fact]
    public void AMalformedChecksumBlockIsRefusedRatherThanIgnored()
    {
        // Silently skipping verification is the wrong failure here.
        Assert.Throws<JsonException>(
            () => JavaMetadata.Parse((JsonObject)JsonNode.Parse(@"{ ""checksum"": ""not-an-object"" }")!));
    }

    [Fact]
    public void MetadataOrdersByVersionThenDateThenName()
    {
        var older = JavaMetadata.Parse((JsonObject)JsonNode.Parse(
            @"{ ""name"": ""a"", ""version"": { ""major"": 17, ""minor"": 0, ""security"": 1 } }")!);

        var newer = JavaMetadata.Parse((JsonObject)JsonNode.Parse(
            @"{ ""name"": ""a"", ""version"": { ""major"": 21, ""minor"": 0, ""security"": 1 } }")!);

        Assert.True(older < newer);
        Assert.True(newer > older);

        var sameVersionLaterDate = JavaMetadata.Parse((JsonObject)JsonNode.Parse(
            @"{ ""name"": ""a"", ""releaseTime"": ""2024-01-01T00:00:00+00:00"",
                ""version"": { ""major"": 17, ""minor"": 0, ""security"": 1 } }")!);

        Assert.True(older < sameVersionLaterDate);
    }

    [Fact]
    public void NamesAreComparedNaturallyRatherThanOrdinally()
    {
        JavaMetadata Named(string name) => JavaMetadata.Parse((JsonObject)JsonNode.Parse($$"""
            { "name": "{{name}}", "version": { "major": 17, "minor": 0, "security": 0 } }
            """)!);

        // "java-11" before "java-100", the way a person would expect rather than the way a byte
        // comparison would.
        Assert.True(Named("java-11") < Named("java-100"));
    }

    [Fact]
    public void EqualityIgnoresVendorAndPlatform()
    {
        var linux = JavaMetadata.Parse((JsonObject)JsonNode.Parse(
            @"{ ""name"": ""jre-17"", ""runtimeOS"": ""linux-x64"", ""vendor"": ""A"",
                ""version"": { ""major"": 17, ""minor"": 0, ""security"": 0 } }")!);

        var windows = JavaMetadata.Parse((JsonObject)JsonNode.Parse(
            @"{ ""name"": ""jre-17"", ""runtimeOS"": ""windows-x64"", ""vendor"": ""B"",
                ""version"": { ""major"": 17, ""minor"": 0, ""security"": 0 } }")!);

        // INHERITED: version and name only. Worth knowing before using this to deduplicate a list that
        // spans platforms — these two are the same entry as far as it is concerned.
        Assert.Equal(linux, windows);
    }

    // ================================================================== archive installs

    private byte[] TarGzRuntime()
    {
        // A minimal runtime: one top-level folder holding bin/java.
        var tar = new MemoryStream();

        void Header(string name, int size, char type)
        {
            var header = new byte[512];

            void Put(int offset, string value, int length)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                Array.Copy(bytes, 0, header, offset, Math.Min(bytes.Length, length));
            }

            Put(0, name, 100);
            Put(100, "0000755", 8);
            Put(124, Convert.ToString(size, 8).PadLeft(11, '0'), 12);
            Put(136, "00000000000", 12);
            header[156] = (byte)type;
            Put(257, "ustar", 6);

            for (var i = 148; i < 156; i++)
            {
                header[i] = (byte)' ';
            }

            Put(148, Convert.ToString(header.Aggregate(0, (sum, b) => sum + b), 8).PadLeft(6, '0') + "\0 ", 8);
            tar.Write(header);
        }

        Header("jdk-17/", 0, '5');
        Header("jdk-17/bin/", 0, '5');

        var content = Encoding.UTF8.GetBytes("#!/bin/sh\nexec java\n");
        Header("jdk-17/bin/java", content.Length, '0');
        tar.Write(content);
        tar.Write(new byte[512 - content.Length]);

        tar.Write(new byte[1024]);

        var gz = new MemoryStream();

        using (var stream = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            stream.Write(tar.ToArray());
        }

        return gz.ToArray();
    }

    [Fact]
    public async Task ATarGzRuntimeIsDownloadedAndUnpacked()
    {
        var body = TarGzRuntime();
        var handler = new RouteHandler().Add("/jre.tar.gz", body);

        using var client = new HttpClient(handler);

        var task = new ArchiveDownloadTask(
            client,
            new Uri("https://example.invalid/jre.tar.gz"),
            Path_("runtime"),
            Path_("cache"));

        Assert.True(await task.RunAsync());

        // The archive's top-level folder is stripped, so bin/java is where everything else expects it.
        Assert.True(File.Exists(Path.Combine(_temp, "runtime", "bin", "java")));
    }

    [Fact]
    public async Task AZipRuntimeIsDownloadedAndUnpacked()
    {
        var zip = new MemoryStream();

        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("jdk-17/bin/java.exe").Open());
            writer.Write("MZ");
        }

        var handler = new RouteHandler().Add("/jre.zip", zip.ToArray());
        using var client = new HttpClient(handler);

        var task = new ArchiveDownloadTask(
            client,
            new Uri("https://example.invalid/jre.zip"),
            Path_("runtime"),
            Path_("cache"));

        Assert.True(await task.RunAsync());
        Assert.True(File.Exists(Path.Combine(_temp, "runtime", "bin", "java.exe")));
    }

    [Fact]
    public async Task AChecksumMismatchFailsTheInstall()
    {
        var handler = new RouteHandler().Add("/jre.tar.gz", TarGzRuntime());
        using var client = new HttpClient(handler);

        var task = new ArchiveDownloadTask(
            client,
            new Uri("https://example.invalid/jre.tar.gz"),
            Path_("runtime"),
            Path_("cache"),
            "sha256",
            Convert.ToHexString(SHA256.HashData("something else"u8.ToArray())));

        // A runtime that does not match its published hash is not one to run the game on.
        Assert.False(await task.RunAsync());
        Assert.False(Directory.Exists(Path.Combine(_temp, "runtime", "bin")));
    }

    [Fact]
    public async Task AMatchingChecksumPasses()
    {
        var body = TarGzRuntime();
        var handler = new RouteHandler().Add("/jre.tar.gz", body);
        using var client = new HttpClient(handler);

        var task = new ArchiveDownloadTask(
            client,
            new Uri("https://example.invalid/jre.tar.gz"),
            Path_("runtime"),
            Path_("cache"),
            "sha256",
            Convert.ToHexString(SHA256.HashData(body)));

        Assert.True(await task.RunAsync());
    }

    [Fact]
    public async Task AnUnrecognisedExtensionIsRefused()
    {
        var handler = new RouteHandler().Add("/jre.bin", TarGzRuntime());
        using var client = new HttpClient(handler);

        var task = new ArchiveDownloadTask(
            client,
            new Uri("https://example.invalid/jre.bin"),
            Path_("runtime"),
            Path_("cache"));

        // Decided by EXTENSION, not by content: a tarball served as .bin is refused rather than sniffed.
        Assert.False(await task.RunAsync());
        Assert.Contains("archive type", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedDownloadFailsTheTask()
    {
        using var client = new HttpClient(new RouteHandler());

        var task = new ArchiveDownloadTask(
            client,
            new Uri("https://example.invalid/missing.tar.gz"),
            Path_("runtime"),
            Path_("cache"));

        Assert.False(await task.RunAsync());
    }

    // ================================================================== manifest installs

    [Fact]
    public async Task AManifestRuntimeFetchesEveryFileItNames()
    {
        var manifest = """
            {
                "files": {
                    "bin": { "type": "directory" },
                    "bin/java": {
                        "type": "file",
                        "executable": true,
                        "downloads": { "raw": { "url": "https://example.invalid/files/java" } }
                    },
                    "release": {
                        "type": "file",
                        "downloads": { "raw": { "url": "https://example.invalid/files/release" } }
                    }
                }
            }
            """;

        var handler = new RouteHandler()
            .Add("/manifest.json", manifest)
            .Add("/files/java", "#!/bin/sh")
            .Add("/files/release", "JAVA_VERSION=17");

        using var client = new HttpClient(handler);

        var task = new ManifestDownloadTask(client, new Uri("https://example.invalid/manifest.json"), Path_("runtime"));

        Assert.True(await task.RunAsync());

        Assert.Equal("#!/bin/sh", await File.ReadAllTextAsync(Path.Combine(_temp, "runtime", "bin", "java")));
        Assert.Equal("JAVA_VERSION=17", await File.ReadAllTextAsync(Path.Combine(_temp, "runtime", "release")));
        Assert.True(Directory.Exists(Path.Combine(_temp, "runtime", "bin")));
    }

    [Fact]
    public async Task ManifestFilesAreVerifiedAgainstTheirHashes()
    {
        var content = "#!/bin/sh"u8.ToArray();

        var manifest = $$"""
            {
                "files": {
                    "bin/java": {
                        "type": "file",
                        "downloads": {
                            "raw": {
                                "url": "https://example.invalid/files/java",
                                "sha1": "{{Convert.ToHexString(SHA1.HashData("something else"u8.ToArray()))}}"
                            }
                        }
                    }
                }
            }
            """;

        var handler = new RouteHandler()
            .Add("/manifest.json", manifest)
            .Add("/files/java", content);

        using var client = new HttpClient(handler);

        var task = new ManifestDownloadTask(client, new Uri("https://example.invalid/manifest.json"), Path_("runtime"));

        Assert.False(await task.RunAsync());
    }

    [Fact]
    public async Task AManifestEntryEscapingTheDestinationIsRefused()
    {
        // The keys come out of a downloaded document; upstream combines them onto the destination
        // unchecked. A manifest naming ../../../.ssh/authorized_keys would write exactly there.
        var manifest = """
            {
                "files": {
                    "../escaped": {
                        "type": "file",
                        "downloads": { "raw": { "url": "https://example.invalid/files/evil" } }
                    }
                }
            }
            """;

        var handler = new RouteHandler()
            .Add("/manifest.json", manifest)
            .Add("/files/evil", "pwned");

        using var client = new HttpClient(handler);

        var task = new ManifestDownloadTask(client, new Uri("https://example.invalid/manifest.json"), Path_("runtime"));

        Assert.False(await task.RunAsync());

        // Refused by the guard specifically, not by some earlier accident.
        Assert.Contains("escapes the destination", task.FailReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_temp, "escaped")));

        // And nothing was fetched for it: the refusal happens before any download starts.
        Assert.DoesNotContain("/files/evil", handler.Requested);
    }

    [Fact]
    public async Task AnUnparseableManifestFailsTheTask()
    {
        var handler = new RouteHandler().Add("/manifest.json", "<html>404</html>");
        using var client = new HttpClient(handler);

        var task = new ManifestDownloadTask(client, new Uri("https://example.invalid/manifest.json"), Path_("runtime"));

        Assert.False(await task.RunAsync());
    }

    [Fact]
    public async Task AFileWithNoUsableUrlIsSkippedRatherThanFailing()
    {
        var manifest = """
            {
                "files": {
                    "bin/java": { "type": "file", "downloads": { "raw": { "url": "" } } },
                    "release": {
                        "type": "file",
                        "downloads": { "raw": { "url": "https://example.invalid/files/release" } }
                    }
                }
            }
            """;

        var handler = new RouteHandler()
            .Add("/manifest.json", manifest)
            .Add("/files/release", "JAVA_VERSION=17");

        using var client = new HttpClient(handler);

        var task = new ManifestDownloadTask(client, new Uri("https://example.invalid/manifest.json"), Path_("runtime"));

        Assert.True(await task.RunAsync());
        Assert.True(File.Exists(Path.Combine(_temp, "runtime", "release")));
    }

    // ================================================================== the macOS bundle layout

    [Fact]
    public async Task AnOrdinaryLayoutNeedsNoLinking()
    {
        var root = Path_("runtime");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(root, "bin", "java"), "#!/bin/sh");

        var linked = 0;

        Assert.True(await new SymlinkTask(root, (_, _) =>
        {
            linked++;
            return true;
        }).RunAsync());

        // Every non-macOS runtime takes this path, and so do macOS ones already flattened by a
        // previous run.
        Assert.Equal(0, linked);
    }

    [Fact]
    public async Task AMacBundleIsFlattenedIntoTheRuntimeRoot()
    {
        var root = Path_("runtime");
        var home = Path.Combine(root, "Contents", "Home");

        Directory.CreateDirectory(Path.Combine(home, "bin"));
        Directory.CreateDirectory(Path.Combine(home, "lib"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin", "java"), "#!/bin/sh");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=17");

        var linked = new List<(string From, string To)>();

        Assert.True(await new SymlinkTask(root, (from, to) =>
        {
            linked.Add((from, to));
            return true;
        }).RunAsync());

        // Linked rather than moved: moving would break the bundle's own structure and its signature.
        Assert.Equal(3, linked.Count);
        Assert.Contains(linked, l => l.To.EndsWith("bin", StringComparison.Ordinal));
        Assert.Contains(linked, l => l.To.EndsWith("release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABundleOneLevelDownIsStillFound()
    {
        var root = Path_("runtime");
        var home = Path.Combine(root, "jdk-17.jdk", "Contents", "Home");

        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin", "java"), "#!/bin/sh");

        var linked = 0;

        Assert.True(await new SymlinkTask(root, (_, _) =>
        {
            linked++;
            return true;
        }).RunAsync());

        Assert.Equal(1, linked);
    }

    [Fact]
    public async Task ARuntimeWithNoJavaBinaryAnywhereFails()
    {
        var root = Path_("runtime");
        Directory.CreateDirectory(Path.Combine(root, "lib"));

        var task = new SymlinkTask(root, (_, _) => true);

        Assert.False(await task.RunAsync());
        Assert.Contains("Failed to find Java binary path", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedLinkFailsTheTask()
    {
        var root = Path_("runtime");
        var home = Path.Combine(root, "Contents", "Home");

        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin", "java"), "#!/bin/sh");

        Assert.False(await new SymlinkTask(root, (_, _) => false).RunAsync());
    }
}
