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
 * Installing a mod update.
 *
 * ASSERTED ON THE MODS FOLDER, because the folder is what the game reads. The ordering is the whole
 * point of the class: the new file must be down and verified before the old one moves, or a failed
 * download leaves an instance that will not start.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ModUpdateTaskTests : IDisposable
{
    private readonly string _gameRoot = Path.Combine(
        Path.GetTempPath(),
        "el-updtask-" + Guid.NewGuid().ToString("N"));

    private readonly string _mods;

    public ModUpdateTaskTests()
    {
        _mods = Path.Combine(_gameRoot, "mods");

        Directory.CreateDirectory(_mods);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_gameRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string NewBytes = "brand new mod jar";

    private static string NewHash => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(NewBytes)));

    private static ModUpdate Update(bool disabled = false, string hash = "") => new(
        Name: "Sodium",
        CurrentFileName: "sodium-0.4.10.jar",
        NewFileName: "sodium-0.5.13.jar",
        NewVersionName: "Sodium 0.5.13",
        DownloadUrl: "https://cdn.invalid/sodium-0.5.13.jar",
        Sha512: hash.Length == 0 ? NewHash : hash,
        ProjectId: "AANobbMI",
        VersionId: "abc")
    {
        WasDisabled = disabled,
    };

    private sealed class ServingHandler(string body = NewBytes, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
            });
    }

    private void WriteInstalled(string name, string contents = "the old jar")
        => File.WriteAllText(Path.Combine(_mods, name), contents);

    [Fact]
    public async Task TheNewFileReplacesTheOldOne()
    {
        WriteInstalled("sodium-0.4.10.jar");

        using var client = new HttpClient(new ServingHandler());

        var task = new ModUpdateTask([Update()], _gameRoot, client);

        Assert.True(await task.RunAsync(CancellationToken.None));
        Assert.Equal(1, task.UpdatedCount);

        Assert.True(File.Exists(Path.Combine(_mods, "sodium-0.5.13.jar")));
        Assert.Equal(NewBytes, File.ReadAllText(Path.Combine(_mods, "sodium-0.5.13.jar")));

        // Not still there under its own name, or the game loads both and refuses to start.
        Assert.False(File.Exists(Path.Combine(_mods, "sodium-0.4.10.jar")));
    }

    [Fact]
    public async Task TheOldFileIsKeptRatherThanDeleted()
    {
        /*
         * A new build with a new crash is one somebody wants to go back from, and finding the old
         * version on a website is a miserable way to spend an evening. ".old" is ignored by the game
         * because it is not a .jar, and is obvious enough to delete by hand.
         */
        WriteInstalled("sodium-0.4.10.jar", "the old jar");

        using var client = new HttpClient(new ServingHandler());

        await new ModUpdateTask([Update()], _gameRoot, client).RunAsync(CancellationToken.None);

        var kept = Path.Combine(_mods, "sodium-0.4.10.jar" + ModUpdateTask.ReplacedSuffix);

        Assert.True(File.Exists(kept));
        Assert.Equal("the old jar", File.ReadAllText(kept));
    }

    [Fact]
    public async Task AFailedDownloadLeavesTheWorkingModAlone()
    {
        /*
         * THE ORDERING THIS CLASS EXISTS FOR. Replacing a working mod with a failed download is how an
         * instance stops starting, and the user's next move is to launch it.
         */
        WriteInstalled("sodium-0.4.10.jar", "the old jar");

        using var client = new HttpClient(new ServingHandler(status: HttpStatusCode.NotFound));

        var task = new ModUpdateTask([Update()], _gameRoot, client);

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(0, task.UpdatedCount);
        Assert.Single(task.Failures);

        Assert.True(File.Exists(Path.Combine(_mods, "sodium-0.4.10.jar")));
        Assert.Equal("the old jar", File.ReadAllText(Path.Combine(_mods, "sodium-0.4.10.jar")));
        Assert.False(File.Exists(Path.Combine(_mods, "sodium-0.5.13.jar")));
    }

    [Fact]
    public async Task AFileThatFailsItsHashIsNotInstalled()
    {
        // A jar arriving corrupt and being installed over a working one is exactly what the hash and
        // the ordering exist to prevent.
        WriteInstalled("sodium-0.4.10.jar", "the old jar");

        using var client = new HttpClient(new ServingHandler());

        var task = new ModUpdateTask([Update(hash: new string('a', 128))], _gameRoot, client);

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(0, task.UpdatedCount);
        Assert.True(File.Exists(Path.Combine(_mods, "sodium-0.4.10.jar")));
        Assert.False(File.Exists(Path.Combine(_mods, "sodium-0.5.13.jar")));
    }

    [Fact]
    public async Task NoPartFileIsLeftBehindOnFailure()
    {
        WriteInstalled("sodium-0.4.10.jar");

        using var client = new HttpClient(new ServingHandler(status: HttpStatusCode.NotFound));

        await new ModUpdateTask([Update()], _gameRoot, client).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(
            Directory.GetFiles(_mods).Select(Path.GetFileName),
            n => n!.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADisabledModStaysDisabled()
    {
        /*
         * A mod somebody turned off is one they will turn back on some day. Updating it must not
         * silently put it back into the game -- which is a change they did not ask for and would find
         * out about by crashing.
         */
        WriteInstalled("sodium-0.4.10.jar" + PackExport.DisabledSuffix);

        using var client = new HttpClient(new ServingHandler());

        await new ModUpdateTask([Update(disabled: true)], _gameRoot, client).RunAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_mods, "sodium-0.5.13.jar" + PackExport.DisabledSuffix)));
        Assert.False(File.Exists(Path.Combine(_mods, "sodium-0.5.13.jar")));
    }

    [Fact]
    public async Task OneFailureDoesNotAbandonTheRest()
    {
        WriteInstalled("sodium-0.4.10.jar");

        using var client = new HttpClient(new ServingHandler());

        var good = Update();

        var bad = good with
        {
            Name = "Missing",
            CurrentFileName = "missing-1.jar",
            NewFileName = "missing-2.jar",
            Sha512 = new string('b', 128),
        };

        var task = new ModUpdateTask([bad, good], _gameRoot, client);

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(1, task.UpdatedCount);
        Assert.Single(task.Failures);
        Assert.Contains("Missing", task.Failures[0], StringComparison.Ordinal);

        // The good one still went in, despite being listed after the failure.
        Assert.True(File.Exists(Path.Combine(_mods, "sodium-0.5.13.jar")));
    }

    [Fact]
    public async Task ThePackwizEntryFollowsTheNewFile()
    {
        /*
         * ALL THREE FIELDS. PackExportTask reads these back to decide what to link, so an entry left
         * with the old url under the new name would export a pack handing everybody who installs it
         * the version this instance just moved off.
         */
        WriteInstalled("sodium-0.4.10.jar");

        var index = Path.Combine(_gameRoot, ".index");

        Directory.CreateDirectory(index);

        File.WriteAllText(
            Path.Combine(index, "sodium.pw.toml"),
            """
            name = "sodium"
            filename = "sodium-0.4.10.jar"
            side = "both"

            [download]
            mode = "url"
            url = "https://cdn.invalid/sodium-0.4.10.jar"
            hash-format = "sha512"
            hash = "oldhash"

            [update]
            [update.modrinth]
            mod-id = "AANobbMI"
            version = "oldversionid"
            """);

        using var client = new HttpClient(new ServingHandler());

        await new ModUpdateTask([Update()], _gameRoot, client).RunAsync(CancellationToken.None);

        var written = File.ReadAllText(Path.Combine(index, "sodium.pw.toml"));

        Assert.Contains("sodium-0.5.13.jar", written, StringComparison.Ordinal);
        Assert.Contains("https://cdn.invalid/sodium-0.5.13.jar", written, StringComparison.Ordinal);
        Assert.Contains(NewHash, written, StringComparison.Ordinal);

        // And nothing of the old one survives to confuse a later export.
        Assert.DoesNotContain("0.4.10", written, StringComparison.Ordinal);
        Assert.DoesNotContain("oldhash", written, StringComparison.Ordinal);

        // hash-format must NOT have been rewritten by the "hash" rule -- it is a different key.
        Assert.Contains("hash-format = \"sha512\"", written, StringComparison.Ordinal);

        /*
         * The version id under [update.modrinth] too. This was missed in the first implementation and
         * found by reading a file a REAL update had produced: filename, url and hash had all moved on
         * and this still named the version being replaced. It is what packwiz reads to decide what is
         * installed, so a stale one has any packwiz tool conclude the old version is still here.
         */
        Assert.Contains("version = \"abc\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("oldversionid", written, StringComparison.Ordinal);

        // And the project id is NOT touched: the mod is the same mod.
        Assert.Contains("mod-id = \"AANobbMI\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstanceWithNoIndexUpdatesAnyway()
    {
        // Mods dropped in by hand have no metadata; that is not a reason to refuse to update them.
        WriteInstalled("sodium-0.4.10.jar");

        using var client = new HttpClient(new ServingHandler());

        var task = new ModUpdateTask([Update()], _gameRoot, client);

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(1, task.UpdatedCount);
    }

    [Fact]
    public async Task UpdatingTwiceDoesNotTripOverTheKeptFile()
    {
        // The second update finds a .old already there from the first.
        WriteInstalled("sodium-0.4.10.jar");

        using var client = new HttpClient(new ServingHandler());

        await new ModUpdateTask([Update()], _gameRoot, client).RunAsync(CancellationToken.None);

        var second = Update() with
        {
            CurrentFileName = "sodium-0.5.13.jar",
            NewFileName = "sodium-0.6.0.jar",
        };

        var task = new ModUpdateTask([second], _gameRoot, client);

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(1, task.UpdatedCount);
        Assert.True(File.Exists(Path.Combine(_mods, "sodium-0.6.0.jar")));
    }
}
