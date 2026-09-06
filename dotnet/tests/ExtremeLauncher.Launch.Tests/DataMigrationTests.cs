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
 * THE WHITELIST IS THE SAFETY PROPERTY, so most of these check what does NOT come across. The old data
 * directory holds whatever else its owner put there, and anything imported lands in a directory this
 * launcher will later write to.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class DataMigrationTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-migrate-" + Guid.NewGuid().ToString("N"));

    private readonly string _source;
    private readonly string _destination;

    public DataMigrationTests()
    {
        _source = Path.Combine(_temp, "old");
        _destination = Path.Combine(_temp, "new");

        Directory.CreateDirectory(_source);
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

    private void Make(params string[] relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(_source, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }

    private async Task<List<string>> MigrateAsync(string oldConfigFile = "multimc.cfg")
    {
        // Cleared first, so a test that migrates twice sees each run's result rather than the union.
        if (Directory.Exists(_destination))
        {
            Directory.Delete(_destination, recursive: true);
        }

        var task = new DataMigrationTask(_source, _destination, DataMigration.CreateMatcher(oldConfigFile));

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        return !Directory.Exists(_destination)
            ? []
            : [.. Directory.EnumerateFiles(_destination, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_destination, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)];
    }

    // ================================================================== what comes across

    [Fact]
    public async Task TheKnownDirectoriesAreBroughtAcross()
    {
        Make(
            "instances/MyPack/instance.cfg",
            "libraries/net/x/a.jar",
            "assets/indexes/5.json",
            "icons/custom.png",
            "themes/dark/theme.json",
            "mods/shared.jar",
            "logs/launcher-0.log",
            "accounts.json",
            "accounts/skins/x.png");

        var copied = await MigrateAsync().ConfigureAwait(true);

        Assert.Equal(9, copied.Count);
        Assert.Contains("instances/MyPack/instance.cfg", copied);
        Assert.Contains("accounts.json", copied);
    }

    /// <summary>Named separately because it differs per fork, and its settings still apply.</summary>
    [Fact]
    public async Task TheOtherLaunchersConfigFileComesAcross()
    {
        Make("multimc.cfg", "polymc.cfg");

        Assert.Equal(["multimc.cfg"], await MigrateAsync("multimc.cfg").ConfigureAwait(true));
        Assert.Equal(["polymc.cfg"], await MigrateAsync("polymc.cfg").ConfigureAwait(true));
    }

    /// <summary>Upstream's own comment: "it is possible that we already used that directory before".</summary>
    [Fact]
    public async Task ThisLaunchersOwnConfigComesAcrossToo()
    {
        Make(BuildConfig.Instance.LauncherConfigFile);

        Assert.Contains(BuildConfig.Instance.LauncherConfigFile, await MigrateAsync().ConfigureAwait(true));
    }

    /*
     * ANYTHING UNRECOGNISED IS LEFT BEHIND. The old directory holds whatever else its owner put there,
     * and an imported file lands somewhere this launcher will later write to.
     */
    [Fact]
    public async Task UnknownFilesAreLeftBehind()
    {
        Make(
            "instances/Keep/instance.cfg",
            "notes.txt",
            "screenshots/x.png",
            "some-other-launcher/data.db",
            "java/jdk17/bin/java.exe");

        Assert.Equal(["instances/Keep/instance.cfg"], await MigrateAsync().ConfigureAwait(true));
    }

    /// <summary>Prefix matches take the whole tree under them.</summary>
    [Fact]
    public async Task AWhitelistedPrefixTakesEverythingBeneathIt()
    {
        Make("instances/A/x.cfg", "instances/B/deep/y.json", "instances/loose.txt");

        Assert.Equal(3, (await MigrateAsync().ConfigureAwait(true)).Count);
    }

    /*
     * A prefix match, not a path-segment match: "instancesomething" starts with "instances" only if
     * the prefix has no trailing slash, and upstream's entries do have one. Worth pinning, since a
     * directory named to look like a whitelisted one would otherwise be imported.
     */
    [Fact]
    public async Task ADirectoryMerelyNamedLikeAWhitelistedOneIsNotImported()
    {
        Make("instances/real.cfg", "instances-backup/old.cfg");

        Assert.Equal(["instances/real.cfg"], await MigrateAsync().ConfigureAwait(true));
    }

    [Fact]
    public async Task AnEmptyOldDirectoryMigratesNothingAndSucceeds()
        => Assert.Empty(await MigrateAsync().ConfigureAwait(true));

    // ================================================================== display

    /*
     * Upstream's numbers: over 50 characters becomes the first 20, an ellipsis and the last 29 --
     * exactly 50. The tail gets more than the head deliberately, because the interesting part of a
     * path being copied is its filename.
     */
    [Fact]
    public void ALongPathIsShortenedFromTheMiddle()
    {
        var path = "instances/" + new string('a', 60) + "/config/thing.json";

        var shortened = DataMigration.ShortenForDisplay(path);

        Assert.Equal(50, shortened.Length);
        Assert.StartsWith(path[..20], shortened, StringComparison.Ordinal);
        Assert.EndsWith(path[^29..], shortened, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short.txt")]
    [InlineData("instances/MyPack/config/some/reasonably/long/path.json")]
    public void APathIsShortenedOnlyWhenItIsTooLong(string path)
    {
        var shortened = DataMigration.ShortenForDisplay(path);

        if (path.Length <= 50)
        {
            Assert.Equal(path, shortened);
        }
        else
        {
            Assert.Equal(50, shortened.Length);
        }
    }

    [Fact]
    public void APathOfExactlyFiftyIsLeftAlone()
    {
        var path = new string('a', 50);

        Assert.Equal(path, DataMigration.ShortenForDisplay(path));
    }
}
