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
 * End-to-end for FlameImportTask with no real network: a hand-built CurseForge archive, a stub resolver
 * returning file releases, and an HttpClient whose handler serves the download URLs from memory. The
 * task should extract, resolve, stage and download into a working instance — and refuse a pack with a
 * blocked file, naming it.
 */

using System.IO.Compression;
using System.Net;
using System.Text;

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class FlameImportTaskTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-flameimport-" + Guid.NewGuid().ToString("N"));

    public FlameImportTaskTests() => Directory.CreateDirectory(_temp);

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

    private static RuntimeContext Context() => new()
    {
        System = "windows",
        JavaArchitecture = "64",
        JavaRealArchitecture = "x86_64",
    };

    private const string Manifest = """
        {
          "manifestType": "minecraftModpack", "manifestVersion": 1, "name": "Test Pack",
          "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "forge-47.1.0", "primary": true } ] },
          "files": [ { "projectID": 100, "fileID": 200, "required": true } ],
          "overrides": "overrides"
        }
        """;

    private string MakeArchive()
    {
        var path = Path.Combine(_temp, "pack-" + Guid.NewGuid().ToString("N") + ".zip");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
        {
            w.Write(Manifest);
        }

        using (var w = new StreamWriter(zip.CreateEntry("overrides/options.txt").Open()))
        {
            w.Write("fov:1.0");
        }

        return path;
    }

    /// <summary>A resolver that answers file 200 with a caller-supplied release.</summary>
    private sealed class StubResolver(IndexedVersion version) : IFlameResolverApi
    {
        public Task<IReadOnlyDictionary<int, IndexedVersion>> GetFilesAsync(
            IReadOnlyList<int> fileIds, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<int, IndexedVersion>>(
                new Dictionary<int, IndexedVersion> { [200] = version });

        public Task<IReadOnlyDictionary<int, IndexedPack>> GetProjectsAsync(
            IReadOnlyList<int> projectIds, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<int, IndexedPack>>(
                new Dictionary<int, IndexedPack> { [100] = new() { WebsiteUrl = "https://curseforge.com/p/cool" } });

        // No Modrinth fallback in these tests.
        public Task<IReadOnlyDictionary<string, IndexedVersion>> GetModrinthVersionsBySha1Async(
            IReadOnlyList<string> hashes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, IndexedVersion>>(new Dictionary<string, IndexedVersion>());
    }

    /// <summary>Serves a fixed body for any URL, so a download hits memory instead of the network.</summary>
    private sealed class MemoryHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task APackIsExtractedResolvedStagedAndDownloaded()
    {
        var staging = Path.Combine(_temp, "staging");
        Directory.CreateDirectory(staging);

        var version = new IndexedVersion { FileName = "cool.jar", DownloadUrl = "https://x.invalid/cool.jar" };
        using var client = new HttpClient(new MemoryHandler(Encoding.UTF8.GetBytes("JARDATA")));

        var task = new FlameImportTask(MakeArchive(), new StubResolver(version), client, Context())
        {
            StagingPath = staging,
        };

        Assert.True(await task.RunAsync());
        Assert.Equal(1, task.DownloadedCount);

        var paths = new InstancePaths(staging);

        // The overrides became the game folder.
        Assert.True(File.Exists(Path.Combine(paths.GameRoot, "options.txt")));

        // The resolved file was downloaded into the mods folder.
        var mod = Path.Combine(paths.GameRoot, "mods", "cool.jar");
        Assert.True(File.Exists(mod));
        Assert.Equal("JARDATA", File.ReadAllText(mod));

        // The instance was staged with the right components.
        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.20.1", profile.GetComponentVersion("net.minecraft"));
        Assert.Equal("47.1.0", profile.GetComponentVersion("net.minecraftforge"));

        Assert.Contains("name=Test Pack", File.ReadAllText(paths.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlockedFileFailsTheInstallAndIsNamed()
    {
        var staging = Path.Combine(_temp, "staging-blocked");
        Directory.CreateDirectory(staging);

        // No download URL: CurseForge withheld it, so it is a manual download.
        var version = new IndexedVersion { FileName = "secret.jar", DownloadUrl = "" };
        using var client = new HttpClient(new MemoryHandler([]));

        var task = new FlameImportTask(MakeArchive(), new StubResolver(version), client, Context())
        {
            StagingPath = staging,
        };

        Assert.False(await task.RunAsync());
        Assert.Contains("secret.jar", task.FailReason, StringComparison.Ordinal);
        Assert.Contains("by hand", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInstanceTakesItsNameFromTheManifestWhenNoneIsGiven()
    {
        var staging = Path.Combine(_temp, "staging-name");
        Directory.CreateDirectory(staging);

        var version = new IndexedVersion { FileName = "cool.jar", DownloadUrl = "https://x.invalid/cool.jar" };
        using var client = new HttpClient(new MemoryHandler(Encoding.UTF8.GetBytes("x")));

        var task = new FlameImportTask(MakeArchive(), new StubResolver(version), client, Context())
        {
            StagingPath = staging,
        };

        Assert.True(await task.RunAsync());
        Assert.Equal("Test Pack", ((IInstanceTask)task).Name);
    }
}
