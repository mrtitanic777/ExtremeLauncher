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
 * Exporting an instance as a .mrpack.
 *
 * ASSERTED ON THE ARCHIVE AND ITS MANIFEST, read back with a plain ZipArchive and a JSON parser. A
 * .mrpack is a format other launchers consume; reading it back through this port's own importer would
 * only prove the two agree with each other.
 */

using System.IO.Compression;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class PackExportTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "el-packexp-" + Guid.NewGuid().ToString("N"));

    private readonly InstancePaths _paths;

    public PackExportTaskTests()
    {
        var instance = Path.Combine(_root, "instance");

        Directory.CreateDirectory(instance);

        _paths = new InstancePaths(instance);

        Directory.CreateDirectory(_paths.GameRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Target => Path.Combine(_root, "exported.mrpack");

    private void WritePackProfile(string minecraft = "1.20.1", string? loaderUid = "net.fabricmc.fabric-loader")
    {
        var components = new List<string>
        {
            $$"""{"important":true,"uid":"net.minecraft","version":"{{minecraft}}"}""",
        };

        if (loaderUid is not null)
        {
            components.Add($$"""{"uid":"{{loaderUid}}","version":"0.15.7"}""");
        }

        File.WriteAllText(
            _paths.PackProfilePath,
            $$"""{"formatVersion":1,"components":[{{string.Join(",", components)}}]}""");
    }

    private void WriteGameFile(string relativePath, string contents = "x")
    {
        var path = Path.Combine(_paths.GameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    /// <summary>A downloaded mod: the jar plus the packwiz entry wave 17 writes beside it.</summary>
    private void WriteDownloadedMod(string slug, string fileName, string url)
    {
        WriteGameFile("mods/" + fileName, "pretend jar bytes for " + slug);

        var index = Path.Combine(_paths.GameRoot, ".index");

        Directory.CreateDirectory(index);

        File.WriteAllText(
            Path.Combine(index, slug + ".pw.toml"),
            $"""
             name = "{slug}"
             filename = "{fileName}"
             side = "both"

             [download]
             mode = "url"
             url = "{url}"
             hash-format = "sha512"
             hash = "aaaa"

             [update]
             [update.modrinth]
             mod-id = "abc"
             version = "def"
             """);
    }

    private static JsonObject ManifestIn(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        using var stream = zip.GetEntry("modrinth.index.json")!.Open();
        using var reader = new StreamReader(stream);

        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    private static string[] EntriesIn(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);

        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task ThePackHasAManifestNamingTheGameAndTheLoader()
    {
        WritePackProfile();

        var task = new PackExportTask(_paths, Target, "My Pack", "2.0", "A summary.");

        Assert.True(await task.RunAsync(CancellationToken.None));

        var manifest = ManifestIn(Target);

        Assert.Equal("My Pack", (string?)manifest["name"]);
        Assert.Equal("2.0", (string?)manifest["versionId"]);
        Assert.Equal("A summary.", (string?)manifest["summary"]);

        var dependencies = manifest["dependencies"]!.AsObject();

        Assert.Equal("1.20.1", (string?)dependencies["minecraft"]);
        Assert.Equal("0.15.7", (string?)dependencies["fabric-loader"]);
    }

    [Fact]
    public async Task ADownloadedModBecomesALinkRatherThanACopy()
    {
        /*
         * THE WHOLE POINT. A mod with packwiz metadata is a URL and a hash in the manifest -- a few
         * hundred bytes -- instead of a megabyte of jar inside the zip. That split is the difference
         * between a 200 KB pack and a 400 MB one.
         */
        WritePackProfile();
        WriteDownloadedMod("sodium", "sodium-fabric-0.5.13.jar", "https://cdn.modrinth.com/x/sodium.jar");

        var task = new PackExportTask(_paths, Target, "My Pack");

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(1, task.LinkedCount);

        var files = ManifestIn(Target)["files"]!.AsArray();

        Assert.Single(files);
        Assert.Equal("mods/sodium-fabric-0.5.13.jar", (string?)files[0]!["path"]);
        Assert.Equal(
            "https://cdn.modrinth.com/x/sodium.jar",
            (string?)files[0]!["downloads"]!.AsArray()[0]);

        // And it is NOT also copied in, or every importer would write the file twice.
        Assert.DoesNotContain(
            EntriesIn(Target),
            e => e.StartsWith("overrides/mods/sodium", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AModWithNoMetadataIsCopiedIntoOverrides()
    {
        // A jar dropped in by hand has no source to link to, so it has to travel bodily.
        WritePackProfile();
        WriteGameFile("mods/handmade.jar", "bytes");

        var task = new PackExportTask(_paths, Target, "My Pack");

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(0, task.LinkedCount);
        Assert.Contains("overrides/mods/handmade.jar", EntriesIn(Target));
    }

    [Fact]
    public async Task AnOrphanedIndexEntryIsNotLinked()
    {
        /*
         * The .pw.toml outlived its jar -- the user deleted the file from the folder. Linking it would
         * hand somebody a pack that fails to install on a mod they never chose.
         */
        WritePackProfile();
        WriteDownloadedMod("sodium", "sodium.jar", "https://example.invalid/sodium.jar");

        File.Delete(Path.Combine(_paths.GameRoot, "mods", "sodium.jar"));

        var task = new PackExportTask(_paths, Target, "My Pack");

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(0, task.LinkedCount);
        Assert.Empty(ManifestIn(Target)["files"]!.AsArray());
    }

    [Fact]
    public async Task ConfigsTravelInOverrides()
    {
        // The reason a pack is worth anything: somebody else gets the configuration too.
        WritePackProfile();
        WriteGameFile("config/sodium-options.json", "{}");

        await new PackExportTask(_paths, Target, "My Pack").RunAsync(CancellationToken.None);

        Assert.Contains("overrides/config/sodium-options.json", EntriesIn(Target));
    }

    [Fact]
    public async Task SomebodyElsesWorldsAndScreenshotsDoNotTravel()
    {
        /*
         * Narrower than the instance export on purpose: a pack is for other people. An instance export
         * is a backup and keeps these.
         */
        WritePackProfile();
        WriteGameFile("saves/MyWorld/level.dat");
        WriteGameFile("screenshots/shot.png");
        WriteGameFile("logs/latest.log");
        WriteGameFile("options.txt", "fov:90");

        await new PackExportTask(_paths, Target, "My Pack").RunAsync(CancellationToken.None);

        var entries = EntriesIn(Target);

        Assert.DoesNotContain(entries, e => e.Contains("saves/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Contains("screenshots/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Contains("logs/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Contains("options.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLauncherOwnMetadataDoesNotTravel()
    {
        // .index means something only to this launcher, and it would tell another one to re-download
        // the very mods the manifest already links.
        WritePackProfile();
        WriteDownloadedMod("sodium", "sodium.jar", "https://example.invalid/sodium.jar");

        await new PackExportTask(_paths, Target, "My Pack").RunAsync(CancellationToken.None);

        // The FOLDER, not the substring: "modrinth.index.json" contains ".index" and is the one file
        // that must be there. My first version of this assertion caught the manifest instead.
        Assert.DoesNotContain(
            EntriesIn(Target),
            e => e.StartsWith("overrides/.index/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInstanceWithNoMinecraftVersionIsRefused()
    {
        File.WriteAllText(_paths.PackProfilePath, """{"formatVersion":1,"components":[]}""");

        var task = new PackExportTask(_paths, Target, "My Pack");

        Assert.False(await task.RunAsync(CancellationToken.None));
        Assert.Contains("Minecraft", task.FailReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task APackWithNoNameIsRefused()
    {
        WritePackProfile();

        var task = new PackExportTask(_paths, Target, string.Empty);

        Assert.False(await task.RunAsync(CancellationToken.None));
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task NoPartFileIsLeftBehind()
    {
        WritePackProfile();
        WriteGameFile("config/a.json");

        await new PackExportTask(_paths, Target, "My Pack").RunAsync(CancellationToken.None);

        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public async Task TheManifestHasNoByteOrderMark()
    {
        /*
         * FOUND BY IMPORTING THE PACK FOR REAL, not by any test here. Encoding.UTF8 emits a BOM, and
         * a manifest starting EF BB BF is rejected outright: "'0xEF' is an invalid start of a value".
         *
         * Every other test in this file passed with the BOM present, because they read the entry back
         * through a StreamReader -- which strips it while decoding. This one reads the BYTES.
         */
        WritePackProfile();

        await new PackExportTask(_paths, Target, "My Pack").RunAsync(CancellationToken.None);

        using var zip = ZipFile.OpenRead(Target);
        using var stream = zip.GetEntry("modrinth.index.json")!.Open();

        var head = new byte[3];

        Assert.Equal(3, stream.Read(head, 0, 3));

        // '{' is what a JSON object starts with; EF BB BF is what a BOM starts with.
        Assert.Equal((byte)'{', head[0]);
    }

    [Fact]
    public async Task TheExportedPackIsSomethingThisLauncherCanImportAgain()
    {
        /*
         * The round trip, which is the only test that says the format is right rather than merely
         * plausible: PackTypeDetector is what the import path uses to decide what a file is, and it
         * has never seen anything this port produced.
         */
        WritePackProfile();
        WriteGameFile("config/a.json");

        await new PackExportTask(_paths, Target, "My Pack").RunAsync(CancellationToken.None);

        var detected = ModPlatform.PackTypeDetector.Detect(Target);

        Assert.Equal(ModPlatform.ModpackType.Modrinth, detected.Type);
    }

    // ================================================================== CurseForge export

    private string FlameTarget => Path.Combine(_root, "exported.zip");

    /// <summary>A mod downloaded from CurseForge: its jar plus a packwiz entry naming the ids.</summary>
    private void WriteCurseForgeMod(string slug, string fileName, int projectId, int fileId)
    {
        WriteGameFile("mods/" + fileName, "pretend jar bytes for " + slug);

        var index = Path.Combine(_paths.GameRoot, ".index");

        Directory.CreateDirectory(index);

        File.WriteAllText(
            Path.Combine(index, slug + ".pw.toml"),
            $"""
             name = "{slug}"
             filename = "{fileName}"
             side = "both"
             [download]
             mode = "metadata:curseforge"
             hash-format = "sha1"
             hash = "bbbb"
             [update]
             [update.curseforge]
             file-id = {fileId}
             project-id = {projectId}
             """);
    }

    private static JsonObject FlameManifestIn(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        using var reader = new StreamReader(stream);

        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    private PackExportTask FlameExport(string name = "My Pack")
        => new(_paths, FlameTarget, name, "1.0.0", "A summary.", optionalFiles: true, PackExportFormat.CurseForge);

    [Fact]
    public async Task ACurseForgeExportWritesAManifestAndModList()
    {
        WritePackProfile();
        WriteCurseForgeMod("sodium", "sodium.jar", projectId: 394468, fileId: 4573708);

        await FlameExport().RunAsync(CancellationToken.None);

        var entries = EntriesIn(FlameTarget);

        Assert.Contains("manifest.json", entries);
        Assert.Contains("modlist.html", entries);
        Assert.DoesNotContain("modrinth.index.json", entries);

        // And it is recognised as a CurseForge pack by the port's own detector.
        Assert.Equal(ModPlatform.ModpackType.Flame, ModPlatform.PackTypeDetector.Detect(FlameTarget).Type);
    }

    [Fact]
    public async Task ACurseForgeModIsLinkedByItsIdsNotCopiedIntoOverrides()
    {
        WritePackProfile();
        WriteCurseForgeMod("sodium", "sodium.jar", projectId: 394468, fileId: 4573708);

        var task = FlameExport();

        await task.RunAsync(CancellationToken.None);

        var manifest = FlameManifestIn(FlameTarget);
        var file = Assert.Single(manifest["files"]!.AsArray());

        Assert.Equal(394468, (int)file!["projectID"]!);
        Assert.Equal(4573708, (int)file["fileID"]!);
        Assert.Equal(1, task.LinkedCount);

        // The linked jar is NOT also copied in -- that would double the file and bloat the pack.
        Assert.DoesNotContain("overrides/mods/sodium.jar", EntriesIn(FlameTarget));
    }

    [Fact]
    public async Task TheManifestNamesTheGameAndTheLoader()
    {
        WritePackProfile(minecraft: "1.20.1", loaderUid: "net.fabricmc.fabric-loader");
        WriteCurseForgeMod("sodium", "sodium.jar", 1, 2);

        await FlameExport().RunAsync(CancellationToken.None);

        var minecraft = FlameManifestIn(FlameTarget)["minecraft"]!.AsObject();

        Assert.Equal("1.20.1", (string?)minecraft["version"]);

        var loader = Assert.Single(minecraft["modLoaders"]!.AsArray());

        Assert.Equal("fabric-0.15.7", (string?)loader!["id"]);
        Assert.True((bool)loader["primary"]!);
    }

    [Fact]
    public async Task AModrinthModCannotBeLinkedInACurseForgePackSoItIsCarried()
    {
        /*
         * The honest edge of the format: a CurseForge manifest names mods by id, and a Modrinth mod
         * has none. Rather than drop it, the export copies it into overrides -- the mod is still in
         * the pack, just carried rather than referenced.
         */
        WritePackProfile();
        WriteDownloadedMod("lithium", "lithium.jar", "https://cdn.modrinth.com/lithium.jar");

        var task = FlameExport();

        await task.RunAsync(CancellationToken.None);

        // Nothing linked in the manifest...
        Assert.Empty(FlameManifestIn(FlameTarget)["files"]!.AsArray());
        Assert.Equal(0, task.LinkedCount);

        // ...but the jar is carried in overrides, so the pack still contains it.
        Assert.Contains("overrides/mods/lithium.jar", EntriesIn(FlameTarget));
    }

    [Fact]
    public async Task TheModListNamesTheMod()
    {
        WritePackProfile();
        WriteCurseForgeMod("sodium", "sodium.jar", 394468, 4573708);

        await FlameExport().RunAsync(CancellationToken.None);

        using var zip = ZipFile.OpenRead(FlameTarget);
        using var reader = new StreamReader(zip.GetEntry("modlist.html")!.Open());

        var html = reader.ReadToEnd();

        Assert.Contains("sodium", html, StringComparison.Ordinal);
        Assert.Contains("394468", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACurseForgeEntryWhoseJarIsGoneIsNotLinked()
    {
        WritePackProfile();
        WriteCurseForgeMod("sodium", "sodium.jar", 1, 2);

        // Delete the jar, leaving the orphaned .pw.toml -- linking it would name a file nobody has.
        File.Delete(Path.Combine(_paths.GameRoot, "mods", "sodium.jar"));

        var task = FlameExport();

        await task.RunAsync(CancellationToken.None);

        Assert.Empty(FlameManifestIn(FlameTarget)["files"]!.AsArray());
        Assert.Equal(0, task.LinkedCount);
    }

    [Fact]
    public async Task ACurseForgeEntryWithNoIdsIsNotLinked()
    {
        /*
         * A corrupt or half-written packwiz entry: the provider is curseforge but the ids are missing,
         * so they parse as zero. Linking it would put projectID:0, fileID:0 in the manifest -- a file
         * CurseForge cannot resolve -- so the id check drops it, and it falls to overrides instead.
         */
        WritePackProfile();

        WriteGameFile("mods/mystery.jar", "bytes");

        var index = Path.Combine(_paths.GameRoot, ".index");
        Directory.CreateDirectory(index);
        File.WriteAllText(
            Path.Combine(index, "mystery.pw.toml"),
            """
            name = "mystery"
            filename = "mystery.jar"
            side = "both"
            [download]
            mode = "metadata:curseforge"
            hash-format = "sha1"
            hash = "cccc"
            [update]
            [update.curseforge]
            """);

        var task = FlameExport();

        await task.RunAsync(CancellationToken.None);

        Assert.Empty(FlameManifestIn(FlameTarget)["files"]!.AsArray());
        Assert.Equal(0, task.LinkedCount);

        // Not dropped: carried in overrides, so the pack still has the jar.
        Assert.Contains("overrides/mods/mystery.jar", EntriesIn(FlameTarget));
    }

    [Fact]
    public async Task TheExportedManifestParsesBackThroughThePortsOwnFlameReader()
    {
        /*
         * Round-trip proof: the manifest this writes is not merely detected as CurseForge, it is
         * structurally what the port's own import-side parser expects -- name, version, and a file
         * indexed by its ids. If the writer and reader ever disagree, this is where it shows.
         */
        WritePackProfile();
        WriteCurseForgeMod("sodium", "sodium.jar", projectId: 394468, fileId: 4573708);

        await FlameExport("Round Trip").RunAsync(CancellationToken.None);

        byte[] manifestBytes;

        using (var zip = ZipFile.OpenRead(FlameTarget))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            manifestBytes = memory.ToArray();
        }

        var parsed = ModPlatform.FlamePack.Parse(manifestBytes);

        Assert.Equal("Round Trip", parsed.Name);
        Assert.True(parsed.Files.TryGetValue(4573708, out var file));
        Assert.Equal(394468, file!.ProjectId);
    }

    [Fact]
    public async Task TheCurseForgeManifestHasNoByteOrderMark()
    {
        // The same trap the Modrinth manifest has: a BOM makes System.Text.Json and other launchers
        // reject the file. Asserted on the raw bytes, not through a StreamReader that would hide it.
        WritePackProfile();
        WriteCurseForgeMod("sodium", "sodium.jar", 1, 2);

        await FlameExport().RunAsync(CancellationToken.None);

        using var zip = ZipFile.OpenRead(FlameTarget);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        using var memory = new MemoryStream();

        stream.CopyTo(memory);

        var bytes = memory.ToArray();

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }
}
