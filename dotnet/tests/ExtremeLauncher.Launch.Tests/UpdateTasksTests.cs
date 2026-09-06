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
 * Characterization tests for the game-update tasks. Upstream has no Qt test for any of them.
 *
 * These are what stand between a resolved profile and a runnable game, so the assertions are mostly
 * about WHAT gets fetched rather than how — a missing artifact pool shows up as a NoClassDefFoundError
 * at runtime rather than as a launcher error, which is far harder for a user to report usefully.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Net;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class UpdateTasksTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-update-" + Guid.NewGuid().ToString("N"));

    public UpdateTasksTests() => Directory.CreateDirectory(_temp);

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

    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    /// <summary>Serves a body per path and records what was asked for.</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = [];

        public RouteHandler Add(string path, string body)
        {
            _routes[path] = Encoding.UTF8.GetBytes(body);
            return this;
        }

        public RouteHandler Add(string path, byte[] body)
        {
            _routes[path] = body;
            return this;
        }

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

    private HttpMetaCache NewCache()
    {
        var cache = new HttpMetaCache(Path_("index-cache.json"));

        cache.AddBase("libraries", Path_("libraries"));
        cache.AddBase("assets", Path_("assets"));
        cache.AddBase("asset_indexes", Path_("assets/indexes"));
        cache.AddBase("fmllibs", Path_("fmllibs"));

        return cache;
    }

    private InstancePaths NewInstance(string name = "instance")
    {
        var root = Path_(name);
        Directory.CreateDirectory(root);

        return new InstancePaths(root);
    }

    private static LaunchProfile ProfileWith(Action<VersionFile> configure)
    {
        var profile = new LaunchProfile();
        var patch = new VersionFile { Uid = "net.minecraft", Version = "1.20.1", MainClass = "Main" };

        configure(patch);
        profile.Apply(patch, Context());

        return profile;
    }

    // ================================================================== FoldersTask

    [Fact]
    public async Task TheGameFolderIsCreated()
    {
        var paths = NewInstance();

        Assert.True(await new FoldersTask(paths).RunAsync());
        Assert.True(Directory.Exists(paths.GameRoot));
    }

    [Fact]
    public async Task CreatingTheGameFolderIsIdempotent()
    {
        var paths = NewInstance();

        Assert.True(await new FoldersTask(paths).RunAsync());
        Assert.True(await new FoldersTask(paths).RunAsync());
    }

    // ================================================================== LibrariesTask

    [Fact]
    public async Task EveryArtifactPoolIsFetched()
    {
        var handler = new RouteHandler()
            .Add("/com/google/guava/guava/31.1-jre/guava-31.1-jre.jar", "guava")
            .Add("/org/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-linux.jar", "natives")
            .Add("/com/mojang/minecraft/1.20.1/minecraft-1.20.1-client.jar", "main");

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://libraries.invalid/") };

        var native = new Library("org.lwjgl:lwjgl-platform:2.9.4") { RepositoryUrl = "https://libraries.invalid/" };
        native.NativeClassifiers["linux"] = "natives-linux";

        var profile = ProfileWith(patch =>
        {
            patch.Libraries.Add(new Library("com.google.guava:guava:31.1-jre") { RepositoryUrl = "https://libraries.invalid/" });
            patch.Libraries.Add(native);
            patch.MainJar = new Library("com.mojang:minecraft:1.20.1:client") { RepositoryUrl = "https://libraries.invalid/" };
        });

        var task = new LibrariesTask(NewInstance(), profile, Context(), client, NewCache(), "Test");

        Assert.True(await task.RunAsync());

        // Classpath library, native, and the main jar. Missing any pool means the game starts with a
        // NoClassDefFoundError rather than a launcher error.
        Assert.Contains("/com/google/guava/guava/31.1-jre/guava-31.1-jre.jar", handler.Requested);
        Assert.Contains("/org/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-linux.jar", handler.Requested);
        Assert.Contains("/com/mojang/minecraft/1.20.1/minecraft-1.20.1-client.jar", handler.Requested);
    }

    [Fact]
    public async Task AMissingLocalArtifactIsReportedBeforeAnythingIsFetched()
    {
        var handler = new RouteHandler();
        using var client = new HttpClient(handler);

        var profile = ProfileWith(patch =>
            patch.Libraries.Add(new Library("com.example:proprietary:1.0") { Hint = "local" }));

        var task = new LibrariesTask(NewInstance(), profile, Context(), client, NewCache(), "Test");

        Assert.False(await task.RunAsync());

        // A "local" artifact is one the user must supply — there is nothing to download, so it is
        // reported as a missing file with instructions rather than as a failed request.
        Assert.Contains("marked as 'local' are missing", task.FailReason, StringComparison.Ordinal);
        Assert.Contains("correct this problem manually", task.FailReason, StringComparison.Ordinal);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task AFailedLibraryDownloadNamesTheReason()
    {
        // Nothing is served, so the request 404s.
        using var client = new HttpClient(new RouteHandler());

        var profile = ProfileWith(patch =>
            patch.Libraries.Add(new Library("com.google.guava:guava:31.1-jre") { RepositoryUrl = "https://libraries.invalid/" }));

        var task = new LibrariesTask(NewInstance(), profile, Context(), client, NewCache(), "Test");

        Assert.False(await task.RunAsync());
        Assert.Contains("impossible to fetch the required libraries", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProfileWithNoLibrariesSucceedsWithoutRequests()
    {
        var handler = new RouteHandler();
        using var client = new HttpClient(handler);

        var profile = ProfileWith(_ => { });

        Assert.True(await new LibrariesTask(NewInstance(), profile, Context(), client, NewCache()).RunAsync());
        Assert.Empty(handler.Requested);
    }

    // ================================================================== AssetUpdateTask

    /// <summary>
    /// The SHA-1 of an object's contents, which in a content-addressed store IS its name.
    /// </summary>
    /// <remarks>
    /// Computed rather than made up, because the downloads carry a checksum validator built from the
    /// index's own hash — a fixture with an invented hash fails validation, correctly.
    /// </remarks>
    private static string HashOf(string content)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string AssetIndexJson(params (string Name, string Hash, int Size)[] objects)
    {
        var entries = objects.Select(o =>
            $$"""    "{{o.Name}}": { "hash": "{{o.Hash}}", "size": {{o.Size}} }""");

        return $$"""
            { "objects": {
            {{string.Join(",\n", entries)}}
            } }
            """;
    }

    [Fact]
    public async Task TheIndexIsFetchedThenEveryAssetItNames()
    {
        // Content-addressed: an object lives at <first two hex chars>/<full hash>, and the hash is
        // the SHA-1 of its contents.
        var hashA = HashOf("aaaa");
        var hashB = HashOf("bbbb");

        var index = AssetIndexJson(("minecraft/sounds/a.ogg", hashA, 4), ("minecraft/lang/b.json", hashB, 4));

        var handler = new RouteHandler()
            .Add("/5.json", index)
            .Add($"/{hashA[..2]}/{hashA}", "aaaa")
            .Add($"/{hashB[..2]}/{hashB}", "bbbb");

        using var client = new HttpClient(handler);

        var profile = ProfileWith(patch => patch.MojangAssetIndex = new MojangAssetIndexInfo
        {
            Id = "5",
            Url = "https://assets.invalid/5.json",
        });

        var task = new AssetUpdateTask(profile, client, NewCache(), Path_("assets"), "https://resources.invalid/", "Test");

        Assert.True(await task.RunAsync());

        // TWO ROUNDS, and they cannot be merged: what to fetch second is not known until the first
        // has finished and parsed.
        Assert.Equal("/5.json", handler.Requested[0]);
        Assert.Contains($"/{hashA[..2]}/{hashA}", handler.Requested);
        Assert.Contains($"/{hashB[..2]}/{hashB}", handler.Requested);
    }

    [Fact]
    public async Task AProfileWithNoAssetIndexIsNotAFailure()
    {
        var handler = new RouteHandler();
        using var client = new HttpClient(handler);

        // Some patches carry no assets at all; that is a valid state, not a missing download.
        Assert.True(await new AssetUpdateTask(ProfileWith(_ => { }), client, NewCache(), Path_("assets")).RunAsync());
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task AnIndexThatFailsItsChecksumFailsTheTask()
    {
        var handler = new RouteHandler().Add("/5.json", AssetIndexJson(("a", new string('a', 40), 1)));
        using var client = new HttpClient(handler);

        var profile = ProfileWith(patch => patch.MojangAssetIndex = new MojangAssetIndexInfo
        {
            Id = "5",
            Url = "https://assets.invalid/5.json",
            Sha1 = Convert.ToHexString(SHA1.HashData("something else"u8.ToArray())).ToLowerInvariant(),
        });

        var task = new AssetUpdateTask(profile, client, NewCache(), Path_("assets"));

        Assert.False(await task.RunAsync());
        Assert.Contains("Failed to download the assets index", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorruptIndexIsEvictedRatherThanLeftCached()
    {
        var handler = new RouteHandler().Add("/5.json", "{ this is not json");
        using var client = new HttpClient(handler);

        var profile = ProfileWith(patch => patch.MojangAssetIndex = new MojangAssetIndexInfo
        {
            Id = "5",
            Url = "https://assets.invalid/5.json",
        });

        var indexPath = Path.Combine(Path_("assets"), "indexes", "5.json");
        var task = new AssetUpdateTask(profile, client, NewCache(), Path_("assets"));

        Assert.False(await task.RunAsync());

        // Left cached, it would be re-read and re-rejected on every launch with no way for the user
        // to break the loop.
        Assert.False(File.Exists(indexPath));
    }

    [Fact]
    public async Task AssetsAlreadyOnDiskAreNotRefetched()
    {
        var hash = HashOf("cccc");

        var handler = new RouteHandler()
            .Add("/5.json", AssetIndexJson(("minecraft/a.ogg", hash, 4)))
            .Add($"/{hash[..2]}/{hash}", "cccc");

        using var client = new HttpClient(handler);

        var profile = ProfileWith(patch => patch.MojangAssetIndex = new MojangAssetIndexInfo
        {
            Id = "5",
            Url = "https://assets.invalid/5.json",
        });

        var assets = Path_("assets");

        Assert.True(await new AssetUpdateTask(profile, client, NewCache(), assets, "https://resources.invalid/").RunAsync());

        handler.Requested.Clear();

        Assert.True(await new AssetUpdateTask(profile, client, NewCache(), assets, "https://resources.invalid/").RunAsync());

        // The index is re-fetched (it has no eternal marking), but a content-addressed object that is
        // already on disk cannot have changed.
        Assert.DoesNotContain($"/{hash[..2]}/{hash}", handler.Requested);
    }

    // ================================================================== FMLLibrariesTask

    private const string FmlBase = "https://fmllibs.invalid/";

    [Fact]
    public async Task ModernVersionsNeedNoLooseLibraries()
    {
        var handler = new RouteHandler();
        using var client = new HttpClient(handler);

        var task = new FMLLibrariesTask(NewInstance(), "1.20.1", hasForge: true, client, NewCache(), FmlBase);

        Assert.True(await task.RunAsync());
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task AnOldVersionWithoutForgeNeedsNoLooseLibrariesEither()
    {
        var handler = new RouteHandler();
        using var client = new HttpClient(handler);

        // The mapping has an entry for 1.4.7, but only Forge looks for these files.
        var task = new FMLLibrariesTask(NewInstance(), "1.4.7", hasForge: false, client, NewCache(), FmlBase);

        Assert.True(await task.RunAsync());
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task OldForgeGetsItsLooseLibrariesCopiedIntoTheInstance()
    {
        var handler = new RouteHandler();

        foreach (var lib in FMLLibrariesTask.Mapping["1.4.7"])
        {
            handler.Add("/" + lib.Filename, lib.Filename + " contents");
        }

        using var client = new HttpClient(handler);

        var paths = NewInstance();
        var task = new FMLLibrariesTask(paths, "1.4.7", hasForge: true, client, NewCache(), FmlBase);

        Assert.True(await task.RunAsync());

        // COPIED into the instance rather than linked: FML resolves these by path inside the instance,
        // and a link into the shared cache would let one instance's changes reach every other.
        foreach (var lib in FMLLibrariesTask.Mapping["1.4.7"])
        {
            var path = Path.Combine(paths.LibDir, lib.Filename);

            Assert.True(File.Exists(path));
            Assert.Equal(lib.Filename + " contents", await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public async Task LibrariesAlreadyInPlaceAreNotFetched()
    {
        var paths = NewInstance();
        Directory.CreateDirectory(paths.LibDir);

        foreach (var lib in FMLLibrariesTask.Mapping["1.3.2"])
        {
            await File.WriteAllTextAsync(Path.Combine(paths.LibDir, lib.Filename), "already here");
        }

        var handler = new RouteHandler();
        using var client = new HttpClient(handler);

        Assert.True(await new FMLLibrariesTask(paths, "1.3.2", true, client, NewCache(), FmlBase).RunAsync());

        // These files never change, so a present one is a correct one.
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task OnlyTheMissingLibrariesAreFetched()
    {
        var paths = NewInstance();
        Directory.CreateDirectory(paths.LibDir);

        var libs = FMLLibrariesTask.Mapping["1.3.2"];
        await File.WriteAllTextAsync(Path.Combine(paths.LibDir, libs[0].Filename), "already here");

        var handler = new RouteHandler();

        foreach (var lib in libs.Skip(1))
        {
            handler.Add("/" + lib.Filename, "fetched");
        }

        using var client = new HttpClient(handler);

        Assert.True(await new FMLLibrariesTask(paths, "1.3.2", true, client, NewCache(), FmlBase).RunAsync());

        Assert.DoesNotContain("/" + libs[0].Filename, handler.Requested);
        Assert.Equal(libs.Count - 1, handler.Requested.Count);
    }

    [Fact]
    public async Task AFailedLooseLibraryDownloadNamesTheFiles()
    {
        // Nothing is served.
        using var client = new HttpClient(new RouteHandler());

        var task = new FMLLibrariesTask(NewInstance(), "1.3.2", true, client, NewCache(), FmlBase);

        Assert.False(await task.RunAsync());
        Assert.Contains("argo-2.25.jar", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMappingIsFrozenData()
    {
        // Filenames and hashes fixed when those versions shipped; these are not tunable.
        Assert.Equal(3, FMLLibrariesTask.Mapping["1.3.2"].Count);
        Assert.Equal(4, FMLLibrariesTask.Mapping["1.4.7"].Count);
        Assert.Equal(6, FMLLibrariesTask.Mapping["1.5.2"].Count);

        // The deobfuscation data is the version-specific member of the 1.5 set.
        Assert.Contains(FMLLibrariesTask.Mapping["1.5.1"], l => l.Filename == "deobfuscation_data_1.5.1.zip");
        Assert.Contains(FMLLibrariesTask.Mapping["1.5.2"], l => l.Filename == "deobfuscation_data_1.5.2.zip");

        Assert.False(FMLLibrariesTask.Mapping.ContainsKey("1.6.4"));
    }
}
