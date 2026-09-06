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
 * Carrying out a pack update in a folder somebody has been playing in.
 *
 * THE ORDERING IS THE THING UNDER TEST. Downloading before deleting is what makes a failed update
 * survivable, and the only way to prove it is to make the download fail and then look at the folder.
 * Several tests here do exactly that.
 */

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class PackUpdateTaskTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-upd-" + Guid.NewGuid().ToString("N"));

    public PackUpdateTaskTests()
    {
        Directory.CreateDirectory(GameRoot);
        Directory.CreateDirectory(Path.Combine(GameRoot, "mods"));
    }

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

    private string InstanceRoot => Path.Combine(_temp, "instance");

    private string GameRoot => Path.Combine(InstanceRoot, "minecraft");

    /// <summary>Serves whatever the test set up, and can be told to fail.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        /// <summary>URLs that never answer, so a cancellation has something to interrupt.</summary>
        public HashSet<string> Hanging { get; } = new(StringComparer.Ordinal);

        /// <summary>Set once a hanging request has actually been reached.</summary>
        public TaskCompletionSource Reached { get; } = new();

        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;

            var url = request.RequestUri!.ToString();

            if (Hanging.Contains(url))
            {
                Reached.TrySetResult();

                // Waits for the token rather than for a timeout, so the test is not a race.
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Failing.Contains(url) || !Files.TryGetValue(url, out var body))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }

    private static byte[] BuildPack(string name, string versionId, params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = files.Select(f => new
            {
                f.Path,
                Hash = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(f.Content))),
            });

            var json = $$"""
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "{{versionId}}",
              "name": "{{name}}",
              "files": [
            {{string.Join(",\n", entries.Select(e => $$"""
                {
                  "path": "{{e.Path}}",
                  "hashes": { "sha512": "{{e.Hash}}" },
                  "downloads": ["https://files.example.invalid/{{Uri.EscapeDataString(e.Path)}}"],
                  "fileSize": 1
                }
            """))}}
              ],
              "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.7" }
            }
            """;

            var entry = zip.CreateEntry("modrinth.index.json");

            using var writer = new StreamWriter(entry.Open());

            writer.Write(json);
        }

        return memory.ToArray();
    }

    private static IndexedVersion Version(string fileId, string name)
        => new()
        {
            FileId = fileId,
            Version = name,
            FileName = $"{name}.mrpack",
            DownloadUrl = $"https://packs.example.invalid/{fileId}.mrpack",
        };

    /// <summary>Puts an instance on disk as if the old version had been installed.</summary>
    private void GiveItAnInstalledVersion(params (string Path, string Content)[] files)
    {
        var packBytes = BuildPack("A Pack", "1.0.0", files);

        Directory.CreateDirectory(Path.Combine(InstanceRoot, "mrpack"));

        PackLedgerStore.WriteManifest(InstanceRoot, ManifestBytesOf(packBytes));
        PackLedgerStore.WriteOverrides(InstanceRoot, "overrides", []);
        PackLedgerStore.WriteOverrides(InstanceRoot, "client-overrides", []);

        foreach (var (path, content) in files)
        {
            var target = Path.Combine(GameRoot, path);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
    }

    private static byte[] ManifestBytesOf(byte[] packBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(packBytes), ZipArchiveMode.Read);
        using var stream = archive.GetEntry("modrinth.index.json")!.Open();
        using var memory = new MemoryStream();

        stream.CopyTo(memory);

        return memory.ToArray();
    }

    private SettingsObject Config()
    {
        var settings = new IniSettingsObject(Path.Combine(InstanceRoot, "instance.cfg"));

        settings.RegisterSetting("ManagedPackVersionID", string.Empty);
        settings.RegisterSetting("ManagedPackVersionName", string.Empty);
        settings.RegisterSetting("ManagedPackName", string.Empty);

        return settings;
    }

    private (PackUpdateTask Task, StubHandler Handler, HttpClient Client) Updating(
        params (string Path, string Content)[] newFiles)
    {
        var handler = new StubHandler();
        var version = Version("v2", "2.0.0");

        handler.Files[version.DownloadUrl] = BuildPack("A Pack", "2.0.0", newFiles);

        foreach (var (path, content) in newFiles)
        {
            handler.Files[$"https://files.example.invalid/{Uri.EscapeDataString(path)}"] =
                Encoding.UTF8.GetBytes(content);
        }

        var client = new HttpClient(handler);

        var task = new PackUpdateTask(
            client,
            InstanceRoot,
            GameRoot,
            new IndexedPack { Provider = ResourceProvider.Modrinth, AddonId = "abc", Name = "A Pack" },
            version,
            Config());

        return (task, handler, client);
    }

    [Fact]
    public async Task AnUpdateReplacesTheOldFilesWithTheNew()
    {
        GiveItAnInstalledVersion(("mods/a.jar", "old a"), ("mods/gone.jar", "gone"));

        var (task, _, client) = Updating(("mods/a.jar", "new a"), ("mods/b.jar", "b"));

        using (client)
        {
            Assert.True(await task.RunAsync(), task.FailReason);
        }

        Assert.Equal("new a", File.ReadAllText(Path.Combine(GameRoot, "mods", "a.jar")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(GameRoot, "mods", "b.jar")));
        Assert.False(File.Exists(Path.Combine(GameRoot, "mods", "gone.jar")));
    }

    [Fact]
    public async Task AFileThePlayerAddedSurvives()
    {
        /*
         * THE PROMISE THE LEDGER EXISTS TO KEEP. Their own mod is in neither manifest, so nothing in
         * the plan mentions it, and an update must walk straight past it.
         */
        GiveItAnInstalledVersion(("mods/a.jar", "old a"));

        File.WriteAllText(Path.Combine(GameRoot, "mods", "my-own-mod.jar"), "mine");
        File.WriteAllText(Path.Combine(GameRoot, "options.txt"), "my settings");

        var (task, _, client) = Updating(("mods/a.jar", "new a"));

        using (client)
        {
            Assert.True(await task.RunAsync(), task.FailReason);
        }

        Assert.Equal("mine", File.ReadAllText(Path.Combine(GameRoot, "mods", "my-own-mod.jar")));
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(GameRoot, "options.txt")));
    }

    [Fact]
    public async Task AFailedDownloadLeavesTheInstanceExactlyAsItWas()
    {
        /*
         * THE WHOLE REASON FOR THE ORDERING, and the test that proves it. The new version's second
         * file cannot be fetched; if deleting came first, the instance would now be missing a mod it
         * needs and have no way back.
         */
        GiveItAnInstalledVersion(("mods/a.jar", "old a"), ("mods/gone.jar", "gone"));

        var (task, handler, client) = Updating(("mods/a.jar", "new a"), ("mods/b.jar", "b"));

        handler.Failing.Add("https://files.example.invalid/mods%2Fb.jar");

        using (client)
        {
            Assert.False(await task.RunAsync());
        }

        // Every file the old version had is still there, with its old contents.
        Assert.Equal("old a", File.ReadAllText(Path.Combine(GameRoot, "mods", "a.jar")));
        Assert.Equal("gone", File.ReadAllText(Path.Combine(GameRoot, "mods", "gone.jar")));

        // And the failure says so, because "update failed" leaves somebody wondering whether their
        // instance is now broken.
        Assert.Contains("exactly as it was", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedDownloadLeavesTheRecordedVersionAlone()
    {
        // The instance still IS the old version, and saying otherwise would make a retry compare
        // against the wrong thing.
        GiveItAnInstalledVersion(("mods/a.jar", "old a"));

        var config = Config();

        config.Set("ManagedPackVersionName", "1.0.0");

        var (task, handler, client) = Updating(("mods/a.jar", "new a"));

        handler.Failing.Add("https://files.example.invalid/mods%2Fa.jar");

        using (client)
        {
            Assert.False(await task.RunAsync());
        }

        Assert.Equal("1.0.0", Config().GetString("ManagedPackVersionName"));
    }

    [Fact]
    public async Task AnInstanceWithNoLedgerIsRefusedRatherThanGuessedAt()
    {
        // Nothing was ever recorded, so nothing can be told apart. Nothing is touched either.
        File.WriteAllText(Path.Combine(GameRoot, "mods", "something.jar"), "who put this here");

        var (task, _, client) = Updating(("mods/a.jar", "a"));

        using (client)
        {
            Assert.False(await task.RunAsync());
        }

        Assert.Contains("no record of which files came from the pack", task.FailReason, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(GameRoot, "mods", "something.jar")));
    }

    [Fact]
    public async Task TheInstanceEndsUpRecordedAsTheNewVersion()
    {
        GiveItAnInstalledVersion(("mods/a.jar", "old a"));

        var (task, _, client) = Updating(("mods/a.jar", "new a"));

        using (client)
        {
            Assert.True(await task.RunAsync(), task.FailReason);
        }

        var after = Config();

        Assert.Equal("v2", after.GetString("ManagedPackVersionID"));
        Assert.Equal("2.0.0", after.GetString("ManagedPackVersionName"));
    }

    [Fact]
    public async Task TheLedgerIsRewrittenSoTheNextUpdateWorks()
    {
        /*
         * An update that did not leave a new ledger would work exactly once, and the SECOND update
         * would refuse -- or worse, diff against the version before last.
         */
        GiveItAnInstalledVersion(("mods/a.jar", "old a"));

        var (task, _, client) = Updating(("mods/a.jar", "new a"), ("mods/b.jar", "b"));

        using (client)
        {
            Assert.True(await task.RunAsync(), task.FailReason);
        }

        var ledger = PackLedgerStore.Read(InstanceRoot);

        Assert.True(ledger.Exists);
        Assert.Equal("2.0.0", ledger.Manifest?.VersionId);
        Assert.Equal(2, ledger.Manifest?.Files.Count);
    }

    [Fact]
    public async Task AFileAlreadyDeletedByHandIsNotAnError()
    {
        // Somebody who removed a mod themselves gets the outcome they wanted anyway.
        GiveItAnInstalledVersion(("mods/a.jar", "old a"), ("mods/gone.jar", "gone"));

        File.Delete(Path.Combine(GameRoot, "mods", "gone.jar"));

        var (task, _, client) = Updating(("mods/a.jar", "new a"));

        using (client)
        {
            Assert.True(await task.RunAsync(), task.FailReason);
        }

        Assert.Equal(1, task.Removed);
    }

    [Fact]
    public async Task AnUnchangedFileIsNotFetchedAgain()
    {
        /*
         * The hash match earning its keep. A pack whose mod list barely moved between releases should
         * cost a handful of downloads, not two hundred.
         */
        GiveItAnInstalledVersion(("mods/a.jar", "same"), ("mods/b.jar", "same b"));

        var (task, handler, client) = Updating(("mods/a.jar", "same"), ("mods/b.jar", "same b"), ("mods/c.jar", "c"));

        using (client)
        {
            Assert.True(await task.RunAsync(), task.FailReason);
        }

        Assert.Equal(1, task.Downloaded);
        Assert.Equal(2, task.Plan?.Unchanged);

        // Still on disk, never re-fetched.
        Assert.Equal("same", File.ReadAllText(Path.Combine(GameRoot, "mods", "a.jar")));
    }

    [Fact]
    public async Task CancellingDuringTheDownloadLeavesTheInstanceExactlyAsItWas()
    {
        /*
         * A DIFFERENT PATH FROM A FAILED DOWNLOAD, and worth its own test: a cancellation comes out
         * as OperationCanceledException rather than TaskFailedException, and LauncherTask handles the
         * two in separate catch blocks. The safety property has to hold for both.
         *
         * This is also the case a person actually causes -- they press Cancel -- where a failed
         * download is the case the network causes.
         */
        GiveItAnInstalledVersion(("mods/a.jar", "old a"), ("mods/gone.jar", "gone"));

        var (task, handler, client) = Updating(("mods/a.jar", "new a"), ("mods/b.jar", "b"));

        handler.Hanging.Add("https://files.example.invalid/mods%2Fb.jar");

        using var cancellation = new CancellationTokenSource();

        using (client)
        {
            var run = task.RunAsync(cancellation.Token);

            // Cancel only once the hanging request has really been reached, so this is not a race.
            await handler.Reached.Task;

            await cancellation.CancelAsync();

            Assert.False(await run);
        }

        Assert.Equal("Aborted.", task.FailReason);

        // Every file the old version had, untouched.
        Assert.Equal("old a", File.ReadAllText(Path.Combine(GameRoot, "mods", "a.jar")));
        Assert.Equal("gone", File.ReadAllText(Path.Combine(GameRoot, "mods", "gone.jar")));
    }

    [Fact]
    public async Task CancellingLeavesTheRecordedVersionAlone()
    {
        // The instance still IS the old version, and a retry has to compare against that.
        GiveItAnInstalledVersion(("mods/a.jar", "old a"));

        var config = Config();

        config.Set("ManagedPackVersionName", "1.0.0");

        var (task, handler, client) = Updating(("mods/a.jar", "new a"));

        handler.Hanging.Add("https://files.example.invalid/mods%2Fa.jar");

        using var cancellation = new CancellationTokenSource();

        using (client)
        {
            var run = task.RunAsync(cancellation.Token);

            await handler.Reached.Task;
            await cancellation.CancelAsync();

            Assert.False(await run);
        }

        Assert.Equal("1.0.0", Config().GetString("ManagedPackVersionName"));
    }

    [Fact]
    public async Task OnceTheDownloadIsDoneCancellingCannotInterruptTheRest()
    {
        /*
         * THE DESTRUCTIVE PHASE IS ATOMIC WITH RESPECT TO CANCELLATION, and that is deliberate rather
         * than lucky: removing, moving in, laying down overrides and rewriting the ledger are all
         * SYNCHRONOUS, so there is no await between them for a token to be observed at.
         *
         * A cancellation point in the middle of that sequence would be the one way to produce the
         * outcome the whole ordering exists to avoid -- an instance with its old files deleted and
         * its new ones not yet in place.
         *
         * Asserted by cancelling AFTER the last download has been served: the update still completes.
         */
        GiveItAnInstalledVersion(("mods/a.jar", "old a"), ("mods/gone.jar", "gone"));

        var (task, handler, client) = Updating(("mods/a.jar", "new a"));

        using var cancellation = new CancellationTokenSource();

        using (client)
        {
            var run = task.RunAsync(cancellation.Token);

            Assert.True(await run, task.FailReason);

            // Cancelling now is too late to do any harm, which is the point.
            await cancellation.CancelAsync();
        }

        Assert.Equal("new a", File.ReadAllText(Path.Combine(GameRoot, "mods", "a.jar")));
        Assert.False(File.Exists(Path.Combine(GameRoot, "mods", "gone.jar")));
        Assert.Equal("2.0.0", Config().GetString("ManagedPackVersionName"));
    }

    [Fact]
    public async Task ALedgerPathThatEscapesTheInstanceIsRefused()
    {
        /*
         * THE DELETE-SIDE OF UPSTREAM BUG #12. The removal list is text on disk -- another launcher
         * wrote it, or somebody edited it -- and a path of "../../../something" must not be followed
         * out of the instance. Getting this wrong on the delete side is worse than on the write side.
         */
        GiveItAnInstalledVersion(("mods/a.jar", "old a"));

        var outside = Path.Combine(_temp, "not-part-of-the-instance.txt");

        File.WriteAllText(outside, "important");

        PackLedgerStore.WriteOverrides(InstanceRoot, "overrides", ["../../not-part-of-the-instance.txt"]);

        var (task, _, client) = Updating(("mods/a.jar", "new a"));

        using (client)
        {
            Assert.True(await task.RunAsync(), task.FailReason);
        }

        Assert.True(File.Exists(outside), "an update followed a path out of the instance folder");
    }
}
