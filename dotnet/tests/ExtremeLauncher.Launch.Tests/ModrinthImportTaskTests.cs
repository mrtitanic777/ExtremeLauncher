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
 * IMPORTING A PACK IS THE LAUNCHER READING A FILE FROM THE INTERNET, so the tests that matter most are
 * the ones about a pack that is lying: a path that escapes the instance, a required file with nowhere
 * to fetch it from, a manifest that names no Minecraft version.
 *
 * The packs are built here as real zips and read back through InstanceList, because a .mrpack is a
 * format somebody else writes and an instance is a format somebody else reads.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ModrinthImportTaskTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-import-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public ModrinthImportTaskTests()
    {
        _instances = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(_instances);
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

    private static RuntimeContext Context() => new()
    {
        System = "windows",
        JavaArchitecture = "64",
        JavaRealArchitecture = "x86_64",
    };

    private InstanceList NewList()
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        return list;
    }

    /// <summary>Builds a .mrpack: a zip with a manifest and optionally some override files.</summary>
    private string MakePack(
        string manifestJson,
        IReadOnlyDictionary<string, string>? overrides = null,
        string root = "")
    {
        var path = Path.Combine(_temp, "pack.mrpack");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(archive, root + "modrinth.index.json", manifestJson);

            foreach (var (name, content) in overrides ?? new Dictionary<string, string>())
            {
                Write(archive, root + name, content);
            }
        }

        return path;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        writer.Write(content);
    }

    private static string Manifest(string name = "Cool Pack", string files = "[]", string deps = "")
        => $$"""
            {
                "formatVersion": 1,
                "game": "minecraft",
                "versionId": "1.0.0",
                "name": "{{name}}",
                "files": {{files}},
                "dependencies": { "minecraft": "1.20.1"{{deps}} }
            }
            """;

    private async Task<(bool Succeeded, string Id, InstanceList List, ModrinthImportTask Task)> ImportAsync(
        string packPath,
        string name = "",
        string group = "")
    {
        var list = NewList();
        var import = new ModrinthImportTask(packPath, client: null, name, group);
        var staging = new InstanceStagingTask(list, import, import);

        var succeeded = await staging.RunAsync().ConfigureAwait(true);

        list.LoadList();

        return (succeeded, staging.CommittedId, list, import);
    }

    // ================================================================== the happy path

    /*
     * Read back through InstanceList -- the same code that reads an instance Prism made -- because an
     * imported pack has to be an ordinary instance afterwards, not a special kind of one.
     */
    [Fact]
    public async Task AnImportedPackIsAnOrdinaryInstance()
    {
        var (succeeded, id, list, task) = await ImportAsync(MakePack(Manifest())).ConfigureAwait(true);

        Assert.True(succeeded);
        Assert.Equal("Cool Pack", task.PackName);

        var instance = list.GetInstanceById(id);

        Assert.NotNull(instance);
        Assert.Equal("Cool Pack", instance!.Name);
        Assert.True(instance.IsSupported);
    }

    [Fact]
    public async Task TheComponentsComeFromTheManifestsDependencies()
    {
        var pack = MakePack(Manifest(deps: """, "fabric-loader": "0.15.7" """));

        var (_, id, list, _) = await ImportAsync(pack).ConfigureAwait(true);

        var profile = new PackProfile(Context());

        Assert.True(profile.Load(list.GetInstanceById(id)!.Paths.PackProfilePath));

        Assert.Equal("1.20.1", profile.GetComponentVersion("net.minecraft"));
        Assert.Equal("0.15.7", profile.GetComponentVersion("net.fabricmc.fabric-loader"));

        // Minecraft important, the loader not -- as for any other instance.
        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
        Assert.False(profile.GetComponent("net.fabricmc.fabric-loader")!.IsImportant);
    }

    /// <summary>A given name wins over the pack's own, which is what the dialog will pass.</summary>
    [Fact]
    public async Task AGivenNameOverridesThePacksOwn()
    {
        var (_, id, list, _) = await ImportAsync(MakePack(Manifest()), name: "My Copy").ConfigureAwait(true);

        Assert.Equal("My Copy", list.GetInstanceById(id)!.Name);
    }

    // ================================================================== overrides

    /*
     * "overrides" then "client-overrides", in that order, because the client ones are meant to win --
     * which is what lets a pack ship a server config and a different client one under the same name.
     */
    [Fact]
    public async Task ClientOverridesWinOverPlainOnes()
    {
        var pack = MakePack(
            Manifest(),
            new Dictionary<string, string>
            {
                ["overrides/config/thing.txt"] = "shared",
                ["overrides/options.txt"] = "from overrides",
                ["client-overrides/options.txt"] = "from client-overrides",
            });

        var (_, id, list, _) = await ImportAsync(pack).ConfigureAwait(true);

        var gameRoot = list.GetInstanceById(id)!.Paths.GameRoot;

        Assert.Equal("shared", File.ReadAllText(Path.Combine(gameRoot, "config", "thing.txt")));
        Assert.Equal("from client-overrides", File.ReadAllText(Path.Combine(gameRoot, "options.txt")));
    }

    /*
     * A WRAPPED .mrpack IS NOT RECOGNISED, and that is upstream's behaviour rather than an oversight
     * here. PackTypeDetector treats the Modrinth marker as ROOT-ONLY -- only instance.cfg and
     * manifest.json are searched for at depth -- so re-zipping a pack from a file manager, which adds a
     * wrapping folder, produces something neither launcher will take.
     *
     * PORTING.md recorded this in wave 8, and I wrote a test asserting the opposite here without
     * reading my own note. Pinned as it actually behaves, with the divergence named rather than
     * quietly fixed.
     */
    [Fact]
    public async Task AWrappedArchiveIsNotRecognised()
    {
        var pack = MakePack(
            Manifest(),
            new Dictionary<string, string> { ["overrides/options.txt"] = "wrapped" },
            root: "Cool Pack 1.0.0/");

        var (succeeded, _, _, task) = await ImportAsync(pack).ConfigureAwait(true);

        Assert.False(succeeded);
        Assert.Contains("not a modpack", task.FailReason, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== packs that lie

    /*
     * UPSTREAM BUG #12 WAS A PATH-TRAVERSAL HOLE in exactly this format: `path` read straight out of
     * the JSON and joined to the game directory. A pack claiming "../../../evil.txt" must be refused,
     * and nothing must be written outside the instance.
     */
    [Fact]
    public async Task APackThatTriesToEscapeTheInstanceIsRefused()
    {
        var files = """
            [{
                "path": "../../../evil.txt",
                "hashes": { "sha512": "00" },
                "downloads": ["https://example.com/evil.txt"],
                "fileSize": 1
            }]
            """;

        var (succeeded, _, _, _) = await ImportAsync(MakePack(Manifest(files: files))).ConfigureAwait(true);

        Assert.False(succeeded);

        /*
         * NO INSTANCE WAS COMMITTED, and nothing was written above the instances folder. The staging
         * root itself (instances/.tmp) is expected to exist -- an earlier version of this asserted the
         * folder was empty and failed on that, which would have read as the guard not working.
         */
        Assert.Empty(NewList().Instances);
        Assert.False(File.Exists(Path.Combine(_temp, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_instances, "evil.txt")));
    }

    [Fact]
    public async Task AManifestWithNoMinecraftVersionIsRefused()
    {
        var manifest = """
            {
                "formatVersion": 1,
                "game": "minecraft",
                "versionId": "1.0.0",
                "name": "Broken Pack",
                "files": [],
                "dependencies": { }
            }
            """;

        var (succeeded, _, _, _) = await ImportAsync(MakePack(manifest)).ConfigureAwait(true);

        Assert.False(succeeded);
        Assert.Empty(NewList().Instances);
    }

    /// <summary>Something that is not a pack at all is named, not merely refused.</summary>
    [Fact]
    public async Task SomethingThatIsNotAPackSaysWhat()
    {
        var path = Path.Combine(_temp, "notapack.zip");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(archive, "readme.txt", "hello");
        }

        var list = NewList();
        var import = new ModrinthImportTask(path) { StagingPath = Path.Combine(_temp, "staging") };

        Assert.False(await import.RunAsync().ConfigureAwait(true));
        Assert.Contains("not a modpack", import.FailReason, StringComparison.OrdinalIgnoreCase);
    }

    /*
     * A CurseForge pack is a real thing somebody will try, and "unsupported" answers a different
     * question than the one they have.
     */
    [Fact]
    public async Task ACurseForgePackIsNamedRatherThanCalledUnsupported()
    {
        var path = Path.Combine(_temp, "flame.zip");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(archive, "manifest.json", """{ "manifestType": "minecraftModpack", "name": "Flame Pack" }""");
        }

        var import = new ModrinthImportTask(path) { StagingPath = Path.Combine(_temp, "staging") };

        Assert.False(await import.RunAsync().ConfigureAwait(true));
        Assert.Contains("Flame", import.FailReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissingFileIsRefused()
    {
        var import = new ModrinthImportTask(Path.Combine(_temp, "nope.mrpack"))
        {
            StagingPath = Path.Combine(_temp, "staging"),
        };

        Assert.False(await import.RunAsync().ConfigureAwait(true));
        Assert.Contains("no file", import.FailReason, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== without a network

    /*
     * IMPORTING WITHOUT A CLIENT WRITES THE INSTANCE AND SKIPS THE MODS. Worth having as a distinct
     * state rather than a failure: the components and the overrides are the part that cannot be
     * recovered later, and a mods folder can be filled by launching once the network is back.
     */
    [Fact]
    public async Task WithoutAClientTheInstanceIsStillWrittenAndTheFilesAreNot()
    {
        var files = """
            [{
                "path": "mods/sodium.jar",
                "hashes": { "sha512": "00" },
                "downloads": ["https://example.com/sodium.jar"],
                "fileSize": 1
            }]
            """;

        var (succeeded, id, list, task) = await ImportAsync(MakePack(Manifest(files: files))).ConfigureAwait(true);

        Assert.True(succeeded);
        Assert.Equal(1, task.FileCount);
        Assert.Equal(0, task.DownloadedCount);

        var gameRoot = list.GetInstanceById(id)!.Paths.GameRoot;

        Assert.False(File.Exists(Path.Combine(gameRoot, "mods", "sodium.jar")));
    }
}
