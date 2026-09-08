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
 * For AtlUpdateCleaner.PlanDeletions (ATLPackInstallTask::deleteExistingFiles): which files an update
 * removes. The built-in rules (clear mods/bin, keep a handful of files and folders), the pack's own
 * keeps/deletes, the "%s%" separator, and the divergence that a kept file is never removed by clearing
 * a folder above it are what these pin.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class AtlUpdateCleanerTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "el-atlclean-" + Guid.NewGuid().ToString("N"));

    public AtlUpdateCleanerTests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_game, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private string Full(string relative) => (_game + "/" + relative).Replace('\\', '/');

    private List<string> Plan(AtlVersionKeeps? keeps = null, AtlVersionDeletes? deletes = null)
        => AtlUpdateCleaner.PlanDeletions(_game, keeps ?? new AtlVersionKeeps(), deletes ?? new AtlVersionDeletes());

    [Fact]
    public void TheBuiltinRulesClearModsAndBinButKeepTheKnownFiles()
    {
        Write("mods/a.jar");
        Write("mods/PortalGunSounds.pak");
        Write("mods/rei_minimap/map.dat");
        Write("mods/VoxelMods/v.txt");
        Write("bin/natives.jar");
        Write("options.txt");
        Write("config/NEI.cfg");

        var plan = Plan();

        Assert.Contains(Full("mods/a.jar"), plan);
        Assert.Contains(Full("bin/natives.jar"), plan);

        // The built-in keeps survive.
        Assert.DoesNotContain(Full("mods/PortalGunSounds.pak"), plan);
        Assert.DoesNotContain(Full("mods/rei_minimap/map.dat"), plan);
        Assert.DoesNotContain(Full("mods/VoxelMods/v.txt"), plan);

        // options.txt and config are not under a built-in delete folder, so they are untouched.
        Assert.DoesNotContain(Full("options.txt"), plan);
        Assert.DoesNotContain(Full("config/NEI.cfg"), plan);
    }

    /// <summary>Clearing a folder never returns a directory, so a kept file below it is never collateral.</summary>
    [Fact]
    public void AKeptFileIsNeverRemovedByClearingAFolderAboveIt()
    {
        Write("mods/rei_minimap/config/settings.dat"); // nested under a keep folder
        Write("mods/rei_minimap/cache/tile.png");
        Write("mods/junk.jar");

        var plan = Plan();

        Assert.Contains(Full("mods/junk.jar"), plan);
        Assert.DoesNotContain(Full("mods/rei_minimap/config/settings.dat"), plan);
        Assert.DoesNotContain(Full("mods/rei_minimap/cache/tile.png"), plan);
        Assert.DoesNotContain(_game.Replace('\\', '/') + "/mods/rei_minimap", plan); // never the directory itself
    }

    [Fact]
    public void APackDeleteRemovesAFileAndTheSeparatorPlaceholderIsExpanded()
    {
        Write("extra/thing.cfg");

        var deletes = new AtlVersionDeletes();
        deletes.Files.Add(new AtlVersionFileRule { Base = "root", Target = "extra%s%thing.cfg" });

        Assert.Contains(Full("extra/thing.cfg"), Plan(deletes: deletes));
    }

    [Fact]
    public void APackKeepOverridesAPackDelete()
    {
        Write("mods/wanted.jar");

        var deletes = new AtlVersionDeletes();
        deletes.Files.Add(new AtlVersionFileRule { Base = "root", Target = "mods%s%wanted.jar" });

        var keeps = new AtlVersionKeeps();
        keeps.Files.Add(new AtlVersionFileRule { Base = "root", Target = "mods%s%wanted.jar" });

        Assert.DoesNotContain(Full("mods/wanted.jar"), Plan(keeps, deletes));
    }

    [Fact]
    public void ThePackCanDeleteAConfigFileViaTheConfigBase()
    {
        Write("config/custom.cfg");

        var deletes = new AtlVersionDeletes();
        deletes.Folders.Add(new AtlVersionFileRule { Base = "config", Target = "" });

        Assert.Contains(Full("config/custom.cfg"), Plan(deletes: deletes));
    }

    [Fact]
    public void NothingIsPlannedWhenTheFoldersAreAbsent()
        => Assert.Empty(Plan());
}
