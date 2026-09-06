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
 * Covers the getDownloads() half of tests/Library_test.cpp: test_legacy_url,
 * test_legacy_url_local_broken and test_legacy_url_local_override, plus the Mojang "downloads" block
 * cases upstream exercises through readMojangJson.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Net;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class LibraryDownloadTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-libdl-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _client = new(new NullHandler());

    public LibraryDownloadTests()
    {
        Directory.CreateDirectory(_temp);
        LibrariesDir = Path.Combine(_temp, "libraries");
        Directory.CreateDirectory(LibrariesDir);
    }

    private string LibrariesDir { get; }

    public void Dispose()
    {
        _client.Dispose();

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>Never actually invoked: these tests only inspect the requests that get built.</summary>
    private sealed class NullHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No request should be sent by these tests.");
    }

    private HttpMetaCache NewCache()
    {
        var cache = new HttpMetaCache(Path.Combine(_temp, "index.json"));
        cache.AddBase("libraries", LibrariesDir);
        return cache;
    }

    private static RuntimeContext Context(string system = "linux", string arch = "64", string realArch = "amd64")
        => new() { System = system, JavaArchitecture = arch, JavaRealArchitecture = realArch };

    [Fact]
    public void LegacyUrlIsDerivedFromTheRepositoryBase()
    {
        var test = new Library("test.package:testname:testversion") { RepositoryUrl = "file://foo/bar" };

        List<string> failed = [];
        var downloads = test.GetDownloads(Context(), _client, NewCache(), failed);

        Assert.Single(downloads);
        Assert.Empty(failed);
        Assert.Equal(
            new Uri("file://foo/bar/test/package/testname/testversion/testname-testversion.jar"),
            downloads[0].Url);
    }

    [Fact]
    public void ARepositoryBaseWithATrailingSlashDoesNotDoubleUp()
    {
        var test = new Library("a:b:1") { RepositoryUrl = "https://example.invalid/maven/" };

        var downloads = test.GetDownloads(Context(), _client, NewCache(), []);

        Assert.Equal(new Uri("https://example.invalid/maven/a/b/1/b-1.jar"), downloads[0].Url);
    }

    [Fact]
    public void AnAbsoluteUrlBypassesTheMavenLayout()
    {
        var test = new Library("a:b:1")
        {
            RepositoryUrl = "https://example.invalid/maven",
            AbsoluteUrl = "https://cdn.invalid/direct.jar",
        };

        var downloads = test.GetDownloads(Context(), _client, NewCache(), []);

        Assert.Equal(new Uri("https://cdn.invalid/direct.jar"), downloads[0].Url);
    }

    [Fact]
    public void WithNoRepositoryTheMojangLibraryBaseIsUsed()
    {
        var test = new Library("a:b:1");

        var downloads = test.GetDownloads(Context(), _client, NewCache(), []);

        Assert.StartsWith("https://libraries.minecraft.net/", downloads[0].Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingLocalFileIsReportedRatherThanDownloaded()
    {
        var test = new Library("test.package:testname:testversion") { Hint = "local" };

        List<string> failed = [];
        var downloads = test.GetDownloads(Context(), _client, NewCache(), failed, _temp);

        // Local libraries are never fetched; absence is the caller's problem to surface.
        Assert.Empty(downloads);
        Assert.Single(failed);
        Assert.EndsWith("testname-testversion.jar", failed[0], StringComparison.Ordinal);
    }

    [Fact]
    public void APresentLocalFileProducesNeitherDownloadNorFailure()
    {
        var test = new Library("com.paulscode:codecwav:20101023") { Hint = "local" };

        File.WriteAllText(Path.Combine(_temp, "codecwav-20101023.jar"), "jar contents");

        List<string> failed = [];
        var downloads = test.GetDownloads(Context(), _client, NewCache(), failed, _temp);

        Assert.Empty(downloads);
        Assert.Empty(failed);
    }

    [Fact]
    public void MojangDownloadsSupplyTheUrlAndChecksum()
    {
        var test = new Library("a:b:1")
        {
            MojangDownloads = new MojangLibraryDownloadInfo(new MojangDownloadInfo
            {
                Url = "https://libraries.minecraft.net/a/b/1/b-1.jar",
                Sha1 = "0123456789abcdef0123456789abcdef01234567",
                Size = 1234,
            }),
        };

        var downloads = test.GetDownloads(Context(), _client, NewCache(), []);

        Assert.Single(downloads);
        Assert.Equal(new Uri("https://libraries.minecraft.net/a/b/1/b-1.jar"), downloads[0].Url);
    }

    [Fact]
    public void AJavaLibraryWithNoArtifactIsSkipped()
    {
        var test = new Library("a:b:1") { MojangDownloads = new MojangLibraryDownloadInfo() };

        // Upstream logs and ignores rather than failing.
        Assert.Empty(test.GetDownloads(Context(), _client, NewCache(), []));
    }

    [Fact]
    public void NativeDownloadsComeFromTheClassifierMap()
    {
        var test = new Library("a:b:1");
        test.NativeClassifiers["linux"] = "natives-linux";

        var downloads = new MojangLibraryDownloadInfo();
        downloads.Classifiers["natives-linux"] = new MojangDownloadInfo
        {
            Url = "https://libraries.minecraft.net/a/b/1/b-1-natives-linux.jar",
        };
        test.MojangDownloads = downloads;

        var result = test.GetDownloads(Context(), _client, NewCache(), []);

        Assert.Single(result);
        Assert.EndsWith("b-1-natives-linux.jar", result[0].Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANativeWithNoClassifierForThisPlatformIsSkipped()
    {
        var test = new Library("a:b:1");
        test.NativeClassifiers["linux"] = "natives-linux";

        var downloads = new MojangLibraryDownloadInfo();
        downloads.Classifiers["natives-linux"] = new MojangDownloadInfo { Url = "https://example.invalid/x.jar" };
        test.MojangDownloads = downloads;

        // Windows: no matching native, so nothing to fetch -- and that is not an error.
        Assert.Empty(test.GetDownloads(Context("windows"), _client, NewCache(), []));
    }

    [Fact]
    public void ArchTokenNativesProduceTwoDownloads()
    {
        var test = new Library("a:b:1");
        test.NativeClassifiers["linux"] = "natives-linux-${arch}";

        var downloads = new MojangLibraryDownloadInfo();
        downloads.Classifiers["natives-linux-32"] = new MojangDownloadInfo { Url = "https://example.invalid/x-32.jar" };
        downloads.Classifiers["natives-linux-64"] = new MojangDownloadInfo { Url = "https://example.invalid/x-64.jar" };
        test.MojangDownloads = downloads;

        var result = test.GetDownloads(Context(), _client, NewCache(), []);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, d => d.Url.ToString().EndsWith("x-32.jar", StringComparison.Ordinal));
        Assert.Contains(result, d => d.Url.ToString().EndsWith("x-64.jar", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyArchTokenStorageProducesTwoDownloads()
    {
        var test = new Library("a:b:1") { RepositoryUrl = "https://example.invalid/maven" };
        test.NativeClassifiers["linux"] = "natives-${arch}";

        var result = test.GetDownloads(Context(), _client, NewCache(), []);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, d => d.Url.ToString().Contains("natives-32", StringComparison.Ordinal));
        Assert.Contains(result, d => d.Url.ToString().Contains("natives-64", StringComparison.Ordinal));
    }

    /*
     * REGRESSION, found by running the CLI against the live Prism meta server: 21 of 65 library
     * downloads 404'd, all of them LWJGL natives.
     *
     * The shape below is copied verbatim from org.lwjgl3/3.3.1.json. It is the one case where the
     * URL cannot be derived from the coordinate: the artifact id says "lwjgl-jemalloc-natives-
     * windows-x86", but the file actually lives under "lwjgl-jemalloc" with the platform as a
     * classifier suffix. Every other library in that document derives correctly by luck, which is
     * why nothing smaller than an end-to-end run caught this.
     *
     * Note there is no "natives" block -- Prism repackages these as ordinary libraries whose
     * coordinate happens to contain the word "natives", so isNative() is false and the plain
     * artifact URL is the one to use.
     */
    [Fact]
    public void ARepackagedNativeUsesTheSuppliedArtifactUrlRatherThanDerivingOne()
    {
        const string Document = """
            {
              "downloads": {
                "artifact": {
                  "sha1": "fb476c8ec110e1c137ad3ce8a7f7bfe6b11c6324",
                  "size": 110405,
                  "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl-jemalloc/3.3.1/lwjgl-jemalloc-3.3.1-natives-windows-x86.jar"
                }
              },
              "name": "org.lwjgl:lwjgl-jemalloc-natives-windows-x86:3.3.1",
              "rules": [ { "action": "allow", "os": { "name": "windows" } } ]
            }
            """;

        var problems = new ProblemContainer();
        var library = OneSixVersionFormat.LibraryFromJson(problems, Json.RequireObject(Json.RequireDocument(
            System.Text.Encoding.UTF8.GetBytes(Document), "test")), "test");

        var result = library.GetDownloads(
            Context(system: "windows", arch: "64", realArch: "x86_64"), _client, NewCache(), []);

        Assert.Single(result);
        Assert.Equal(
            new Uri("https://libraries.minecraft.net/org/lwjgl/lwjgl-jemalloc/3.3.1/lwjgl-jemalloc-3.3.1-natives-windows-x86.jar"),
            result[0].Url);
    }
}
