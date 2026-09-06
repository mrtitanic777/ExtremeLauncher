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
 * THIS FILE BELONGS TO MINECRAFT, NOT THE LAUNCHER. It holds every keybind and video setting a player
 * has ever changed, and the game rewrites it on exit. So these tests are mostly about what survives a
 * round trip untouched — unknown keys, duplicates, ordering, odd values — rather than about what the
 * launcher can read out of it.
 */

using System.Text;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class GameOptionsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-opts-" + Guid.NewGuid().ToString("N"));

    private readonly string _path;

    public GameOptionsTests()
    {
        Directory.CreateDirectory(_temp);
        _path = Path.Combine(_temp, "options.txt");
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

    private GameOptions Write(string contents)
    {
        File.WriteAllText(_path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return GameOptions.Load(_path);
    }

    // ================================================================== reading

    [Fact]
    public void OptionsAreReadAsKeyValuePairs()
    {
        var options = Write("version:3465\nfov:0.5\nfullscreen:true\n");

        Assert.True(options.IsLoaded);
        Assert.Equal(3465, options.Version);
        Assert.Equal("0.5", options.Get("fov"));
        Assert.Equal("true", options.Get("fullscreen"));
    }

    /// <summary>Held apart from the rest, and written back first — where Minecraft puts it.</summary>
    [Fact]
    public void TheVersionIsNotAnOrdinaryEntry()
    {
        var options = Write("version:3465\nfov:0.5\n");

        Assert.Equal("fov", Assert.Single(options.Contents).Key);
        Assert.Null(options.Get("version"));
    }

    /// <summary>An old file without one should not gain a version from being opened.</summary>
    [Fact]
    public void AFileWithNoVersionKeepsNone()
    {
        var options = Write("fov:0.5\n");

        Assert.Equal(0, options.Version);

        options.Save();

        Assert.DoesNotContain("version", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    /*
     * The FIRST colon separates. Keybinds are written as "key.keyboard.left.control" and resource pack
     * lists as JSON arrays, so splitting on every colon would truncate them.
     */
    [Theory]
    [InlineData("key_key.attack:key.mouse.left", "key_key.attack", "key.mouse.left")]
    [InlineData("resourcePacks:[\"vanilla\",\"file/x:y\"]", "resourcePacks", "[\"vanilla\",\"file/x:y\"]")]
    [InlineData("empty:", "empty", "")]
    public void OnlyTheFirstColonSeparates(string line, string key, string value)
    {
        var options = Write(line + "\n");

        Assert.Equal(key, Assert.Single(options.Contents).Key);
        Assert.Equal(value, options.Get(key));
    }

    /// <summary>There is no such thing as an options.txt worth refusing to open.</summary>
    [Fact]
    public void ALineWithNoColonIsSkippedRatherThanFatal()
    {
        var options = Write("fov:0.5\nnonsense\n\nfullscreen:true\n");

        Assert.True(options.IsLoaded);
        Assert.Equal(2, options.Contents.Count);
    }

    [Fact]
    public void AMissingFileIsNotLoadedRatherThanFatal()
    {
        var options = GameOptions.Load(Path.Combine(_temp, "absent.txt"));

        Assert.False(options.IsLoaded);
        Assert.Empty(options.Contents);
    }

    /*
     * Upstream chops only '\n', so a file saved with CRLF -- by a user editing in Notepad -- leaves
     * '\r' on the end of every value. Nothing then matches "true", and the stray byte is written
     * straight back on save.
     */
    [Fact]
    public void CarriageReturnsAreStrippedFromValues()
    {
        var options = Write("version:3465\r\nfullscreen:true\r\nfov:0.5\r\n");

        Assert.Equal(3465, options.Version);
        Assert.Equal("true", options.Get("fullscreen"));
        Assert.Equal("0.5", options.Get("fov"));
    }

    // ================================================================== preserving

    /*
     * ORDER IS PRESERVED. Rewriting the file sorted would work perfectly and produce a diff against
     * every backup the user has.
     */
    [Fact]
    public void OrderSurvivesARoundTrip()
    {
        var original = "version:3465\nzzz:1\naaa:2\nmmm:3\n";

        Write(original).Save();

        Assert.Equal(original, File.ReadAllText(_path));
    }

    /// <summary>Most keys in a real options.txt are ones this launcher has never heard of.</summary>
    [Fact]
    public void UnknownKeysSurviveUntouched()
    {
        var original = "someFutureSetting:42\nanotherOne:[1,2,3]\n";

        Write(original).Save();

        Assert.Equal(original, File.ReadAllText(_path));
    }

    /*
     * The format permits duplicates and Minecraft reads the last; collapsing them would silently
     * change which one wins.
     */
    [Fact]
    public void DuplicateKeysSurvive()
    {
        var options = Write("fov:0.5\nfov:0.9\n");

        Assert.Equal(2, options.Contents.Count);

        options.Save();

        Assert.Equal("fov:0.5\nfov:0.9\n", File.ReadAllText(_path));
    }

    /// <summary>LF on every platform, which is what Minecraft itself writes.</summary>
    [Fact]
    public void TheFileIsWrittenWithUnixLineEndings()
    {
        Write("fov:0.5\r\nfullscreen:true\r\n").Save();

        var written = File.ReadAllText(_path);

        Assert.DoesNotContain('\r', written);
        Assert.Equal("fov:0.5\nfullscreen:true\n", written);
    }

    // ================================================================== editing

    [Fact]
    public void SettingAnExistingKeyChangesItInPlace()
    {
        var options = Write("version:3465\nfov:0.5\nfullscreen:false\n");

        options.Set("fullscreen", "true");
        options.Save();

        Assert.Equal("version:3465\nfov:0.5\nfullscreen:true\n", File.ReadAllText(_path));
    }

    [Fact]
    public void SettingANewKeyAppendsIt()
    {
        var options = Write("fov:0.5\n");

        options.Set("newSetting", "1");
        options.Save();

        Assert.Equal("fov:0.5\nnewSetting:1\n", File.ReadAllText(_path));
    }

    /// <summary>Adding a third copy would be worse than editing the first.</summary>
    [Fact]
    public void SettingADuplicatedKeyEditsTheFirstRatherThanAddingAnother()
    {
        var options = Write("fov:0.5\nfov:0.9\n");

        options.Set("fov", "0.7");
        options.Save();

        Assert.Equal("fov:0.7\nfov:0.9\n", File.ReadAllText(_path));
    }

    [Fact]
    public void ReloadingDiscardsUnsavedEdits()
    {
        var options = Write("fov:0.5\n");

        options.Set("fov", "0.9");
        options.Reload();

        Assert.Equal("0.5", options.Get("fov"));
    }

    /// <summary>Values with non-ASCII survive: a keybind may carry any character.</summary>
    [Fact]
    public void NonAsciiValuesRoundTrip()
    {
        var original = "lang:fr_fr\nlastServer:Café Serveur ✦\n";

        Write(original).Save();

        Assert.Equal(original, File.ReadAllText(_path));
    }
}
