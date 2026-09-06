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
 * THE SUFFIX IS THE STATE. A mod is turned off by renaming it to ".disabled", which is how this whole
 * launcher lineage does it and how the game sees it too: the loader simply does not recognise the
 * extension. So every test here is really about a rename, and the ways a rename can lose a file.
 */

using ExtremeLauncher.Minecraft.Mods;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class ResourceEnableTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-res-" + Guid.NewGuid().ToString("N"));

    public ResourceEnableTests() => Directory.CreateDirectory(_temp);

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

    private string Make(string name, string content = "jar")
    {
        var path = Path.Combine(_temp, name);

        File.WriteAllText(path, content);

        return path;
    }

    // ================================================================== turning it off and on

    [Fact]
    public void DisablingRenamesTheFileAndUpdatesTheResource()
    {
        var resource = new Resource(Make("sodium.jar"));

        Assert.True(resource.Enabled);
        Assert.True(resource.SetEnabled(Resource.EnableAction.Disable));

        Assert.False(resource.Enabled);
        Assert.EndsWith(".disabled", resource.Path, StringComparison.Ordinal);
        Assert.True(File.Exists(resource.Path));
        Assert.False(File.Exists(Path.Combine(_temp, "sodium.jar")));

        // The display name loses the suffix, so the list does not show "sodium.jar.disabled".
        Assert.Equal("sodium", resource.Name);
    }

    [Fact]
    public void EnablingPutsTheNameBack()
    {
        var resource = new Resource(Make("sodium.jar.disabled"));

        Assert.False(resource.Enabled);
        Assert.True(resource.SetEnabled(Resource.EnableAction.Enable));

        Assert.True(resource.Enabled);
        Assert.Equal(Path.Combine(_temp, "sodium.jar"), Path.GetFullPath(resource.Path));
        Assert.Equal("sodium", resource.Name);
    }

    [Fact]
    public void TogglingGoesBothWays()
    {
        var resource = new Resource(Make("sodium.jar"));

        Assert.True(resource.SetEnabled(Resource.EnableAction.Toggle));
        Assert.False(resource.Enabled);

        Assert.True(resource.SetEnabled(Resource.EnableAction.Toggle));
        Assert.True(resource.Enabled);

        Assert.Equal("jar", File.ReadAllText(resource.Path));
    }

    /// <summary>Asking for the state it is already in changes nothing and says so.</summary>
    [Fact]
    public void AskingForTheCurrentStateIsANoOp()
    {
        var resource = new Resource(Make("sodium.jar"));

        Assert.False(resource.SetEnabled(Resource.EnableAction.Enable));
        Assert.True(File.Exists(Path.Combine(_temp, "sodium.jar")));
    }

    // ================================================================== refusing

    /*
     * A FOLDER HAS NO SUFFIX CONVENTION. Renaming one would hide it from the game with nothing on
     * screen to explain why, so upstream refuses and so does this.
     */
    [Fact]
    public void AFolderCannotBeDisabled()
    {
        var path = Path.Combine(_temp, "somefolder");
        Directory.CreateDirectory(path);

        var resource = new Resource(path);

        Assert.False(resource.SetEnabled(Resource.EnableAction.Disable));
        Assert.True(Directory.Exists(path));
    }

    /*
     * NOT TESTED, deliberately, and worth saying why: SetEnabled also refuses to enable a resource that
     * is disabled but has no ".disabled" suffix. That state cannot be reached through the constructor,
     * because Enabled is DERIVED from the suffix -- so a test for it could only manufacture the state
     * by reflection, and would pin the guard rather than any behaviour a caller can produce.
     *
     * The guard still earns its place in the source: chopping nine characters off a name that does not
     * end in ".disabled" would destroy part of a real filename.
     */

    // ================================================================== collisions

    /*
     * UPSTREAM'S ".duplicate" QUIRK, kept. Disabling "sodium.jar" when "sodium.jar.disabled" already
     * exists cannot use that name, and upstream's getUniqueResourceName appends ".duplicate" -- NOT
     * another ".disabled". The result is "sodium.jar.duplicate", which the loader also ignores, but
     * for the different reason that it is not a jar.
     *
     * What matters most is the negative: NEITHER FILE IS LOST. A rename that overwrote the existing
     * one would silently destroy a mod the user still had.
     */
    [Fact]
    public void DisablingOntoAnExistingDisabledFileKeepsBoth()
    {
        Make("sodium.jar.disabled", "the old one");

        var resource = new Resource(Make("sodium.jar", "the new one"));

        Assert.True(resource.SetEnabled(Resource.EnableAction.Disable));

        Assert.Equal("the old one", File.ReadAllText(Path.Combine(_temp, "sodium.jar.disabled")));
        Assert.Equal("the new one", File.ReadAllText(resource.Path));
        Assert.EndsWith(".duplicate", resource.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondCollisionCountsUpwards()
    {
        Make("sodium.jar.disabled", "one");
        Make("sodium.jar.duplicate", "two");

        var resource = new Resource(Make("sodium.jar", "three"));

        Assert.True(resource.SetEnabled(Resource.EnableAction.Disable));

        Assert.Equal("three", File.ReadAllText(resource.Path));
        Assert.EndsWith(".duplicate2", resource.Path, StringComparison.Ordinal);

        // And the two that were already there are untouched.
        Assert.Equal("one", File.ReadAllText(Path.Combine(_temp, "sodium.jar.disabled")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(_temp, "sodium.jar.duplicate")));
    }

    // ================================================================== removing

    /*
     * Trashed where the platform can, and PERMANENTLY DELETED WHERE IT CANNOT, without asking -- which
     * is upstream's `(attemptTrash && trash()) || deletePath()`. Defensible for a mod, which is a
     * download rather than a world, and a real difference from how this port removes instances.
     */
    [Fact]
    public void DestroyingRemovesTheFile()
    {
        var path = Make("sodium.jar");
        var resource = new Resource(path);

        Assert.True(resource.Destroy());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DestroyingWithoutTrashDeletesOutright()
    {
        var path = Make("sodium.jar");
        var resource = new Resource(path);

        Assert.True(resource.Destroy(attemptTrash: false));
        Assert.False(File.Exists(path));
    }
}
