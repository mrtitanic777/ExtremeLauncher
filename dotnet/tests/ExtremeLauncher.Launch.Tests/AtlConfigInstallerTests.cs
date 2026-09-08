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
 * For the ATLauncher config-archive step (ATLPackInstallTask installConfigs/extractConfigs) and its
 * CDN URLs. The archive is fetched, checked against the version's sha1 when there is one, and unpacked
 * into the game folder — driven here with a stub HttpClient serving a zip from memory, no network.
 */

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Net;

using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AtlConfigInstallerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-atlcfg-" + Guid.NewGuid().ToString("N"));

    public AtlConfigInstallerTests() => Directory.CreateDirectory(_temp);

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

    private sealed class MemoryHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static byte[] ConfigZip()
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("config/foo.cfg").Open());
            w.Write("hello");
        }

        return stream.ToArray();
    }

    // ================================================================== URLs

    [Fact]
    public void TheVersionManifestAndConfigArchiveUrlsAreBuilt()
    {
        const string server = "https://atl.invalid/atl/";

        Assert.Equal(
            "https://atl.invalid/atl/packs/SkyFactory4/versions/4.2.2/Configs.json",
            AtlUrls.VersionManifest(server, "SkyFactory4", "4.2.2"));

        Assert.Equal(
            "https://atl.invalid/atl/packs/SkyFactory4/versions/4.2.2/Configs.zip",
            AtlUrls.ConfigArchive(server, "SkyFactory4", "4.2.2"));
    }

    // ================================================================== install

    [Fact]
    public async Task TheArchiveIsUnpackedIntoTheGameFolder()
    {
        var gameRoot = Path.Combine(_temp, "minecraft");
        using var client = new HttpClient(new MemoryHandler(ConfigZip()));

        await AtlConfigInstaller.InstallAsync(client, "https://x.invalid/Configs.zip", gameRoot, sha1: "");

        Assert.Equal("hello", File.ReadAllText(Path.Combine(gameRoot, "config", "foo.cfg")));
    }

    [Fact]
    public async Task AMatchingSha1IsAccepted()
    {
        var zip = ConfigZip();
        var sha1 = Convert.ToHexString(SHA1.HashData(zip)).ToLowerInvariant();

        var gameRoot = Path.Combine(_temp, "minecraft-ok");
        using var client = new HttpClient(new MemoryHandler(zip));

        await AtlConfigInstaller.InstallAsync(client, "https://x.invalid/Configs.zip", gameRoot, sha1);

        Assert.True(File.Exists(Path.Combine(gameRoot, "config", "foo.cfg")));
    }

    [Fact]
    public async Task AMismatchedSha1IsRejected()
    {
        var gameRoot = Path.Combine(_temp, "minecraft-bad");
        using var client = new HttpClient(new MemoryHandler(ConfigZip()));

        await Assert.ThrowsAsync<LauncherException>(() =>
            AtlConfigInstaller.InstallAsync(client, "https://x.invalid/Configs.zip", gameRoot, sha1: "deadbeef"));

        Assert.False(Directory.Exists(Path.Combine(gameRoot, "config")));
    }
}
