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
 * THE ORDERING IS WHAT THESE TEST. An update is an install plus a removal, and doing the removal first
 * — or doing it at all when the download failed — costs the user a working mod in exchange for
 * nothing. So the failure cases matter more here than the happy one.
 *
 * The HTTP layer is stubbed rather than mocked at the socket: what is being checked is what the task
 * does with a success and with a failure, not how it speaks HTTP.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class ResourceDownloadTaskTests : IDisposable
{
    private const string Content = "the mod jar";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-rdl-" + Guid.NewGuid().ToString("N"));

    private readonly string _mods;
    private readonly string _index;

    public ResourceDownloadTaskTests()
    {
        _mods = Path.Combine(_temp, "mods");
        _index = Path.Combine(_mods, ".index");

        Directory.CreateDirectory(_mods);
        Directory.CreateDirectory(_index);
    }

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

    /// <summary>Serves fixed bytes, or refuses.</summary>
    private sealed class StubHandler(bool succeed = true, string body = Content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(succeed
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
    }

    private HttpMetaCache NewCache()
    {
        var cache = new HttpMetaCache(Path.Combine(_temp, "index.json"));
        cache.AddBase("mods", _mods);

        return cache;
    }

    private static IndexedPack Pack() => new()
    {
        AddonId = "p1",
        Slug = "fabric-api",
        Name = "Fabric API",
        Provider = ResourceProvider.Modrinth,
    };

    private static IndexedVersion Version(string fileName, string hash = "", string hashType = "sha1")
    {
        var version = new IndexedVersion
        {
            AddonId = "p1",
            FileId = "v1",
            FileName = fileName,
            DownloadUrl = "https://cdn.modrinth.com/" + fileName,
            Hash = hash,
            HashType = hashType,
        };

        version.McVersion.Add("1.20.1");

        return version;
    }

    private ResourceDownloadTask Download(
        IndexedVersion version,
        bool succeed = true,
        bool isIndexed = true,
        string body = Content)
        => new(Pack(), version, _mods, _index, new HttpClient(new StubHandler(succeed, body)), NewCache(), isIndexed);

    // ================================================================== installing

    [Fact]
    public async Task AModIsDownloadedAndRecorded()
    {
        var task = Download(Version("fabric-api-0.83.jar"));

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        Assert.Equal(Content, File.ReadAllText(Path.Combine(_mods, "fabric-api-0.83.jar")));

        var metadata = Packwiz.GetIndexForMod(_index, "fabric-api");

        Assert.True(metadata.IsValid);
        Assert.Equal("fabric-api-0.83.jar", metadata.Filename);
    }

    /// <summary>A resource pack is installed without metadata; only mods are tracked.</summary>
    [Fact]
    public async Task AnUnindexedResourceGetsNoMetadata()
    {
        var task = Download(Version("shaders.zip"), isIndexed: false);

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        Assert.True(File.Exists(Path.Combine(_mods, "shaders.zip")));
        Assert.Empty(Directory.GetFiles(_index));
    }

    // ================================================================== updating

    /*
     * THE ORDER IS THE WHOLE POINT. An update is an install plus a removal, and the removal happens
     * last so a failed download leaves the instance with the version it started with.
     */
    [Fact]
    public async Task AnUpdateRemovesTheOldFileOnlyAfterTheNewOneArrives()
    {
        await InstallAsync("fabric-api-0.83.jar").ConfigureAwait(true);

        var task = Download(Version("fabric-api-0.90.jar"));

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        Assert.True(File.Exists(Path.Combine(_mods, "fabric-api-0.90.jar")));
        Assert.False(File.Exists(Path.Combine(_mods, "fabric-api-0.83.jar")));
        Assert.Equal("fabric-api-0.83.jar", task.ReplacedFileName);
    }

    /// <summary>The one that matters: a failed update must not cost the user a working mod.</summary>
    [Fact]
    public async Task AFailedUpdateLeavesTheOldFileInPlace()
    {
        await InstallAsync("fabric-api-0.83.jar").ConfigureAwait(true);

        var task = Download(Version("fabric-api-0.90.jar"), succeed: false);

        Assert.False(await task.RunAsync().ConfigureAwait(true));

        Assert.True(File.Exists(Path.Combine(_mods, "fabric-api-0.83.jar")));
        Assert.Equal(Content, File.ReadAllText(Path.Combine(_mods, "fabric-api-0.83.jar")));
        Assert.Equal(string.Empty, task.ReplacedFileName);
    }

    /*
     * Many updates keep the filename, in which case the download has already replaced the file --
     * deleting "the old one" would delete the new one.
     */
    [Fact]
    public async Task AnUpdateThatKeepsItsFilenameDeletesNothing()
    {
        await InstallAsync("fabric-api.jar").ConfigureAwait(true);

        var task = Download(Version("fabric-api.jar"), body: "the newer jar");

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        Assert.Equal("the newer jar", File.ReadAllText(Path.Combine(_mods, "fabric-api.jar")));
        Assert.Equal(string.Empty, task.ReplacedFileName);
    }

    /// <summary>A user may have turned the mod off before updating it.</summary>
    [Fact]
    public async Task ADisabledOldFileIsRemovedToo()
    {
        await InstallAsync("fabric-api-0.83.jar").ConfigureAwait(true);

        File.Move(
            Path.Combine(_mods, "fabric-api-0.83.jar"),
            Path.Combine(_mods, "fabric-api-0.83.jar.disabled"));

        Assert.True(await Download(Version("fabric-api-0.90.jar")).RunAsync().ConfigureAwait(true));

        Assert.False(File.Exists(Path.Combine(_mods, "fabric-api-0.83.jar.disabled")));
        Assert.True(File.Exists(Path.Combine(_mods, "fabric-api-0.90.jar")));
    }

    [Fact]
    public async Task AFirstInstallReplacesNothing()
    {
        var task = Download(Version("fabric-api-0.83.jar"));

        await task.RunAsync().ConfigureAwait(true);

        Assert.Equal(string.Empty, task.ReplacedFileName);
    }

    // ================================================================== verification

    [Fact]
    public async Task AGoodChecksumPasses()
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Content))).ToLowerInvariant();

        Assert.True(await Download(Version("a.jar", hash)).RunAsync().ConfigureAwait(true));
    }

    [Fact]
    public async Task ABadChecksumFailsAndLeavesNoFile()
    {
        var wrong = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("something else"))).ToLowerInvariant();

        Assert.False(await Download(Version("a.jar", wrong)).RunAsync().ConfigureAwait(true));
        Assert.False(File.Exists(Path.Combine(_mods, "a.jar")));
    }

    /*
     * MURMUR2 IS NOT A CHECKSUM THE LAUNCHER CAN VERIFY -- it is CurseForge's file fingerprint, not a
     * digest, and no validator speaks it. A CurseForge download whose only hash is murmur2 is
     * installed UNVERIFIED. That is the format's limitation rather than a choice made here, and it is
     * pinned so nobody assumes otherwise.
     */
    [Fact]
    public async Task AMurmur2HashInstallsWithoutVerification()
    {
        var task = Download(Version("a.jar", "1540447798", "murmur2"));

        Assert.True(await task.RunAsync().ConfigureAwait(true));
        Assert.True(File.Exists(Path.Combine(_mods, "a.jar")));
    }

    /// <summary>A malformed hash cannot verify anything; blocking the install over it helps nobody.</summary>
    [Fact]
    public async Task AMalformedHashInstallsWithoutVerification()
        => Assert.True(await Download(Version("a.jar", "not-hex", "sha1")).RunAsync().ConfigureAwait(true));

    private async Task InstallAsync(string fileName)
        => Assert.True(await Download(Version(fileName)).RunAsync().ConfigureAwait(true));
}
