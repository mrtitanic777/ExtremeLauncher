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
 * DELETION IS THE ONE OPERATION WITH NO SECOND CHANCE, so it gets the most careful tests in the port.
 *
 * The interesting cases are all failures. A delete that works is easy; a delete that half-works --
 * removing the instance from its group and then failing to move the directory -- loses something the
 * user never agreed to lose, and that is upstream bug #18, pinned below.
 *
 * Everything here works inside a temporary directory it created. Trashing really does use the desktop
 * trash, so instances made here are named unmistakably.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstanceDeletionTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-del-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public InstanceDeletionTests()
    {
        _instances = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(_instances);
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

    private InstanceList NewList()
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        return list;
    }

    private string MakeInstance(string id, string? world = null)
    {
        var path = Path.Combine(_instances, id);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "instance.cfg"), $"name={id}\nInstanceType=OneSix\n");

        if (world is not null)
        {
            var saves = Path.Combine(path, "minecraft", "saves", world);

            Directory.CreateDirectory(saves);
            File.WriteAllText(Path.Combine(saves, "level.dat"), "irreplaceable");
        }

        return path;
    }

    // ================================================================== permanent deletion

    [Fact]
    public void DeletingRemovesTheDirectoryAndTheEntry()
    {
        var path = MakeInstance("Doomed");
        var list = NewList();

        Assert.True(list.DeleteInstance("Doomed"));

        Assert.False(Directory.Exists(path));
        Assert.Null(list.GetInstanceById("Doomed"));
        Assert.Equal(0, list.Count);
    }

    /// <summary>An instance already gone from disk is not an error — it is the desired state.</summary>
    [Fact]
    public void DeletingSomethingThatIsNotThereIsHarmless()
    {
        MakeInstance("Real");

        var list = NewList();

        Assert.False(list.DeleteInstance("NeverExisted"));
        Assert.Equal(1, list.Count);
    }

    [Fact]
    public void DeletingLeavesOtherInstancesAlone()
    {
        MakeInstance("Keep", world: "My World");
        MakeInstance("Doomed");

        var list = NewList();

        Assert.True(list.DeleteInstance("Doomed"));

        Assert.NotNull(list.GetInstanceById("Keep"));
        Assert.True(File.Exists(Path.Combine(_instances, "Keep", "minecraft", "saves", "My World", "level.dat")));
    }

    [Fact]
    public void DeletingTakesTheInstanceOutOfItsGroup()
    {
        MakeInstance("Doomed");

        var list = NewList();
        list.SetInstanceGroup("Doomed", "Modded");

        Assert.True(list.DeleteInstance("Doomed"));
        Assert.Equal(string.Empty, NewList().GetInstanceGroup("Doomed"));
    }

    // ================================================================== upstream bug #18

    /*
     * A FAILED REMOVAL MUST CHANGE NOTHING. Upstream removes the instance from the group index and
     * saves instgroups.json BEFORE attempting the trash, then returns false when the trash fails --
     * so the instance is still on disk, still listed, and silently no longer in its group.
     *
     * Trash failure is not exotic: upstream itself returns false under Flatpak and on Windows Server.
     *
     * Provoked for real: a file inside the instance is held open with no sharing, so the directory
     * cannot be moved or removed and both operations genuinely fail with the instance present and
     * listed. An earlier version of this test used an id that did not exist, which returns at the null
     * check and never reaches the ordering at issue -- it would have passed against the bug.
     */
    [SkippableFact]
    public void AFailedRemovalDoesNotDisturbGrouping()
    {
        // The way this test MAKES a removal fail -- holding a file open exclusively -- only blocks a
        // move or delete on Windows. Unix lets you rename and unlink open files, so the removal would
        // succeed there and the scenario cannot be reproduced; the property it checks (a failed removal
        // leaves grouping untouched) is Windows-specific in how it arises.
        Skip.IfNot(OperatingSystem.IsWindows(), "Open files only block removal on Windows.");

        var path = MakeInstance("Stuck", world: "My World");

        var list = NewList();
        list.SetInstanceGroup("Stuck", "Modded");

        // Held open exclusively for as long as this handle lives.
        using (var _ = new FileStream(
                   Path.Combine(path, "minecraft", "saves", "My World", "level.dat"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Assert.False(list.TrashInstance("Stuck"));
            Assert.False(list.DeleteInstance("Stuck"));
        }

        // Nothing moved...
        Assert.True(Directory.Exists(path));
        Assert.NotNull(list.GetInstanceById("Stuck"));
        Assert.False(list.TrashedSomething);

        // ...and nothing was written to instgroups.json either. Re-read from disk, because the point
        // is what was persisted rather than what is still in memory.
        Assert.Equal("Modded", NewList().GetInstanceGroup("Stuck"));
    }

    // ================================================================== trashing

    /*
     * Accepts either outcome, because "this filesystem has no trash" is a state upstream returns false
     * for too. What must hold either way is the pairing: trashed means gone AND delisted, not trashed
     * means everything exactly as it was.
     */
    [Fact]
    public void TrashingEitherRemovesTheInstanceEntirelyOrChangesNothing()
    {
        var path = MakeInstance("TrashProbe", world: "My World");

        var list = NewList();
        list.SetInstanceGroup("TrashProbe", "Modded");

        if (list.TrashInstance("TrashProbe"))
        {
            Assert.False(Directory.Exists(path));
            Assert.Null(list.GetInstanceById("TrashProbe"));
            Assert.Equal(string.Empty, NewList().GetInstanceGroup("TrashProbe"));
        }
        else
        {
            Assert.True(Directory.Exists(path));
            Assert.NotNull(list.GetInstanceById("TrashProbe"));
            Assert.Equal("Modded", NewList().GetInstanceGroup("TrashProbe"));
        }
    }

    /// <summary>Undo is offered only when the platform said where the instance went.</summary>
    [Fact]
    public void UndoIsOnlyOfferedWhenItCanActuallyWork()
    {
        MakeInstance("TrashProbe");

        var list = NewList();

        Assert.False(list.TrashedSomething);

        var trashed = list.TrashInstance("TrashProbe");

        // Windows does not report the trashed path, so there is nothing to undo from -- and claiming
        // otherwise would put an Undo button in front of the user that could not work.
        Assert.Equal(trashed && !OperatingSystem.IsWindows(), list.TrashedSomething);
    }

    [Fact]
    public void UndoingNothingIsHarmless()
        => Assert.Equal(string.Empty, NewList().UndoTrashInstance());

    /*
     * The full round trip, where the platform supports it: the instance comes back, with its worlds and
     * its group. Skipped where the trashed path is unknowable rather than pretended.
     */
    [SkippableFact]
    public void AnUndoneTrashRestoresTheInstanceAndItsGroup()
    {
        MakeInstance("Restorable", world: "My World");

        var list = NewList();
        list.SetInstanceGroup("Restorable", "Modded");

        Skip.IfNot(
            list.TrashInstance("Restorable") && list.TrashedSomething,
            "This platform has no trash, or does not report where things went.");

        Assert.Equal("Restorable", list.UndoTrashInstance());

        Assert.NotNull(list.GetInstanceById("Restorable"));
        Assert.Equal("Modded", list.GetInstanceGroup("Restorable"));

        Assert.Equal(
            "irreplaceable",
            File.ReadAllText(Path.Combine(_instances, "Restorable", "minecraft", "saves", "My World", "level.dat")));

        Assert.False(list.TrashedSomething);
    }

    /*
     * Something else already sitting where it came from must not be overwritten by the restore. Upstream
     * appends "1" to the id and the path until the name is free, which is odd-looking and is what
     * existing installs do.
     */
    [SkippableFact]
    public void RestoringOntoAnOccupiedNameTakesADifferentOne()
    {
        MakeInstance("Clash", world: "Original");

        var list = NewList();

        Skip.IfNot(
            list.TrashInstance("Clash") && list.TrashedSomething,
            "This platform has no trash, or does not report where things went.");

        // Something new takes the old name while the first is in the trash.
        MakeInstance("Clash", world: "Replacement");

        Assert.Equal("Clash1", list.UndoTrashInstance());

        // Neither has clobbered the other.
        Assert.True(File.Exists(Path.Combine(_instances, "Clash", "minecraft", "saves", "Replacement", "level.dat")));
        Assert.True(File.Exists(Path.Combine(_instances, "Clash1", "minecraft", "saves", "Original", "level.dat")));
    }
}
