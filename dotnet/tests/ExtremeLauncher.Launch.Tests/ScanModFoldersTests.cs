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
 * Characterization tests for folder scanning. Upstream has no Qt test for ModFolderLoadTask or
 * ScanModFolders.
 *
 * The rule worth being certain about is the disabled/enabled pairing: getting it wrong lists the same
 * mod twice, and a user who then enables one copy while the other is still there gets a duplicate-mod
 * crash that is very hard to diagnose from the launcher.
 */

using System.IO.Compression;
using System.Text;
using ExtremeLauncher.Minecraft.Mods;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ScanModFoldersTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-scan-" + Guid.NewGuid().ToString("N"));

    public ScanModFoldersTests() => Directory.CreateDirectory(_temp);

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

    private string GameRoot => Path.Combine(_temp, ".minecraft");

    /// <summary>Writes a jar with Fabric metadata into one of the instance's mod folders.</summary>
    private string MakeMod(string folder, string fileName, string id = "", string version = "1.0")
    {
        var directory = Path.Combine(GameRoot, folder);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);

        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            if (id.Length != 0)
            {
                using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), Encoding.UTF8);
                writer.Write($$"""{ "schemaVersion": 1, "id": "{{id}}", "version": "{{version}}", "name": "{{id}}" }""");
            }
            else
            {
                using var writer = new StreamWriter(zip.CreateEntry("com/example/Thing.class").Open());
                writer.Write("not metadata");
            }
        }

        return path;
    }

    // ================================================================== the pairing rule

    [Fact]
    public void ADisabledFileSupersedesItsEnabledTwin()
    {
        MakeMod("mods", "coolmod.jar", "coolmod");
        MakeMod("mods", "coolmod.jar.disabled", "coolmod");

        var found = ResourceFolder.LoadMods(Path.Combine(GameRoot, "mods"));

        // They are the same mod. Listing both would let a user enable one copy while the other is
        // still there, which the game sees as a duplicate and refuses to start on.
        var entry = Assert.Single(found);

        Assert.Equal("coolmod.jar.disabled", entry.Key);
        Assert.False(entry.Value.Resource.Enabled);
    }

    [Fact]
    public void TheOrderTheFolderListsThemDoesNotMatter()
    {
        // The pairing is resolved after everything has been seen, so a filesystem that lists
        // ".disabled" first behaves the same as one that does not.
        MakeMod("mods", "a.jar.disabled", "a");
        MakeMod("mods", "a.jar", "a");
        MakeMod("mods", "b.jar", "b");
        MakeMod("mods", "b.jar.disabled", "b");

        var found = ResourceFolder.LoadMods(Path.Combine(GameRoot, "mods"));

        Assert.Equal(2, found.Count);
        Assert.All(found.Values, e => Assert.False(e.Resource.Enabled));
    }

    [Fact]
    public void AnUnpairedDisabledModIsStillListed()
    {
        MakeMod("mods", "lonely.jar.disabled", "lonely");

        var entry = Assert.Single(ResourceFolder.LoadMods(Path.Combine(GameRoot, "mods")));

        Assert.Equal("lonely.jar.disabled", entry.Key);
        Assert.False(entry.Value.Resource.Enabled);
    }

    [Fact]
    public void EnabledModsAreListedNormally()
    {
        MakeMod("mods", "one.jar", "one");
        MakeMod("mods", "two.jar", "two");

        var found = ResourceFolder.LoadMods(Path.Combine(GameRoot, "mods"));

        Assert.Equal(2, found.Count);
        Assert.All(found.Values, e => Assert.True(e.Resource.Enabled));
    }

    // ================================================================== parsing while scanning

    [Fact]
    public void EachModsMetadataIsParsedDuringTheScan()
    {
        MakeMod("mods", "coolmod.jar", "coolmod", "2.5.1");

        var entry = Assert.Single(ResourceFolder.LoadMods(Path.Combine(GameRoot, "mods")));
        var mod = Assert.IsType<Mod>(entry.Value.Resource);

        Assert.Equal("coolmod", mod.Details.ModId);
        Assert.Equal("2.5.1", mod.Details.Version);
    }

    [Fact]
    public void AJarWithNoMetadataIsStillListed()
    {
        MakeMod("mods", "mystery.jar");

        // It is in the folder, so the game will try to load it -- and a user looking for why their
        // game crashed needs to see it.
        var entry = Assert.Single(ResourceFolder.LoadMods(Path.Combine(GameRoot, "mods")));

        Assert.Equal("mystery.jar", entry.Key);
        Assert.False(entry.Value.Resource.Valid);
    }

    [Fact]
    public void EverythingStartsWithoutMetadata()
    {
        MakeMod("mods", "coolmod.jar", "coolmod");

        // The metadata index is not ported; a hand-installed mod is in this state anyway.
        Assert.Equal(
            ResourceStatus.NoMetadata,
            Assert.Single(ResourceFolder.LoadMods(Path.Combine(GameRoot, "mods"))).Value.Status);
    }

    [Fact]
    public void AMissingFolderIsEmptyRatherThanAnError()
        => Assert.Empty(ResourceFolder.LoadMods(Path.Combine(GameRoot, "nope")));

    // ================================================================== the three folders

    [Fact]
    public void AllThreeModFoldersAreScanned()
    {
        MakeMod("mods", "modern.jar", "modern");
        MakeMod("coremods", "ancient.jar", "ancient");
        MakeMod("nilmods", "nil.jar", "nil");

        var found = ResourceFolder.LoadAllMods(GameRoot);

        // "coremods" is a pre-1.6 Forge arrangement and "nilmods" is NilLoader's; both are empty for
        // almost every instance, but skipping them would silently hide the mods of the people who
        // still use them.
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void TheModsFolderWinsANameCollision()
    {
        MakeMod("mods", "same.jar", "from-mods");
        MakeMod("coremods", "same.jar", "from-coremods");

        var found = ResourceFolder.LoadAllMods(GameRoot);
        var mod = Assert.IsType<Mod>(Assert.Single(found).Value.Resource);

        Assert.Equal("from-mods", mod.Details.ModId);
    }

    // ================================================================== the launch step

    [Fact]
    public async Task TheStepLogsWhatItFound()
    {
        MakeMod("mods", "coolmod.jar", "coolmod", "2.5.1");
        MakeMod("mods", "offmod.jar.disabled", "offmod");

        var step = new ScanModFolders(GameRoot);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());

        // When a user reports a crash, the first question is which mods were installed -- this is what
        // answers it.
        Assert.Contains(lines, l => l.Contains("Mods (2)", StringComparison.Ordinal));

        Assert.Contains(
            lines,
            l => l.Contains("coolmod.jar", StringComparison.Ordinal) && l.Contains("2.5.1", StringComparison.Ordinal));

        Assert.Contains(
            lines,
            l => l.Contains("offmod.jar.disabled", StringComparison.Ordinal)
                 && l.Contains("[disabled]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInstanceWithNoModsSaysNothing()
    {
        Directory.CreateDirectory(GameRoot);

        var step = new ScanModFolders(GameRoot);

        var lines = new List<string>();
        step.LogLines += (_, e) => lines.AddRange(e.Lines);

        Assert.True(await step.RunAsync());
        Assert.Empty(lines);
        Assert.Empty(step.Mods);
    }

    [Fact]
    public async Task TheStepNeverFailsALaunch()
    {
        // A folder that cannot be read is worth a warning and nothing more; refusing to start the game
        // because a mod list could not be enumerated would be the worse outcome.
        var pipeline = new LaunchPipeline().AddStep(new ScanModFolders(Path.Combine(_temp, "no-such-instance")));

        Assert.True(await pipeline.RunAsync());
    }
}
