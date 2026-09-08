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
 * End-to-end for AtlInstallTask with no network: a stub HttpClient routes the manifest, the config
 * archive and each mod URL to bytes held in memory. The task should fetch the manifest, lay the config
 * archive over the game folder, download the mods into place, and stage a working instance — and refuse
 * a pack with a browser-only mod, naming it.
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

public sealed class AtlInstallTaskTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-atltask-" + Guid.NewGuid().ToString("N"));

    public AtlInstallTaskTests() => Directory.CreateDirectory(_temp);

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

    private const string Server = "https://atl.invalid/atl/";

    /// <summary>Serves a fixed body per URL; a URL with no mapping is a 404.</summary>
    private sealed class MapHandler(Dictionary<string, byte[]> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            return Task.FromResult(routes.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] ConfigZip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("config/pack.cfg").Open());
            w.Write("cfg");
        }

        return stream.ToArray();
    }

    private static string Manifest(string modsJson, bool withConfigs) => $$"""
        {
          "version": "1.0", "minecraft": "1.12.2",
          "loader": { "type": "forge", "choose": false, "metadata": { "version": "14.23.5.2860" } },
          {{(withConfigs ? "\"configs\": { \"filesize\": 100, \"sha1\": \"\" }," : "")}}
          "mods": {{modsJson}}
        }
        """;

    [Fact]
    public async Task APackIsResolvedConfiguredDownloadedAndStaged()
    {
        var staging = Path.Combine(_temp, "staging");
        Directory.CreateDirectory(staging);

        var mods = """
            [ { "name": "JEI", "version": "1.0", "url": "https://direct.invalid/jei.jar",
                "file": "jei.jar", "md5": "", "download": "direct", "type": "mods", "client": true } ]
            """;

        var routes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [AtlUrls.VersionManifest(Server, "Test", "1.0")] = Encoding.UTF8.GetBytes(Manifest(mods, withConfigs: true)),
            [AtlUrls.ConfigArchive(Server, "Test", "1.0")] = ConfigZip(),
            ["https://direct.invalid/jei.jar"] = Encoding.UTF8.GetBytes("JEIDATA"),
        };

        using var client = new HttpClient(new MapHandler(routes));

        var task = new AtlInstallTask("Test Pack", "Test", "1.0", client, Context(), serverBaseUrl: Server)
        {
            StagingPath = staging,
        };

        Assert.True(await task.RunAsync());

        var paths = new InstancePaths(staging);

        // The config archive was laid over the game folder.
        Assert.True(File.Exists(Path.Combine(paths.GameRoot, "config", "pack.cfg")));

        // The mod was downloaded into the mods folder.
        Assert.Equal("JEIDATA", File.ReadAllText(Path.Combine(paths.GameRoot, "mods", "jei.jar")));

        // The instance was staged with Minecraft + Forge and the managed-pack fields.
        var profile = new PackProfile(Context());
        Assert.True(profile.Load(paths.PackProfilePath));
        Assert.Equal("1.12.2", profile.GetComponentVersion("net.minecraft"));
        Assert.Equal("14.23.5.2860", profile.GetComponentVersion("net.minecraftforge"));

        var cfg = File.ReadAllText(paths.ConfigPath);
        Assert.Contains("name=Test Pack", cfg, StringComparison.Ordinal);
        Assert.Contains("ManagedPackType=atlauncher", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrowserOnlyModFailsTheInstallAndIsNamed()
    {
        var staging = Path.Combine(_temp, "staging-blocked");
        Directory.CreateDirectory(staging);

        var mods = """
            [ { "name": "SecretMod", "version": "1.0", "url": "https://curseforge.invalid/p/x",
                "file": "secret.jar", "md5": "", "download": "browser", "type": "mods", "client": true } ]
            """;

        var routes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [AtlUrls.VersionManifest(Server, "Test", "1.0")] = Encoding.UTF8.GetBytes(Manifest(mods, withConfigs: false)),
        };

        using var client = new HttpClient(new MapHandler(routes));

        var task = new AtlInstallTask("Test Pack", "Test", "1.0", client, Context(), serverBaseUrl: Server)
        {
            StagingPath = staging,
        };

        Assert.False(await task.RunAsync());
        Assert.Contains("SecretMod", task.FailReason, StringComparison.Ordinal);
        Assert.Contains("by hand", task.FailReason, StringComparison.Ordinal);
    }
}
