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
 * The provider is faked and the hash function is injected, so these test the FLOW: which files get
 * skipped, which get looked up, how many requests that costs, and what lands on disk. That is the
 * half upstream buries in a signal graph, and the half that decides whether a user's mods folder
 * comes back with working update checks.
 *
 * Metadata is written for real, into a temp directory, and read back through the packwiz reader --
 * asserting the object I just built would prove nothing about the file that ends up beside the pack.
 */

using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class EnsureMetadataTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-meta-" + Guid.NewGuid().ToString("N"));

    public EnsureMetadataTests() => Directory.CreateDirectory(_temp);

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

    /// <summary>Answers from a script, and records what it was asked.</summary>
    private sealed class FakeProvider : IMetadataProvider
    {
        private readonly Dictionary<string, IndexedVersion> _versions;
        private readonly Dictionary<string, IndexedPack> _projects;

        public FakeProvider(
            ResourceProvider provider,
            Dictionary<string, IndexedVersion>? versions = null,
            Dictionary<string, IndexedPack>? projects = null)
        {
            Provider = provider;
            _versions = versions ?? [];
            _projects = projects ?? [];
        }

        public ResourceProvider Provider { get; }

        public List<IReadOnlyList<string>> HashLookups { get; } = [];

        public List<IReadOnlyList<string>> ProjectLookups { get; } = [];

        public Task<IReadOnlyDictionary<string, IndexedVersion>> GetVersionsByHashAsync(
            IReadOnlyList<string> hashes,
            CancellationToken cancellationToken)
        {
            HashLookups.Add(hashes);

            IReadOnlyDictionary<string, IndexedVersion> found = _versions
                .Where(kv => hashes.Contains(kv.Key, StringComparer.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            return Task.FromResult(found);
        }

        public Task<IReadOnlyDictionary<string, IndexedPack>> GetProjectsAsync(
            IReadOnlyList<string> addonIds,
            CancellationToken cancellationToken)
        {
            ProjectLookups.Add(addonIds);

            IReadOnlyDictionary<string, IndexedPack> found = _projects
                .Where(kv => addonIds.Contains(kv.Key, StringComparer.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            return Task.FromResult(found);
        }
    }

    private static IndexedVersion Version(string addonId, string fileId = "v1", string hash = "deadbeef")
    {
        var version = new IndexedVersion
        {
            AddonId = addonId,
            FileId = fileId,
            Hash = hash,
            HashType = "sha512",
            DownloadUrl = "https://cdn.modrinth.com/file.jar",
            VersionType = VersionType.Release,
            Loaders = ModLoaderTypes.Fabric,
        };

        version.McVersion.Add("1.20.1");

        return version;
    }

    private static IndexedPack Pack(string addonId, string slug, ResourceProvider provider = ResourceProvider.Modrinth)
        => new() { AddonId = addonId, Slug = slug, Name = slug, Provider = provider, Side = "both" };

    // ================================================================== what gets skipped

    /// <summary>A file that already has this provider's metadata is never hashed or looked up.</summary>
    [Fact]
    public async Task AFileThatAlreadyHasMetadataForThisProviderIsLeftAlone()
    {
        var provider = new FakeProvider(ResourceProvider.Modrinth);

        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("a.jar", "A", HasMetadataFor: ResourceProvider.Modrinth)],
            _temp,
            provider,
            hashFile: _ => throw new InvalidOperationException("should not be hashed")).ConfigureAwait(true);

        Assert.Single(result.Ready);
        Assert.Empty(result.Failed);

        // Nothing was asked of the provider at all.
        Assert.Empty(provider.HashLookups);
    }

    /// <summary>Metadata for the OTHER provider does not count — it is the wrong record.</summary>
    [Fact]
    public async Task MetadataFromADifferentProviderDoesNotCount()
    {
        var provider = new FakeProvider(
            ResourceProvider.Modrinth,
            versions: new() { ["h1"] = Version("p1") },
            projects: new() { ["p1"] = Pack("p1", "mod-a") });

        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("a.jar", "A", HasMetadataFor: ResourceProvider.Flame)],
            _temp,
            provider,
            hashFile: _ => "h1").ConfigureAwait(true);

        Assert.Single(result.Ready);
        Assert.Single(provider.HashLookups);
    }

    /// <summary>Folders have no metadata, so they are ready without being asked about.</summary>
    [Fact]
    public async Task AFolderIsReadyWithoutALookup()
    {
        var provider = new FakeProvider(ResourceProvider.Modrinth);

        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("mods/folder", "Folder", IsFolder: true)],
            _temp,
            provider,
            hashFile: _ => throw new InvalidOperationException("should not be hashed")).ConfigureAwait(true);

        Assert.Single(result.Ready);
        Assert.Empty(provider.HashLookups);
    }

    /// <summary>Invalidity is checked before folder-ness, so an unreadable folder fails.</summary>
    [Fact]
    public async Task AnInvalidResourceFailsEvenIfItIsAFolder()
    {
        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("broken", "Broken", IsFolder: true, IsValid: false)],
            _temp,
            new FakeProvider(ResourceProvider.Modrinth)).ConfigureAwait(true);

        Assert.Empty(result.Ready);
        Assert.Single(result.Failed);
    }

    /// <summary>A file that cannot be read fails on its own, without losing the batch.</summary>
    [Fact]
    public async Task AFileThatCannotBeHashedFailsAlone()
    {
        var provider = new FakeProvider(
            ResourceProvider.Modrinth,
            versions: new() { ["good"] = Version("p1") },
            projects: new() { ["p1"] = Pack("p1", "mod-a") });

        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("locked.jar", "Locked"), new ResourceToIdentify("fine.jar", "Fine")],
            _temp,
            provider,
            hashFile: path => path == "locked.jar" ? throw new IOException("in use") : "good").ConfigureAwait(true);

        Assert.Equal("Fine", Assert.Single(result.Ready).Name);
        Assert.Equal("Locked", Assert.Single(result.Failed).Name);
    }

    // ================================================================== the lookups

    /// <summary>One project owning several files costs one project request, not one per file.</summary>
    [Fact]
    public async Task ProjectIdsAreDeduplicatedBeforeAsking()
    {
        var provider = new FakeProvider(
            ResourceProvider.Modrinth,
            versions: new()
            {
                ["h1"] = Version("p1", "v1"),
                ["h2"] = Version("p1", "v2"),
                ["h3"] = Version("p2", "v3"),
            },
            projects: new() { ["p1"] = Pack("p1", "mod-a"), ["p2"] = Pack("p2", "mod-b") });

        var result = await EnsureMetadata.RunAsync(
            [
                new ResourceToIdentify("a.jar", "A"),
                new ResourceToIdentify("b.jar", "B"),
                new ResourceToIdentify("c.jar", "C"),
            ],
            _temp,
            provider,
            hashFile: path => path switch { "a.jar" => "h1", "b.jar" => "h2", _ => "h3" }).ConfigureAwait(true);

        Assert.Equal(3, result.Ready.Count);

        // Three files, two projects, one request.
        Assert.Equal(["p1", "p2"], Assert.Single(provider.ProjectLookups).Order(StringComparer.Ordinal));
    }

    /// <summary>Nothing to look up means no requests at all, not an empty one.</summary>
    [Fact]
    public async Task NothingToIdentifyMeansNoRequests()
    {
        var provider = new FakeProvider(ResourceProvider.Modrinth);

        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("f", "F", IsFolder: true)],
            _temp,
            provider).ConfigureAwait(true);

        Assert.Empty(provider.HashLookups);
        Assert.Empty(provider.ProjectLookups);
        Assert.Single(result.Ready);
    }

    /// <summary>A hash the provider does not recognise is an unidentified file, not an error.</summary>
    [Fact]
    public async Task AnUnrecognisedHashFails()
    {
        var provider = new FakeProvider(ResourceProvider.Modrinth);

        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("mystery.jar", "Mystery")],
            _temp,
            provider,
            hashFile: _ => "unknown").ConfigureAwait(true);

        Assert.Single(result.Failed);

        // The version lookup happened; the project lookup had nothing to ask about.
        Assert.Single(provider.HashLookups);
        Assert.Empty(provider.ProjectLookups);
    }

    /// <summary>A release whose project the API does not return leaves the file unidentified.</summary>
    [Fact]
    public async Task AVersionWhoseProjectIsMissingFails()
    {
        var provider = new FakeProvider(
            ResourceProvider.Modrinth,
            versions: new() { ["h1"] = Version("p1") });

        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("a.jar", "A")],
            _temp,
            provider,
            hashFile: _ => "h1").ConfigureAwait(true);

        Assert.Single(result.Failed);
    }

    /*
     * The same jar under two names hashes identically, and only one entry can own that hash. The
     * second is reported failed rather than silently vanishing -- upstream's QHash insert would
     * overwrite it, so the file would never be accounted for either way.
     */
    [Fact]
    public async Task TheSameFileUnderTwoNamesIdentifiesOnce()
    {
        var provider = new FakeProvider(
            ResourceProvider.Modrinth,
            versions: new() { ["same"] = Version("p1") },
            projects: new() { ["p1"] = Pack("p1", "mod-a") });

        var result = await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("a.jar", "A"), new ResourceToIdentify("a-copy.jar", "A copy")],
            _temp,
            provider,
            hashFile: _ => "same").ConfigureAwait(true);

        Assert.Single(result.Ready);
        Assert.Single(result.Failed);

        // Every file is accounted for exactly once.
        Assert.Equal(2, result.Ready.Count + result.Failed.Count);
    }

    // ================================================================== what lands on disk

    [Fact]
    public async Task IdentifyingAFileWritesMetadataThatReadsBack()
    {
        var provider = new FakeProvider(
            ResourceProvider.Modrinth,
            versions: new() { ["h1"] = Version("p1", "v1") },
            projects: new() { ["p1"] = Pack("p1", "fabric-api") });

        await EnsureMetadata.RunAsync(
            [new ResourceToIdentify("mods/fabric-api-0.83.0.jar", "Fabric API")],
            _temp,
            provider,
            hashFile: _ => "h1").ConfigureAwait(true);

        // Read back through the packwiz reader, not from the object that was written.
        var written = Packwiz.GetIndexForMod(_temp, "fabric-api");

        Assert.True(written.IsValid);
        Assert.Equal("fabric-api-0.83.0.jar", written.Filename);
        Assert.Equal("p1", written.ModId);
        Assert.Equal("v1", written.Version);
        Assert.Equal("url", written.Mode);
        Assert.Equal(["1.20.1"], written.McVersions);
        Assert.Equal(["fabric"], written.Loaders);
    }

    /*
     * A disabled mod is "foo.jar.disabled" on disk but has to be recorded as "foo.jar" -- otherwise
     * enabling it renames the file and the metadata stops matching it.
     */
    [Theory]
    [InlineData("mods/mod.jar.disabled", "mod.jar")]
    [InlineData("mods/mod.jar", "mod.jar")]
    [InlineData("mods/mod.disabled.jar", "mod.disabled.jar")]
    public void TheDisabledSuffixIsStrippedFromTheRecordedName(string path, string expected)
        => Assert.Equal(expected, EnsureMetadata.LocalFileName(path));

    /// <summary>The name on disk wins over the provider's, or nothing matches it back.</summary>
    [Fact]
    public void TheLocalFileNameIsRecordedNotTheProvidersOne()
    {
        var version = Version("p1");
        version.FileName = "what-the-api-calls-it.jar";

        var metadata = EnsureMetadata.CreateMetadata(
            Pack("p1", "mod-a"),
            version,
            new ResourceToIdentify("mods/renamed-by-user.jar", "A"));

        Assert.Equal("renamed-by-user.jar", metadata.Filename);
    }

    /*
     * CurseForge forbids third-party downloads for some projects, so packwiz records "ask CurseForge
     * at install time" rather than a URL. That is chosen by PROVIDER, not by whether a URL happens to
     * be present.
     */
    [Fact]
    public void CurseforgeEntriesUseMetadataModeAndNumericIds()
    {
        var version = Version("306612", "3814740");

        var metadata = EnsureMetadata.CreateMetadata(
            Pack("306612", "fabric-api", ResourceProvider.Flame),
            version,
            new ResourceToIdentify("mods/a.jar", "A"));

        Assert.Equal("metadata:curseforge", metadata.Mode);
        Assert.Equal(306612, metadata.ProjectId);
        Assert.Equal(3814740, metadata.FileId);

        // The string pair belongs to Modrinth and stays empty here.
        Assert.Equal(string.Empty, metadata.ModId);
        Assert.True(metadata.IsValid);
    }

    /// <summary>The file's own side wins; a file that says nothing inherits the project's.</summary>
    [Theory]
    [InlineData("client", "both", PackwizSide.ClientSide)]
    [InlineData("", "server", PackwizSide.ServerSide)]
    [InlineData("", "", PackwizSide.UniversalSide)]
    public void TheMoreSpecificSideWins(string versionSide, string packSide, PackwizSide expected)
    {
        var version = Version("p1");
        version.Side = versionSide;

        var pack = Pack("p1", "mod-a");
        pack.Side = packSide;

        var metadata = EnsureMetadata.CreateMetadata(pack, version, new ResourceToIdentify("a.jar", "A"));

        Assert.Equal(expected, metadata.Side);
    }

    /// <summary>Sorted, so an entry rewritten from a differently ordered response does not churn.</summary>
    [Fact]
    public void MinecraftVersionsAreRecordedInOrder()
    {
        var version = Version("p1");
        version.McVersion.Clear();
        version.McVersion.AddRange(["1.20.1", "1.19.4", "1.20"]);

        var metadata = EnsureMetadata.CreateMetadata(
            Pack("p1", "mod-a"),
            version,
            new ResourceToIdentify("a.jar", "A"));

        Assert.Equal(["1.19.4", "1.20", "1.20.1"], metadata.McVersions);
    }

    [Fact]
    public void EveryLoaderTheFileDeclaresIsRecorded()
    {
        var version = Version("p1");
        version.Loaders = ModLoaderTypes.Fabric | ModLoaderTypes.Quilt;

        var metadata = EnsureMetadata.CreateMetadata(
            Pack("p1", "mod-a"),
            version,
            new ResourceToIdentify("a.jar", "A"));

        Assert.Equal(["fabric", "quilt"], metadata.Loaders.Order(StringComparer.Ordinal));
    }

    /// <summary>Each provider is identified by the algorithm its API answers on.</summary>
    [Fact]
    public void EachProviderHashesWithItsOwnAlgorithm()
    {
        Assert.Equal(HashAlgorithm.Sha512, Hashing.AlgorithmFor(ResourceProvider.Modrinth));
        Assert.Equal(HashAlgorithm.Murmur2, Hashing.AlgorithmFor(ResourceProvider.Flame));
    }
}
