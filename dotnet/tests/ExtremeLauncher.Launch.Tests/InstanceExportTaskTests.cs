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
 * Exporting an instance as a zip.
 *
 * ASSERTED ON THE ARCHIVE, opened and read back with a plain ZipArchive rather than through anything
 * this port wrote. A zip is what somebody hands to a friend, and the only question that matters is
 * what is inside it when it gets there.
 */

using System.IO.Compression;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstanceExportTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "el-export-" + Guid.NewGuid().ToString("N"));

    private readonly string _instance;

    public InstanceExportTaskTests()
    {
        _instance = Path.Combine(_root, "instance");

        Directory.CreateDirectory(_instance);
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

    private string Target => Path.Combine(_root, "exported.zip");

    private void Write(string relativePath, string contents = "x")
    {
        var path = Path.Combine(_instance, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string[] EntriesIn(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);

        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private void Populate()
    {
        Write("instance.cfg", "name=Test\n");
        Write("mmc-pack.json", "{}");
        Write(".minecraft/options.txt", "fov:90");
        Write(".minecraft/mods/sodium.jar");
        Write(".minecraft/saves/World/level.dat");
        Write(".minecraft/logs/latest.log", "lots of noise");
        Write(".minecraft/crash-reports/crash.txt", "a crash");
        Write(".minecraft/screenshots/shot.png");
    }

    [Fact]
    public async Task TheInstancesOwnFilesAreInTheArchive()
    {
        Populate();

        var task = new InstanceExportTask(_instance, Target);

        await task.RunAsync(CancellationToken.None);

        var entries = EntriesIn(Target);

        // instance.cfg and mmc-pack.json ARE the instance -- an export without them restores nothing.
        Assert.Contains("instance.cfg", entries);
        Assert.Contains("mmc-pack.json", entries);
        Assert.Contains(".minecraft/options.txt", entries);
        Assert.Contains(".minecraft/mods/sodium.jar", entries);
        Assert.Contains(".minecraft/saves/World/level.dat", entries);
    }

    [Fact]
    public async Task TheNoiseIsLeftOutByDefault()
    {
        /*
         * Logs, crash reports and screenshots are often the biggest thing in an instance folder and
         * none of them belong in something you send to somebody else.
         */
        Populate();

        await new InstanceExportTask(_instance, Target).RunAsync(CancellationToken.None);

        var entries = EntriesIn(Target);

        Assert.DoesNotContain(entries, e => e.StartsWith(".minecraft/logs/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.StartsWith(".minecraft/crash-reports/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.StartsWith(".minecraft/screenshots/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExclusionMatchesWholeSegmentsRatherThanAnyPrefix()
    {
        /*
         * ".minecraft/logs" must not also exclude ".minecraft/logsomething", which a plain StartsWith
         * would. Somebody's mod configuration folder called "logsettings" is not a log.
         */
        Write("instance.cfg");
        Write(".minecraft/logs/latest.log");
        Write(".minecraft/logsomething/keep.txt");

        await new InstanceExportTask(_instance, Target).RunAsync(CancellationToken.None);

        var entries = EntriesIn(Target);

        Assert.Contains(".minecraft/logsomething/keep.txt", entries);
        Assert.DoesNotContain(".minecraft/logs/latest.log", entries);
    }

    [Fact]
    public async Task ExportingEverythingIsPossibleWhenTheGoalIsABackup()
    {
        // An empty exclusion set is a legitimate thing to want.
        Populate();

        await new InstanceExportTask(_instance, Target, []).RunAsync(CancellationToken.None);

        Assert.Contains(".minecraft/logs/latest.log", EntriesIn(Target));
    }

    [Fact]
    public async Task ExtraExclusionsCanBeGiven()
    {
        Populate();

        await new InstanceExportTask(_instance, Target, [".minecraft/saves"]).RunAsync(CancellationToken.None);

        var entries = EntriesIn(Target);

        Assert.DoesNotContain(entries, e => e.StartsWith(".minecraft/saves/", StringComparison.Ordinal));

        // And the ones the caller did NOT name are now included, because the default set was replaced
        // rather than added to -- which is what passing an explicit list means.
        Assert.Contains(".minecraft/logs/latest.log", entries);
    }

    [Fact]
    public async Task TheContentsSurviveTheRoundTrip()
    {
        // Not just the names: an archive full of empty files would pass every test above.
        Write("instance.cfg", "name=Round Trip\n");

        await new InstanceExportTask(_instance, Target).RunAsync(CancellationToken.None);

        using var zip = ZipFile.OpenRead(Target);
        using var stream = zip.GetEntry("instance.cfg")!.Open();
        using var reader = new StreamReader(stream);

        Assert.Equal("name=Round Trip\n", reader.ReadToEnd());
    }

    [Fact]
    public async Task ExportingReportsWhatItDid()
    {
        Populate();

        var task = new InstanceExportTask(_instance, Target);

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(5, task.FileCount);
        Assert.True(task.ArchiveSize > 0);
    }

    [Fact]
    public async Task NoPartFileIsLeftBehind()
    {
        /*
         * The archive is written to a .part and moved into place, so a half-written zip never appears
         * under the name the user chose -- they are usually watching that folder and would pick it up.
         */
        Populate();

        await new InstanceExportTask(_instance, Target).RunAsync(CancellationToken.None);

        Assert.False(File.Exists(Target + ".part"));
        Assert.Equal(new[] { "exported.zip" }, Directory.GetFiles(_root).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task ExportingAnInstanceThatIsNotThereFails()
    {
        /*
         * RunAsync REPORTS rather than throws -- LauncherTask turns any escape into a failed state
         * with a reason, so a bad file in a batch cannot take the batch down. My first draft of these
         * two tests asserted a throw and failed for that reason alone, which is the right way round:
         * the contract is the task's, not mine.
         */
        var task = new InstanceExportTask(Path.Combine(_root, "gone"), Target);

        Assert.False(await task.RunAsync(CancellationToken.None));
        Assert.Contains("no instance", task.FailReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task ExcludingEverythingIsRefusedRatherThanWritingAnEmptyZip()
    {
        // An empty archive looks like a successful export right up until somebody tries to use it.
        Write(".minecraft/logs/latest.log");

        var task = new InstanceExportTask(_instance, Target);

        Assert.False(await task.RunAsync(CancellationToken.None));
        Assert.Contains("nothing to export", task.FailReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task OverwritingAnEarlierExportWorks()
    {
        // Exporting twice to the same name is what somebody does after changing one thing.
        Populate();

        await new InstanceExportTask(_instance, Target).RunAsync(CancellationToken.None);

        Write("newfile.txt");

        await new InstanceExportTask(_instance, Target).RunAsync(CancellationToken.None);

        Assert.Contains("newfile.txt", EntriesIn(Target));
    }
}
