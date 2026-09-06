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
 * Characterization tests for the asset index. There is no upstream Qt test for AssetsUtils.
 */

using System.Text;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class AssetsIndexTests : IDisposable
{
    private const string ResourceBase = "https://resources.download.minecraft.net/";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-assets-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _client = new(new NullHandler());

    public AssetsIndexTests()
    {
        AssetsDir = Path.Combine(_temp, "assets");
        Directory.CreateDirectory(AssetsDir);
    }

    private string AssetsDir { get; }

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

    /// <summary>These tests only inspect the requests that get built, never send them.</summary>
    private sealed class NullHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No request should be sent.");
    }

    private const string SampleIndex = """
        {
          "objects": {
            "minecraft/sounds/step/grass1.ogg": { "hash": "bdf48ef6b5d0d23bbb02e17d04865216179f510a", "size": 3665 },
            "minecraft/lang/en_us.json": { "hash": "0123456789abcdef0123456789abcdef01234567", "size": 1024 }
          }
        }
        """;

    private static AssetsIndex Parse(string json, string id = "5")
        => AssetsIndex.FromJson(Json.RequireObject(Json.RequireDocument(json)), id);

    [Fact]
    public void ParsesObjects()
    {
        var index = Parse(SampleIndex);

        Assert.Equal("5", index.Id);
        Assert.Equal(2, index.Objects.Count);

        var sound = index.Objects["minecraft/sounds/step/grass1.ogg"];
        Assert.Equal("bdf48ef6b5d0d23bbb02e17d04865216179f510a", sound.Hash);
        Assert.Equal(3665, sound.Size);
    }

    [Fact]
    public void ObjectPathsFanOutByTheFirstTwoHashCharacters()
    {
        var asset = Parse(SampleIndex).Objects["minecraft/sounds/step/grass1.ogg"];

        // Keeps any single directory from holding tens of thousands of files.
        Assert.Equal("bd/bdf48ef6b5d0d23bbb02e17d04865216179f510a", asset.RelativePath);

        Assert.Equal(
            new Uri("https://resources.download.minecraft.net/bd/bdf48ef6b5d0d23bbb02e17d04865216179f510a"),
            asset.GetUrl(ResourceBase));
    }

    [Fact]
    public void LayoutFlagsDefaultToFalse()
    {
        var index = Parse(SampleIndex);

        Assert.False(index.IsVirtual);
        Assert.False(index.MapToResources);
    }

    [Fact]
    public void LayoutFlagsAreRead()
    {
        var virtualIndex = Parse("""{ "virtual": true, "objects": {} }""");
        Assert.True(virtualIndex.IsVirtual);

        var mapped = Parse("""{ "map_to_resources": true, "objects": {} }""");
        Assert.True(mapped.MapToResources);
    }

    [Fact]
    public void AnIndexWithNoObjectsIsStillValid()
        => Assert.Empty(Parse("""{ }""").Objects);

    // ================================================================== downloads

    [Fact]
    public void EveryMissingObjectIsDownloaded()
    {
        var downloads = Parse(SampleIndex).CreateDownloads(_client, AssetsDir, ResourceBase);

        Assert.Equal(2, downloads.Count);
    }

    [Fact]
    public void AnObjectAlreadyOnDiskAtTheRightSizeIsSkipped()
    {
        var index = Parse(SampleIndex);
        var asset = index.Objects["minecraft/lang/en_us.json"];

        var path = asset.GetLocalPath(AssetsDir);
        FileSystem.EnsureFilePathExists(path);
        File.WriteAllBytes(path, new byte[1024]);

        // Existence plus exact size, deliberately not a hash: rehashing thousands of files every
        // launch would cost more than it saves.
        Assert.Single(index.CreateDownloads(_client, AssetsDir, ResourceBase));
    }

    [Fact]
    public void AnObjectAtTheWrongSizeIsRedownloaded()
    {
        var index = Parse(SampleIndex);
        var asset = index.Objects["minecraft/lang/en_us.json"];

        var path = asset.GetLocalPath(AssetsDir);
        FileSystem.EnsureFilePathExists(path);

        // Truncated, as an interrupted download would leave it.
        File.WriteAllBytes(path, new byte[500]);

        Assert.Equal(2, index.CreateDownloads(_client, AssetsDir, ResourceBase).Count);
    }

    [Fact]
    public void ADownloadJobIsOnlyCreatedWhenThereIsWorkToDo()
    {
        var index = Parse(SampleIndex);

        Assert.NotNull(index.CreateDownloadJob(_client, AssetsDir, ResourceBase));

        foreach (var (_, asset) in index.Objects)
        {
            var path = asset.GetLocalPath(AssetsDir);
            FileSystem.EnsureFilePathExists(path);
            File.WriteAllBytes(path, new byte[asset.Size]);
        }

        Assert.Null(index.CreateDownloadJob(_client, AssetsDir, ResourceBase));
    }

    // ================================================================== layouts

    [Fact]
    public void AVirtualIndexPointsAtTheVirtualRoot()
    {
        var dir = Parse("""{ "virtual": true, "objects": {} }""").GetAssetsDir(AssetsDir, "/resources");

        Assert.EndsWith("virtual/5", FileSystem.CleanPath(dir), StringComparison.Ordinal);
    }

    [Fact]
    public void AMappedIndexPointsAtTheInstanceResourcesFolder()
        => Assert.Equal(
            "/resources",
            Parse("""{ "map_to_resources": true, "objects": {} }""").GetAssetsDir(AssetsDir, "/resources"));

    [Fact]
    public void ANormalIndexAlsoReturnsTheVirtualRoot()
    {
        // QUIRK, preserved: nothing is reconstructed there, but it is upstream's answer. Only feeds
        // the pre-1.7.3 ${game_assets} token, which modern versions ignore.
        var dir = Parse(SampleIndex).GetAssetsDir(AssetsDir, "/resources");

        Assert.EndsWith("virtual/5", FileSystem.CleanPath(dir), StringComparison.Ordinal);
    }

    [Fact]
    public void ReconstructingIsANoOpForANormalIndex()
        => Assert.Null(Parse(SampleIndex).ReconstructVirtualTree(AssetsDir, Path.Combine(_temp, "resources")));

    [Fact]
    public void ReconstructingAVirtualTreeCopiesObjectsToTheirLogicalNames()
    {
        var index = Parse("""
            {
              "virtual": true,
              "objects": { "minecraft/lang/en_us.lang": { "hash": "0123456789abcdef0123456789abcdef01234567", "size": 5 } }
            }
            """);

        var asset = index.Objects["minecraft/lang/en_us.lang"];
        var source = asset.GetLocalPath(AssetsDir);
        FileSystem.EnsureFilePathExists(source);
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("hello"));

        var target = index.ReconstructVirtualTree(AssetsDir, Path.Combine(_temp, "resources"));

        Assert.NotNull(target);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "minecraft", "lang", "en_us.lang")));
    }

    [Fact]
    public void ReconstructingSkipsObjectsThatAreNotDownloadedYet()
    {
        var index = Parse("""
            {
              "virtual": true,
              "objects": { "a/b.txt": { "hash": "0123456789abcdef0123456789abcdef01234567", "size": 5 } }
            }
            """);

        // Nothing in the object store, so nothing to copy — and no exception.
        var target = index.ReconstructVirtualTree(AssetsDir, Path.Combine(_temp, "resources"));

        Assert.NotNull(target);
        Assert.False(File.Exists(Path.Combine(target, "a", "b.txt")));
    }

    // ================================================================== loading

    [Fact]
    public void LoadsFromTheStandardIndexPath()
    {
        var path = AssetsIndex.IndexPath(AssetsDir, "5");
        FileSystem.EnsureFilePathExists(path);
        File.WriteAllText(path, SampleIndex);

        var index = AssetsIndex.Load(AssetsDir, "5");

        Assert.NotNull(index);
        Assert.Equal(2, index.Objects.Count);
    }

    [Fact]
    public void LoadingAMissingIndexReturnsNull()
        => Assert.Null(AssetsIndex.Load(AssetsDir, "nope"));

    [Fact]
    public void LoadingAMalformedIndexReturnsNullRatherThanThrowing()
    {
        var path = AssetsIndex.IndexPath(AssetsDir, "broken");
        FileSystem.EnsureFilePathExists(path);
        File.WriteAllText(path, "{ not json");

        Assert.Null(AssetsIndex.Load(AssetsDir, "broken"));
    }
}
