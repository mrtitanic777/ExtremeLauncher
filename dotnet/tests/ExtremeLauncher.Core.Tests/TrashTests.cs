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
 * THESE TESTS REALLY DO PUT THINGS IN THE RECYCLE BIN. They use uniquely named temporary directories
 * so that what lands there is unmistakably theirs, and they never call this on a path they did not
 * create themselves.
 *
 * What matters most is the negative: that a FAILED trash leaves the original exactly where it was.
 * A caller reading "false" and then deleting permanently is fine; a caller reading "false" after the
 * data has already moved somewhere unknown is data loss.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class TrashTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "el-trash-" + Guid.NewGuid().ToString("N"));

    public TrashTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the point of the test may have been to move it elsewhere.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Make(string name, string content = "hello")
    {
        var path = Path.Combine(_temp, name);

        File.WriteAllText(path, content);

        return path;
    }

    // ================================================================== refusing

    /*
     * "There is nothing there" is FALSE, not an exception and not a success. A caller that treats a
     * missing path as trashed would report an instance deleted that is still on disk.
     */
    [Fact]
    public void TrashingSomethingThatIsNotThereFails()
        => Assert.False(Trash.TryTrash(Path.Combine(_temp, "no-such-file"), out _));

    [Fact]
    public void TrashingRejectsNull()
        => Assert.Throws<ArgumentNullException>(() => Trash.TryTrash(null!, out _));

    // ================================================================== the real thing

    /*
     * Runs on every platform, and accepts EITHER outcome, because "this filesystem has no trash" is a
     * normal state upstream returns false for too -- Flatpak, Windows Server, a FAT USB stick.
     *
     * What is not negotiable is the pairing: trashed means gone from where it was, and not trashed
     * means still exactly where it was. Anything else loses data.
     */
    [Fact]
    public void AFileIsEitherTrashedOrLeftAloneEntirely()
    {
        var file = Make("doomed.txt");

        var trashed = Trash.TryTrash(file, out var trashedPath);

        if (trashed)
        {
            Assert.False(File.Exists(file));

            // Where knowable, the reported path is real -- an undo depends on it.
            if (trashedPath.Length != 0)
            {
                Assert.True(File.Exists(trashedPath));
                Assert.Equal("hello", File.ReadAllText(trashedPath));

                File.Delete(trashedPath);
            }
        }
        else
        {
            Assert.True(File.Exists(file));
            Assert.Equal(string.Empty, trashedPath);
        }
    }

    [Fact]
    public void ADirectoryGoesWithEverythingInside()
    {
        var directory = Path.Combine(_temp, "instance");

        Directory.CreateDirectory(Path.Combine(directory, "minecraft", "saves", "World"));
        File.WriteAllText(Path.Combine(directory, "minecraft", "saves", "World", "level.dat"), "world");

        var trashed = Trash.TryTrash(directory, out var trashedPath);

        if (trashed)
        {
            Assert.False(Directory.Exists(directory));

            if (trashedPath.Length != 0)
            {
                Assert.Equal(
                    "world",
                    File.ReadAllText(Path.Combine(trashedPath, "minecraft", "saves", "World", "level.dat")));

                Directory.Delete(trashedPath, recursive: true);
            }
        }
        else
        {
            Assert.True(Directory.Exists(directory));
        }
    }

    /*
     * Two instances with the same name must not have the second overwrite the first INSIDE the trash,
     * which would destroy the very thing being preserved. Only checkable where the trashed path is
     * knowable, which is not Windows -- there the shell handles it.
     */
    [SkippableFact]
    public void TrashingTwoThingsOfTheSameNameKeepsBoth()
    {
        var first = Path.Combine(_temp, "a", "Pack");
        var second = Path.Combine(_temp, "b", "Pack");

        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        File.WriteAllText(Path.Combine(first, "which.txt"), "first");
        File.WriteAllText(Path.Combine(second, "which.txt"), "second");

        Skip.IfNot(Trash.TryTrash(first, out var firstPath) && firstPath.Length != 0,
            "This platform has no trash, or does not report where things went.");

        try
        {
            Assert.True(Trash.TryTrash(second, out var secondPath));
            Assert.NotEqual(firstPath, secondPath);

            Assert.Equal("first", File.ReadAllText(Path.Combine(firstPath, "which.txt")));
            Assert.Equal("second", File.ReadAllText(Path.Combine(secondPath, "which.txt")));

            Directory.Delete(secondPath, recursive: true);
        }
        finally
        {
            if (Directory.Exists(firstPath))
            {
                Directory.Delete(firstPath, recursive: true);
            }
        }
    }
}
