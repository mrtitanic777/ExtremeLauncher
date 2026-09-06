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
 * Which icons exist, and what happens to the files.
 *
 * ASSERTED ON THE FOLDER wherever the question is "did that get copied". The icons folder is shared
 * with an upstream install and is a compatibility surface in its own right.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class IconListTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "el-icons-" + Guid.NewGuid().ToString("N"));

    private readonly string _icons;

    public IconListTests()
    {
        _icons = Path.Combine(_root, "icons");

        Directory.CreateDirectory(_icons);
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

    private static readonly string[] BuiltIn = ["default", "creeper_legacy", "steve_legacy"];

    private string WriteIcon(string name, string folder = "")
    {
        var path = Path.Combine(folder.Length == 0 ? _icons : folder, name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A one-pixel PNG. Only the extension matters to this layer, but a real header keeps the
        // fixture honest for anything that later tries to decode it.
        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        return path;
    }

    [Fact]
    public void TheBuiltInIconsAreListedWithNoFolderAtAll()
    {
        // Every first run: the icons folder is created on demand, not up front.
        Directory.Delete(_icons, recursive: true);

        var list = new IconList(_icons, BuiltIn);

        Assert.Equal(3, list.All().Count);
        Assert.All(list.All(), e => Assert.Equal(IconSource.BuiltIn, e.Source));
    }

    [Fact]
    public void AFileInTheFolderShowsUpAsAUserIcon()
    {
        WriteIcon("mypack.png");

        var list = new IconList(_icons, BuiltIn);

        var mine = list.All().Single(e => e.Key == "mypack");

        Assert.Equal(IconSource.User, mine.Source);
        Assert.True(mine.IsRenderable);
    }

    [Fact]
    public void AUserIconWinsOverABuiltInOfTheSameName()
    {
        /*
         * Upstream's rule, and the only way to replace a built-in you dislike: drop a file with the
         * same name into the folder. Listing both would show the same key twice, and whichever the UI
         * picked would be arbitrary.
         */
        WriteIcon("creeper_legacy.png");

        var list = new IconList(_icons, BuiltIn);

        var entries = list.All().Where(e => e.Key == "creeper_legacy").ToArray();

        Assert.Single(entries);
        Assert.Equal(IconSource.User, entries[0].Source);
    }

    [Fact]
    public void SomethingThatIsNotAnImageIsIgnored()
    {
        File.WriteAllText(Path.Combine(_icons, "notes.txt"), "not an icon");

        var list = new IconList(_icons, BuiltIn);

        Assert.DoesNotContain(list.All(), e => e.Key == "notes");
    }

    [Fact]
    public void AnSvgIsListedButMarkedUnrenderable()
    {
        /*
         * Upstream accepts SVG and this build cannot draw one without another dependency. Listing it
         * anyway is deliberate: an icons folder shared with an upstream install has them, and hiding
         * them would make the launcher disagree with the folder it is showing.
         */
        WriteIcon("vector.svg");

        var list = new IconList(_icons, BuiltIn);

        var entry = list.All().Single(e => e.Key == "vector");

        Assert.Equal(IconSource.User, entry.Source);
        Assert.False(entry.IsRenderable);
    }

    [Fact]
    public void AnUnknownKeyResolvesToTheDefaultRatherThanToNothing()
    {
        // An instance whose icon file was deleted must still draw something. No tile at all reads as
        // a broken instance rather than a missing picture.
        var list = new IconList(_icons, BuiltIn);

        var resolved = list.Resolve("this-was-never-here");

        Assert.Equal("default", resolved.Key);
    }

    [Fact]
    public void AKnownKeyResolvesToItself()
    {
        var list = new IconList(_icons, BuiltIn);

        Assert.Equal("steve_legacy", list.Resolve("steve_legacy").Key);
    }

    [Fact]
    public void ImportingCopiesTheFileIntoTheIconsFolder()
    {
        var outside = WriteIcon("brought-along.png", Path.Combine(_root, "elsewhere"));

        var list = new IconList(_icons, BuiltIn);

        var key = list.Import(outside);

        Assert.Equal("brought-along", key);

        // The FILE, not the listing: the icon has to survive the source folder going away.
        Assert.True(File.Exists(Path.Combine(_icons, "brought-along.png")));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void ImportingTheSameNameTwiceKeepsBoth()
    {
        /*
         * Importing "creeper.png" from two different folders is a thing people do. Overwriting the
         * first is how somebody loses an icon they were using -- and every instance keyed to it would
         * change picture at once, with nothing on screen explaining why.
         */
        var first = WriteIcon("creeper.png", Path.Combine(_root, "one"));
        var second = WriteIcon("creeper.png", Path.Combine(_root, "two"));

        var list = new IconList(_icons, BuiltIn);

        Assert.Equal("creeper", list.Import(first));
        Assert.Equal("creeper-2", list.Import(second));

        Assert.Equal(2, Directory.GetFiles(_icons).Length);
    }

    [Fact]
    public void ImportingSomethingThatIsNotAnIconIsRefused()
    {
        var path = Path.Combine(_root, "readme.txt");

        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "hello");

        var list = new IconList(_icons, BuiltIn);

        Assert.Equal(string.Empty, list.Import(path));
        Assert.Empty(Directory.GetFiles(_icons));
    }

    [Fact]
    public void ImportingAMissingFileIsRefusedRatherThanThrowing()
    {
        var list = new IconList(_icons, BuiltIn);

        Assert.Equal(string.Empty, list.Import(Path.Combine(_root, "nope.png")));
    }

    [Fact]
    public void RemovingAUserIconTakesTheFileAway()
    {
        WriteIcon("mypack.png");

        var list = new IconList(_icons, BuiltIn);

        Assert.True(list.Remove("mypack"));
        Assert.DoesNotContain(list.All(), e => e.Key == "mypack");
        Assert.False(File.Exists(Path.Combine(_icons, "mypack.png")));
    }

    [Fact]
    public void ABuiltInIconCannotBeRemoved()
    {
        // There is no file to remove, and the key would come straight back on the next listing --
        // which would look exactly like the delete having silently failed.
        var list = new IconList(_icons, BuiltIn);

        Assert.False(list.Remove("steve_legacy"));
        Assert.Contains(list.All(), e => e.Key == "steve_legacy");
    }

    [Fact]
    public void TheDisplayNameIsReadableRatherThanTheRawKey()
    {
        WriteIcon("my_favourite_pack.png");

        var list = new IconList(_icons, BuiltIn);

        Assert.Equal("My favourite pack", list.All().Single(e => e.Key == "my_favourite_pack").DisplayName);
    }

    [Fact]
    public void FindBestIconInMatchesAKeyContainingADot()
    {
        /*
         * Upstream matches the complete base name OR the whole file name, which is not redundant:
         * a key of "my.icon" must find "my.icon.png" by the first rule, and a file literally named
         * "my.icon" by the second.
         */
        WriteIcon("my.icon.png");

        Assert.EndsWith("my.icon.png", IconUtils.FindBestIconIn(_icons, "my.icon"), StringComparison.Ordinal);
    }

    [Fact]
    public void FindBestIconInReturnsNothingForAFolderThatDoesNotExist()
    {
        Assert.Equal(string.Empty, IconUtils.FindBestIconIn(Path.Combine(_root, "gone"), "anything"));
    }
}
