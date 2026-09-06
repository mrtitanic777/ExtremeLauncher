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
 * An instance remembering that it came from a modpack.
 *
 * THE SETTINGS EXISTED AND NOTHING WROTE THEM. All six ManagedPack* keys were registered several
 * waves ago and no code path ever set one, so an imported pack forgot it had ever been a pack the
 * moment the import finished. These tests are mostly about the write actually happening, and about
 * the difference between knowing where an instance came from and being able to update it.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ManagedPackTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-managed-" + Guid.NewGuid().ToString("N"));

    public ManagedPackTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InstanceSettings NewInstance()
    {
        var globals = GlobalSettings.Create(Path.Combine(_temp, "extremelauncher.cfg"));
        var own = new IniSettingsObject(Path.Combine(_temp, $"instance-{Guid.NewGuid():N}.cfg"));

        return new InstanceSettings(own, globals);
    }

    /// <summary>A minimal but real .mrpack.</summary>
    private string WritePack(string name, string versionId)
    {
        var path = Path.Combine(_temp, $"pack-{Guid.NewGuid():N}.mrpack");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var entry = zip.CreateEntry("modrinth.index.json");

        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);

        writer.Write($$"""
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "{{versionId}}",
          "name": "{{name}}",
          "files": [],
          "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.7" }
        }
        """);

        return path;
    }

    [Fact]
    public async Task AnImportedPackRemembersWhatItWas()
    {
        /*
         * THE GAP THIS WAVE CLOSED. Without this the instance is "some mods and Fabric" and nothing
         * anywhere says it is Fabulously Optimized 5.9.2 -- which is what the person actually
         * installed and the only name they know it by.
         */
        var staging = Path.Combine(_temp, "staging");

        var task = new ModrinthImportTask(WritePack("Fabulously Optimized", "5.9.2")) { StagingPath = staging };

        Assert.True(await task.RunAsync(), task.FailReason);

        var settings = new IniSettingsObject(Path.Combine(staging, "instance.cfg"));

        settings.RegisterSetting("ManagedPack", false);
        settings.RegisterSetting("ManagedPackType", string.Empty);
        settings.RegisterSetting("ManagedPackName", string.Empty);
        settings.RegisterSetting("ManagedPackVersionName", string.Empty);

        Assert.True(settings.GetBool("ManagedPack"));
        Assert.Equal("modrinth", settings.GetString("ManagedPackType"));
        Assert.Equal("Fabulously Optimized", settings.GetString("ManagedPackName"));
        Assert.Equal("5.9.2", settings.GetString("ManagedPackVersionName"));
    }

    [Fact]
    public async Task ThePackKeepsItsOwnNameWhenTheInstanceIsRenamed()
    {
        /*
         * Two different names, and the pack's is the one worth recording: somebody who called their
         * instance "modded 1.20" still wants to know what is underneath it.
         */
        var staging = Path.Combine(_temp, "staging-renamed");

        var task = new ModrinthImportTask(WritePack("Fabulously Optimized", "5.9.2"), name: "modded 1.20")
        {
            StagingPath = staging,
        };

        Assert.True(await task.RunAsync(), task.FailReason);

        var settings = new IniSettingsObject(Path.Combine(staging, "instance.cfg"));

        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("ManagedPackName", string.Empty);

        Assert.Equal("modded 1.20", settings.GetString("name"));
        Assert.Equal("Fabulously Optimized", settings.GetString("ManagedPackName"));
    }

    [Fact]
    public async Task AFileImportHasNothingToLookThePackUpBy()
    {
        /*
         * NOT AN OVERSIGHT. A .mrpack on disk does not carry its own Modrinth project id -- only the
         * launcher's own pack browser knows that, and this port has no pack browser. So an imported
         * instance has a name and a version and no way to ask for a newer one.
         */
        var staging = Path.Combine(_temp, "staging-noid");

        var task = new ModrinthImportTask(WritePack("Some Pack", "1.0.0")) { StagingPath = staging };

        Assert.True(await task.RunAsync(), task.FailReason);

        var settings = new IniSettingsObject(Path.Combine(staging, "instance.cfg"));

        settings.RegisterSetting("ManagedPackID", string.Empty);
        settings.RegisterSetting("ManagedPackVersionID", string.Empty);

        Assert.Equal(string.Empty, settings.GetString("ManagedPackID"));
        Assert.Equal(string.Empty, settings.GetString("ManagedPackVersionID"));
    }

    [Fact]
    public async Task AnInstallLeavesTheLedgerAnUpdateWouldNeed()
    {
        /*
         * THE RECORD OF WHAT THE PACK PUT THERE. Nothing about a file on disk says whether the pack
         * supplied it or the player added it, so without this an update can only choose between
         * leaving stale mods behind and deleting things that were never the pack's.
         *
         * Upstream's layout exactly -- <instance>/mrpack/ -- so an instance made here can be updated
         * by Prism and the other way round.
         */
        var staging = Path.Combine(_temp, "staging-ledger");

        var task = new ModrinthImportTask(WritePack("A Pack", "1.0.0")) { StagingPath = staging };

        Assert.True(await task.RunAsync(), task.FailReason);

        var ledgerFolder = Path.Combine(staging, "mrpack");

        Assert.True(File.Exists(Path.Combine(ledgerFolder, "modrinth.index.json")));

        // Written even though this pack has no overrides folder: an EMPTY list and a MISSING list
        // mean different things -- "the pack contributed nothing" against "nobody wrote this down".
        Assert.True(File.Exists(Path.Combine(ledgerFolder, "overrides.txt")));
        Assert.True(File.Exists(Path.Combine(ledgerFolder, "client-overrides.txt")));
    }

    [Fact]
    public async Task TheKeptManifestIsWhatTheAuthorPublished()
    {
        /*
         * COPIED, NOT RE-SERIALISED. Writing it back through this port's own model would quietly drop
         * any field the model does not know about -- and the entire value of the ledger is being able
         * to compare against what was actually published.
         */
        var staging = Path.Combine(_temp, "staging-verbatim");
        var packPath = WritePack("A Pack", "1.0.0");

        var task = new ModrinthImportTask(packPath) { StagingPath = staging };

        Assert.True(await task.RunAsync(), task.FailReason);

        using var zip = ZipFile.OpenRead(packPath);
        using var original = zip.GetEntry("modrinth.index.json")!.Open();
        using var memory = new MemoryStream();

        original.CopyTo(memory);

        Assert.Equal(
            memory.ToArray(),
            File.ReadAllBytes(Path.Combine(staging, "mrpack", "modrinth.index.json")));
    }

    [Fact]
    public async Task TheLedgerReadsBackAsAPlanCanUseIt()
    {
        var staging = Path.Combine(_temp, "staging-readback");

        var task = new ModrinthImportTask(WritePack("A Pack", "1.0.0")) { StagingPath = staging };

        Assert.True(await task.RunAsync(), task.FailReason);

        var ledger = PackLedgerStore.Read(staging);

        Assert.True(ledger.Exists);
        Assert.Equal("A Pack", ledger.Manifest?.Name);
        Assert.Empty(ledger.Overrides);
    }

    [Fact]
    public void AnInstanceWithNoLedgerReadsAsEmptyRatherThanThrowing()
    {
        // Every pack installed by this launcher before the ledger existed.
        var ledger = PackLedgerStore.Read(_temp);

        Assert.False(ledger.Exists);
        Assert.Empty(ledger.Overrides);
    }

    [Fact]
    public void ALedgerThatWillNotParseIsTreatedAsAbsent()
    {
        /*
         * The safe reading: PackUpdatePlanner refuses to delete anything without a ledger, so a
         * corrupt one has to mean "no ledger" rather than "an empty pack" -- which would plan the
         * removal of every file the instance has.
         */
        var folder = Path.Combine(_temp, "corrupt");

        Directory.CreateDirectory(Path.Combine(folder, "mrpack"));
        File.WriteAllText(Path.Combine(folder, "mrpack", "modrinth.index.json"), "{ this is not json");

        Assert.False(PackLedgerStore.Read(folder).Exists);
    }

    [Fact]
    public void KnowingWhereItCameFromAndBeingAbleToUpdateAreDifferentQuestions()
    {
        /*
         * A DIVERGENCE FROM UPSTREAM, which has one flag for both. Its ModrinthInstanceCreationTask
         * carries the comment "Don't add managed info to packs without an ID (most likely imported
         * from ZIP)" and then its else branch calls setManagedPack anyway with empty ids -- so
         * ManagedPack is true, its managed-pack page appears, and the page has nothing to look up.
         */
        var settings = NewInstance();

        settings.SetManagedPack("modrinth", string.Empty, "Some Pack", string.Empty, "1.0.0");

        Assert.True(settings.IsManagedPack);
        Assert.False(settings.CanCheckForPackUpdates);

        settings.SetManagedPack("modrinth", "abc123", "Some Pack", "xyz789", "1.0.0");

        Assert.True(settings.CanCheckForPackUpdates);
    }

    [Fact]
    public void AnInstanceNobodyImportedSaysNothing()
    {
        // Most instances. A permanent "imported from nowhere" row would be worse than no row.
        var settings = NewInstance();

        Assert.False(settings.IsManagedPack);
        Assert.False(settings.CanCheckForPackUpdates);
        Assert.Equal(string.Empty, settings.ManagedPackName);
    }

    [Fact]
    public void TheFieldsRoundTripThroughInstanceCfg()
    {
        // They are a compatibility surface: upstream reads the same six keys out of the same file.
        var path = Path.Combine(_temp, "roundtrip.cfg");
        var globals = GlobalSettings.Create(Path.Combine(_temp, "extremelauncher.cfg"));

        new InstanceSettings(new IniSettingsObject(path), globals)
            .SetManagedPack("modrinth", "abc123", "Some Pack", "xyz789", "1.0.0");

        var reopened = new InstanceSettings(new IniSettingsObject(path), globals);

        Assert.Equal("modrinth", reopened.ManagedPackType);
        Assert.Equal("abc123", reopened.ManagedPackId);
        Assert.Equal("Some Pack", reopened.ManagedPackName);
        Assert.Equal("xyz789", reopened.ManagedPackVersionId);
        Assert.Equal("1.0.0", reopened.ManagedPackVersionName);

        var raw = File.ReadAllText(path);

        Assert.Contains("ManagedPackName=Some Pack", raw, StringComparison.Ordinal);
    }
}
