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
 * The copy path runs for real. The link path is skippable, because symlinks need a privilege on
 * Windows that an ordinary developer machine does not have — and a suite that fails there gets ignored
 * rather than fixed.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstanceCopyTaskTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-inst-" + Guid.NewGuid().ToString("N"));

    private readonly string _source;
    private readonly string _staging;

    public InstanceCopyTaskTests()
    {
        _source = Path.Combine(_temp, "orig");
        _staging = Path.Combine(_temp, "staging");

        Make("instance.cfg", "minecraft/options.txt", "minecraft/mods/a.jar", "minecraft/saves/World/level.dat");
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
            File.WriteAllText(full, "content of " + relative);
        }
    }

    private List<string> Staged()
        => !Directory.Exists(_staging)
            ? []
            : [.. Directory.EnumerateFiles(_staging, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_staging, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)];

    // ================================================================== choosing

    /// <summary>Clone wins where offered: the same near-zero cost without sharing edits.</summary>
    [Theory]
    [InlineData(false, false, false, InstanceCopyStrategy.Copy)]
    [InlineData(true, false, false, InstanceCopyStrategy.Clone)]
    [InlineData(false, true, false, InstanceCopyStrategy.Link)]
    [InlineData(false, false, true, InstanceCopyStrategy.Link)]
    [InlineData(true, true, false, InstanceCopyStrategy.Clone)]
    public void TheStrategyFollowsThePreferences(bool clone, bool symlinks, bool hardLinks, InstanceCopyStrategy expected)
        => Assert.Equal(
            expected,
            InstanceCopyTask.ChooseStrategy(new InstanceCopyPrefs
            {
                UseClone = clone,
                UseSymLinks = symlinks,
                UseHardLinks = hardLinks,
            }));

    /*
     * An instance holds its game files in ".minecraft" or "minecraft" depending on its age. The DOTTED
     * name is preferred only when the plain one is absent, so an instance containing both -- which
     * happens after a botched migration -- keeps using the visible one.
     */
    [Fact]
    public void TheGameRootPrefersTheVisibleNameWhenBothExist()
    {
        var both = Path.Combine(_temp, "both");
        Directory.CreateDirectory(Path.Combine(both, "minecraft"));
        Directory.CreateDirectory(Path.Combine(both, ".minecraft"));

        Assert.EndsWith("minecraft", InstanceCopyTask.StagingGameRoot(both), StringComparison.Ordinal);
        Assert.DoesNotContain(".minecraft", InstanceCopyTask.StagingGameRoot(both), StringComparison.Ordinal);

        var dottedOnly = Path.Combine(_temp, "dotted");
        Directory.CreateDirectory(Path.Combine(dottedOnly, ".minecraft"));

        Assert.Contains(".minecraft", InstanceCopyTask.StagingGameRoot(dottedOnly), StringComparison.Ordinal);
    }

    // ================================================================== copying

    [Fact]
    public async Task ACopyBringsEverythingAndRenamesTheInstance()
    {
        var task = new InstanceCopyTask(_source, _staging, new InstanceCopyPrefs(), "My Copy", "flame");

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        Assert.Contains("minecraft/mods/a.jar", Staged());
        Assert.Contains("minecraft/saves/World/level.dat", Staged());

        var cfg = File.ReadAllText(Path.Combine(_staging, "instance.cfg"));

        Assert.Contains("name=My Copy", cfg, StringComparison.Ordinal);
        Assert.Contains("iconKey=flame", cfg, StringComparison.Ordinal);
    }

    /// <summary>An unticked box excludes the folder it names.</summary>
    [Fact]
    public async Task UntickedBoxesAreLeftBehind()
    {
        var prefs = new InstanceCopyPrefs { CopySaves = false, CopyMods = false };
        var task = new InstanceCopyTask(_source, _staging, prefs, "My Copy");

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        var staged = Staged();

        Assert.Contains("minecraft/options.txt", staged);
        Assert.DoesNotContain("minecraft/saves/World/level.dat", staged);
        Assert.DoesNotContain("minecraft/mods/a.jar", staged);
    }

    /*
     * UPSTREAM BUG #16 IN EFFECT. The copy filter is built case-insensitively, so a folder the user
     * spelled "Saves" is still excluded when they untick saves -- which is what they asked for, and
     * what upstream's inverted caseSensitive() prevented.
     */
    [Fact]
    public async Task AFolderWithUnexpectedCasingIsStillExcluded()
    {
        var oddly = Path.Combine(_temp, "odd");
        Directory.CreateDirectory(Path.Combine(oddly, "minecraft", "Saves", "World"));
        File.WriteAllText(Path.Combine(oddly, "minecraft", "Saves", "World", "level.dat"), "x");
        File.WriteAllText(Path.Combine(oddly, "instance.cfg"), "name=Orig\n");

        var task = new InstanceCopyTask(
            oddly, _staging, new InstanceCopyPrefs { CopySaves = false }, "My Copy");

        Assert.True(await task.RunAsync().ConfigureAwait(true));
        Assert.DoesNotContain("minecraft/Saves/World/level.dat", Staged());
    }

    /// <summary>A duplicated instance has not been played; keeping the hours is a choice.</summary>
    [Fact]
    public async Task PlaytimeIsResetUnlessKept()
    {
        File.WriteAllText(
            Path.Combine(_source, "instance.cfg"),
            "name=Orig\ntotalTimePlayed=9999\nlastTimePlayed=1234\n");

        var task = new InstanceCopyTask(
            _source, _staging, new InstanceCopyPrefs { KeepPlaytime = false }, "My Copy");

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        var cfg = File.ReadAllText(Path.Combine(_staging, "instance.cfg"));

        Assert.Contains("totalTimePlayed=0", cfg, StringComparison.Ordinal);
        Assert.DoesNotContain("9999", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaytimeIsKeptWhenAsked()
    {
        File.WriteAllText(Path.Combine(_source, "instance.cfg"), "name=Orig\ntotalTimePlayed=9999\n");

        var task = new InstanceCopyTask(
            _source, _staging, new InstanceCopyPrefs { KeepPlaytime = true }, "My Copy");

        Assert.True(await task.RunAsync().ConfigureAwait(true));
        Assert.Contains("9999", File.ReadAllText(Path.Combine(_staging, "instance.cfg")), StringComparison.Ordinal);
    }

    // ================================================================== linking

    /*
     * SAVES ARE COPIED EVEN WHEN EVERYTHING ELSE IS LINKED. Sharing a world between two instances is
     * not a saving -- it is two copies of Minecraft writing to one region file.
     */
    [SkippableFact]
    public async Task LinkingStillCopiesTheSaves()
    {
        var prefs = new InstanceCopyPrefs { UseHardLinks = true, LinkRecursively = true, CopySaves = true };
        var task = new InstanceCopyTask(_source, _staging, prefs, "My Copy");

        Skip.IfNot(await task.RunAsync().ConfigureAwait(true), "Links unavailable here: " + task.FailReason);

        var save = Path.Combine(_staging, "minecraft", "saves", "World", "level.dat");

        Assert.True(File.Exists(save));

        // A real copy: writing to the original's world must not change the duplicate's.
        File.WriteAllText(Path.Combine(_source, "minecraft", "saves", "World", "level.dat"), "changed");
        Assert.NotEqual("changed", File.ReadAllText(save));
    }

    /*
     * The launcher refuses to follow a symlink out of an instance unless the target is listed, so
     * without this a linked instance starts and then cannot read its own mods.
     */
    [SkippableFact]
    public async Task LinkingRecordsTheOriginalAsAnAllowedSymlink()
    {
        var prefs = new InstanceCopyPrefs { UseHardLinks = true, LinkRecursively = true };
        var task = new InstanceCopyTask(_source, _staging, prefs, "My Copy");

        Skip.IfNot(await task.RunAsync().ConfigureAwait(true), "Links unavailable here: " + task.FailReason);

        var allowed = File.ReadAllText(
            Path.Combine(InstanceCopyTask.StagingGameRoot(_staging), "allowed_symlinks.txt"));

        Assert.Contains("minecraft", allowed, StringComparison.Ordinal);
        Assert.EndsWith("\n", allowed, StringComparison.Ordinal);
    }

    /// <summary>A plain copy is self-contained, so it needs no permission to leave itself.</summary>
    [Fact]
    public async Task ACopyRecordsNoAllowedSymlinks()
    {
        var task = new InstanceCopyTask(_source, _staging, new InstanceCopyPrefs(), "My Copy");

        Assert.True(await task.RunAsync().ConfigureAwait(true));

        Assert.False(File.Exists(
            Path.Combine(InstanceCopyTask.StagingGameRoot(_staging), "allowed_symlinks.txt")));
    }
}
