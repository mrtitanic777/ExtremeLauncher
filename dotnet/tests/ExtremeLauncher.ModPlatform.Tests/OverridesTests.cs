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
 * Real directories, real files. The record exists to drive a DELETE on the next update, so a test
 * asserting an in-memory list would be checking the least consequential half.
 */

using System.Text;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class OverridesTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-ovr-" + Guid.NewGuid().ToString("N"));

    public OverridesTests() => Directory.CreateDirectory(_temp);

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

    private string MakeOverrides(string folderName, params string[] relativePaths)
    {
        var root = Path.Combine(_temp, folderName);

        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(root, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "content");
        }

        Directory.CreateDirectory(root);

        return root;
    }

    // ================================================================== writing and reading

    [Fact]
    public void EveryFileInTheFolderIsRecordedRelatively()
    {
        var overrides = MakeOverrides("overrides", "options.txt", "config/mod.cfg", "config/deep/x.json");

        Overrides.Write("overrides", _temp, overrides);

        Assert.Equal(
            ["config/deep/x.json", "config/mod.cfg", "options.txt"],
            Overrides.Read("overrides", _temp));
    }

    /// <summary>Forward slashes, so a record written on Windows reads correctly elsewhere.</summary>
    [Fact]
    public void RecordedPathsUseForwardSlashes()
    {
        Overrides.Write("overrides", _temp, MakeOverrides("overrides", "config/deep/x.json"));

        var text = File.ReadAllText(Path.Combine(_temp, "overrides.txt"));

        Assert.DoesNotContain('\\', text);
        Assert.Contains("config/deep/x.json", text, StringComparison.Ordinal);
    }

    /// <summary>Directories are not recorded — only the files inside them.</summary>
    [Fact]
    public void OnlyFilesAreRecorded()
    {
        var overrides = MakeOverrides("overrides", "config/mod.cfg");
        Directory.CreateDirectory(Path.Combine(overrides, "empty-folder"));

        Overrides.Write("overrides", _temp, overrides);

        Assert.Equal(["config/mod.cfg"], Overrides.Read("overrides", _temp));
    }

    /// <summary>A pack with no overrides still gets a record, so "never imported" stays distinguishable.</summary>
    [Fact]
    public void APackWithNoOverridesStillGetsAnEmptyRecord()
    {
        Overrides.Write("overrides", _temp, Path.Combine(_temp, "does-not-exist"));

        Assert.True(File.Exists(Path.Combine(_temp, "overrides.txt")));
        Assert.Empty(Overrides.Read("overrides", _temp));
    }

    /// <summary>No record at all is the normal state for an instance imported before records existed.</summary>
    [Fact]
    public void NoRecordReadsAsEmptyRatherThanFailing()
        => Assert.Empty(Overrides.Read("overrides", _temp));

    /// <summary>Each overrides folder gets its own record; a pack may ship several.</summary>
    [Fact]
    public void SeveralOverrideFoldersAreRecordedSeparately()
    {
        Overrides.Write("overrides", _temp, MakeOverrides("overrides", "a.txt"));
        Overrides.Write("client-overrides", _temp, MakeOverrides("client-overrides", "b.txt"));

        Assert.Equal(["a.txt"], Overrides.Read("overrides", _temp));
        Assert.Equal(["b.txt"], Overrides.Read("client-overrides", _temp));
    }

    /*
     * UPSTREAM DERIVES THE RELATIVE PATH BY SPLITTING THE ABSOLUTE PATH ON THE FOLDER'S NAME, then
     * dropping one leading character. That works until the name appears elsewhere in the path -- a
     * staging directory under a pack called "overrides", or any user folder containing the word --
     * and every recorded path is truncated at the wrong point. The record then names files that do
     * not exist, so the next update deletes nothing and the stale overrides stay forever.
     */
    [Fact]
    public void AFolderNameAppearingTwiceInThePathDoesNotCorruptTheRecord()
    {
        // The word appears in an ancestor directory as well as being the folder's own name.
        var nested = Path.Combine(_temp, "overrides", "staging");
        Directory.CreateDirectory(nested);

        var overrides = Path.Combine(nested, "overrides");
        Directory.CreateDirectory(Path.Combine(overrides, "config"));
        File.WriteAllText(Path.Combine(overrides, "config", "mod.cfg"), "x");

        Overrides.Write("overrides", _temp, overrides);

        Assert.Equal(["config/mod.cfg"], Overrides.Read("overrides", _temp));
    }

    /*
     * Upstream's read loop appends before testing for the end, so the list it returns always ends
     * with an empty string -- and every caller opens with a check for one. That check is the tell.
     */
    [Fact]
    public void BlankLinesAreDropped()
    {
        File.WriteAllText(
            Path.Combine(_temp, "overrides.txt"),
            "config/mod.cfg\n\n   \noptions.txt\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Equal(["config/mod.cfg", "options.txt"], Overrides.Read("overrides", _temp));
    }

    // ================================================================== deleting

    [Fact]
    public void StalePathsResolveAgainstTheGameDirectory()
    {
        var gameRoot = Path.Combine(_temp, "minecraft");
        Directory.CreateDirectory(gameRoot);

        Overrides.Write("overrides", _temp, MakeOverrides("overrides", "config/mod.cfg"));

        Assert.Equal(
            [Path.GetFullPath(Path.Combine(gameRoot, "config", "mod.cfg"))],
            Overrides.GetStalePaths("overrides", _temp, gameRoot));
    }

    /*
     * The record is written by this launcher, but it sits in the instance folder where anything could
     * have edited it -- and the result of this call is a DELETE list, which is where a bad path does
     * the most damage.
     */
    [Theory]
    [InlineData("../../outside.txt")]
    [InlineData("../sibling/x.txt")]
    [InlineData("/etc/passwd")]
    public void ARecordedPathOutsideTheInstanceIsNotReturnedForDeletion(string entry)
    {
        var gameRoot = Path.Combine(_temp, "minecraft");
        Directory.CreateDirectory(gameRoot);

        File.WriteAllText(Path.Combine(_temp, "overrides.txt"), entry + "\nconfig/mod.cfg\n");

        var stale = Overrides.GetStalePaths("overrides", _temp, gameRoot);

        // The legitimate entry survives; the escaping one does not.
        Assert.Equal([Path.GetFullPath(Path.Combine(gameRoot, "config", "mod.cfg"))], stale);
    }

    [Fact]
    public void NoRecordMeansNothingToDelete()
        => Assert.Empty(Overrides.GetStalePaths("overrides", _temp, Path.Combine(_temp, "minecraft")));
}
