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
 * Finding out which installed mods are out of date.
 *
 * The stub answers the way the real service does, which was established by probing it rather than
 * assumed -- including the part that matters most: /version_files/update answers for EVERY hash it
 * recognises, current ones included.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ModUpdateCheckTests : IDisposable
{
    private readonly string _gameRoot = Path.Combine(
        Path.GetTempPath(),
        "el-modupd-" + Guid.NewGuid().ToString("N"));

    private readonly string _mods;

    public ModUpdateCheckTests()
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

    /// <summary>Writes a jar and returns the sha512 the checker will compute for it.</summary>
    private string WriteMod(string fileName, string contents)
    {
        File.WriteAllText(Path.Combine(_mods, fileName), contents);

        return Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(contents)));
    }

    private static JsonObject VersionFor(string fileName, string name, string url = "https://cdn.invalid/f.jar")
        => new()
        {
            ["name"] = name,
            ["id"] = "vid",
            ["project_id"] = "pid",
            ["files"] = new JsonArray(
                new JsonObject
                {
                    ["primary"] = true,
                    ["filename"] = fileName,
                    ["url"] = url,
                    ["hashes"] = new JsonObject { ["sha512"] = "deadbeef" },
                }),
        };

    private sealed class StubHandler(JsonObject answer) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = string.Empty;

        public string LastUrl { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();

            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task ANewerBuildIsReportedAsAnUpdate()
    {
        var hash = WriteMod("sodium-0.4.10.jar", "old sodium");

        var answer = new JsonObject { [hash] = VersionFor("sodium-0.5.13.jar", "Sodium 0.5.13") };

        using var client = new HttpClient(new StubHandler(answer));

        var updates = (await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1")).Updates;

        var update = Assert.Single(updates);

        Assert.Equal("sodium-0.4.10.jar", update.CurrentFileName);
        Assert.Equal("sodium-0.5.13.jar", update.NewFileName);
        Assert.Equal("Sodium 0.5.13", update.NewVersionName);
    }

    [Fact]
    public async Task AModThatIsAlreadyCurrentIsNotReported()
    {
        /*
         * ESTABLISHED BY PROBING THE REAL SERVICE. /version_files/update answers for every hash it
         * recognises, INCLUDING ones already current -- its contract is "what this should be", not
         * "what is newer". Without the same-filename check the dialog offers to reinstall every mod
         * in the folder, which looks like everything is stale.
         */
        var hash = WriteMod("sodium-0.5.13.jar", "current sodium");

        var answer = new JsonObject { [hash] = VersionFor("sodium-0.5.13.jar", "Sodium 0.5.13") };

        using var client = new HttpClient(new StubHandler(answer));

        Assert.Empty((await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1")).Updates);
    }

    [Fact]
    public async Task TheRequestFiltersByLoaderAndGameVersion()
    {
        /*
         * Without these the service offers a 1.21 build as the update for a mod in a 1.20.1 instance
         * -- which installs cleanly and then refuses to load.
         */
        WriteMod("sodium.jar", "x");

        var handler = new StubHandler([]);

        using var client = new HttpClient(handler);

        await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1");

        Assert.Contains("/version_files/update", handler.LastUrl, StringComparison.Ordinal);
        Assert.Contains("\"fabric\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"1.20.1\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("sha512", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryModGoesInOneRequest()
    {
        // A hundred mods is one round trip, not a hundred. That is the whole reason this endpoint
        // exists rather than asking per project.
        WriteMod("a.jar", "a");
        WriteMod("b.jar", "b");
        WriteMod("c.jar", "c");

        var handler = new StubHandler([]);

        using var client = new HttpClient(handler);

        await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1");

        var hashes = JsonNode.Parse(handler.LastBody)!["hashes"]!.AsArray();

        Assert.Equal(3, hashes.Count);
    }

    [Fact]
    public async Task ADisabledModIsCheckedAndStaysDisabled()
    {
        /*
         * A disabled mod is one somebody turned off, not one they removed -- they will turn it back on
         * one day and want it current. Updating it must not silently re-enable it.
         */
        var hash = WriteMod("sodium-0.4.10.jar.disabled", "old sodium");

        var answer = new JsonObject { [hash] = VersionFor("sodium-0.5.13.jar", "Sodium 0.5.13") };

        using var client = new HttpClient(new StubHandler(answer));

        var update = Assert.Single((await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1")).Updates);

        Assert.True(update.WasDisabled);

        // Hashed and reported under its REAL name, not the .disabled one.
        Assert.Equal("sodium-0.4.10.jar", update.CurrentFileName);
    }

    [Fact]
    public async Task SomethingThatIsNotAJarIsIgnored()
    {
        File.WriteAllText(Path.Combine(_mods, "notes.txt"), "not a mod");

        var handler = new StubHandler([]);

        using var client = new HttpClient(handler);

        Assert.Empty((await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1")).Updates);

        // And nothing was asked at all, because there was nothing to ask about.
        Assert.Equal(string.Empty, handler.LastUrl);
    }

    [Fact]
    public async Task AVanillaInstanceIsNotChecked()
    {
        /*
         * Modrinth's filter requires a loader. Sending an empty list matches nothing and comes back
         * as "no updates", which is indistinguishable from "everything is current" -- so this refuses
         * up front and lets the caller say why.
         */
        WriteMod("something.jar", "x");

        var handler = new StubHandler([]);

        using var client = new HttpClient(handler);

        Assert.Empty((await new ModUpdateCheck(client).FindAsync(_gameRoot, string.Empty, "1.20.1")).Updates);
        Assert.Equal(string.Empty, handler.LastUrl);
    }

    [Fact]
    public async Task AnInstanceWithNoModsFolderIsFine()
    {
        Directory.Delete(_mods, recursive: true);

        using var client = new HttpClient(new StubHandler([]));

        Assert.Empty((await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1")).Updates);
    }

    [Fact]
    public async Task AHashTheServiceDoesNotKnowIsSimplyAbsent()
    {
        // A mod from CurseForge, or one built by hand. Not an error -- Modrinth omits what it does
        // not recognise rather than reporting it.
        WriteMod("handmade.jar", "unknown to modrinth");

        using var client = new HttpClient(new StubHandler([]));

        Assert.Empty((await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1")).Updates);
    }

    [Fact]
    public async Task ANonPrimaryFileIsNotOfferedAsTheUpdate()
    {
        /*
         * A version can carry a sources or javadoc jar alongside the mod. Installing one of those in
         * place of the mod breaks the instance in a way that looks like the mod itself is broken.
         */
        var hash = WriteMod("sodium-0.4.10.jar", "old");

        var version = VersionFor("sodium-0.5.13.jar", "Sodium 0.5.13");

        version["files"]!.AsArray().Insert(0, new JsonObject
        {
            ["primary"] = false,
            ["filename"] = "sodium-0.5.13-sources.jar",
            ["url"] = "https://cdn.invalid/sources.jar",
            ["hashes"] = new JsonObject { ["sha512"] = "cafe" },
        });

        using var client = new HttpClient(new StubHandler(new JsonObject { [hash] = version }));

        var update = Assert.Single((await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1")).Updates);

        Assert.Equal("sodium-0.5.13.jar", update.NewFileName);
    }

    [Fact]
    public async Task AFailedRequestSaysSoRatherThanReportingNoUpdates()
    {
        // "No updates" and "could not check" are different answers, and only one of them means the
        // instance is up to date.
        WriteMod("sodium.jar", "x");

        using var client = new HttpClient(new FailingHandler());

        var check = new ModUpdateCheck(client);

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => check.FindAsync(_gameRoot, "Fabric", "1.20.1"));

        Assert.Contains("Could not check", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUpdatesComeBackInAReadableOrder()
    {
        var a = WriteMod("zebra-1.jar", "z");
        var b = WriteMod("apple-1.jar", "a");

        var answer = new JsonObject
        {
            [a] = VersionFor("zebra-2.jar", "Zebra 2"),
            [b] = VersionFor("apple-2.jar", "Apple 2"),
        };

        using var client = new HttpClient(new StubHandler(answer));

        var updates = (await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1")).Updates;

        Assert.Equal(["apple-1", "zebra-1"], updates.Select(u => u.Name).ToArray());
    }

    [Fact]
    public async Task AModTheServiceDoesNotKnowIsCountedSoTheUserIsTold()
    {
        /*
         * ESTABLISHED BY PROBING: the endpoint returns a key for every hash it recognises, INCLUDING
         * ones already current, and omits only what it has never seen. So the difference between
         * "current" and "not from Modrinth" is exactly presence in the answer -- and without counting
         * it, a folder full of CurseForge mods reports "everything is up to date", which is wrong in
         * the way somebody acts on.
         */
        var known = WriteMod("sodium-0.5.13.jar", "known to modrinth");

        WriteMod("from-curseforge.jar", "never seen");
        WriteMod("built-by-hand.jar", "also never seen");

        var answer = new JsonObject { [known] = VersionFor("sodium-0.5.13.jar", "Sodium 0.5.13") };

        using var client = new HttpClient(new StubHandler(answer));

        var report = await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1");

        Assert.Empty(report.Updates);
        Assert.Equal(3, report.Checked);
        Assert.Equal(2, report.Unrecognised);
    }

    [Fact]
    public async Task NothingIsUnrecognisedWhenTheServiceKnowsThemAll()
    {
        var hash = WriteMod("sodium-0.4.10.jar", "old");

        var answer = new JsonObject { [hash] = VersionFor("sodium-0.5.13.jar", "Sodium 0.5.13") };

        using var client = new HttpClient(new StubHandler(answer));

        var report = await new ModUpdateCheck(client).FindAsync(_gameRoot, "Fabric", "1.20.1");

        Assert.Single(report.Updates);
        Assert.Equal(0, report.Unrecognised);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("api.modrinth.com could not be resolved");
    }
}
