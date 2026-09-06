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
 * THIS IS THE PAGE THAT BREAKS INSTANCES, so what is checked is mostly what it REFUSES to do.
 *
 * Upstream re-derives eight buttons' enabled states in VersionPage::updateButtons(), called from six
 * places; missing one call leaves a button acting on a row that is no longer selected. Expressed as
 * properties, the rules can be checked directly -- and the rules were read off Component.cpp rather
 * than assumed, because two of the three I first assumed were wrong.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class VersionPageViewModelTests
{
    private static PackProfile NewProfile()
        => new(new RuntimeContext { System = "windows", JavaArchitecture = "64", JavaRealArchitecture = "x86_64" });

    /// <summary>A component with a version file behind it, so it resolves.</summary>
    private static Component Make(
        string uid,
        string name,
        string version = "1.0",
        bool important = false,
        bool dependencyOnly = false)
    {
        var component = new Component(uid)
        {
            Version = version,
            IsImportant = important,
            IsDependencyOnly = dependencyOnly,
            LocalFile = new VersionFile { Uid = uid, Name = name, Version = version },
        };

        component.SetCachedData(name, version, [], [], isVolatile: false);

        return component;
    }

    private static VersionPageViewModel Load(params Component[] components) => Load(string.Empty, components);

    private static VersionPageViewModel Load(string path, params Component[] components)
    {
        var profile = NewProfile();

        foreach (var component in components)
        {
            profile.AppendComponent(component);
        }

        var page = new VersionPageViewModel();
        page.Load(profile, path);

        return page;
    }

    // ================================================================== what it shows

    [Fact]
    public void TheComponentsAreListedInOrder()
    {
        var page = Load(Make("net.minecraft", "Minecraft"), Make("net.fabricmc.fabric-loader", "Fabric Loader"));

        Assert.Equal(["Minecraft", "Fabric Loader"], page.Components.Select(c => c.Name));
    }

    /*
     * A component with no version has metadata that has not loaded. Blank would read as "no version",
     * which is a different and more alarming thing than "not known yet".
     */
    [Fact]
    public void AComponentWithNoVersionSaysSoRatherThanShowingNothing()
    {
        var page = Load(Make("net.minecraft", "Minecraft", version: string.Empty));

        Assert.Equal("(not loaded)", page.Components[0].Version);
    }

    [Fact]
    public void NothingIsSelectedToBeginWith()
    {
        var page = Load(Make("net.minecraft", "Minecraft"));

        Assert.False(page.HasSelection);
        Assert.False(page.CanRemove);
        Assert.False(page.CanMoveUp);
        Assert.False(page.CanMoveDown);
    }

    // ================================================================== what the buttons may do

    /*
     * `isRemovable()` is `!important`. Minecraft itself cannot go -- an instance without it is not an
     * instance -- and this is the single most destructive button on the page.
     */
    [Fact]
    public void AnImportantComponentCannotBeRemoved()
    {
        var page = Load(Make("net.minecraft", "Minecraft", important: true));

        page.Select("net.minecraft");

        Assert.True(page.HasSelection);
        Assert.False(page.CanRemove);
    }

    /*
     * A DEPENDENCY-ONLY COMPONENT IS REMOVABLE, which is not obvious: it is only present because
     * something else asked for it. Upstream allows it because removing whatever pulled it in is how a
     * user gets rid of it.
     */
    [Fact]
    public void ADependencyOnlyComponentCanStillBeRemoved()
    {
        var page = Load(Make("org.lwjgl3", "LWJGL 3", dependencyOnly: true));

        page.Select("org.lwjgl3");

        Assert.True(page.CanRemove);
    }

    /*
     * CHANGING A VERSION NEEDS A VERSION LIST, which is upstream's `isVersionChangeable(false)`. My
     * first version of this asked "is it not custom", which is a different question with a different
     * answer -- and would have offered the button for components with nothing to choose from.
     */
    [Fact]
    public void VersionCannotBeChangedWithoutAVersionListToChooseFrom()
    {
        var page = Load(Make("net.minecraft", "Minecraft", important: true));

        page.Select("net.minecraft");

        // Important, so not removable -- but also no version list here, so not changeable either.
        Assert.False(page.CanRemove);
        Assert.False(page.CanChangeVersion);
    }

    [Fact]
    public void VersionCanBeChangedWhenAListHasEntries()
    {
        var component = Make("net.minecraft", "Minecraft", important: true);

        component.VersionList = new VersionList("net.minecraft");
        component.VersionList.GetOrCreateVersion("1.20.1");

        var page = Load(component);
        page.Select("net.minecraft");

        Assert.True(page.CanChangeVersion);
    }

    // ================================================================== moving

    /*
     * DIVERGES FROM UPSTREAM, deliberately: `isMoveable()` there is a hardcoded `true`, so Move Up is
     * enabled on the top row and does something surprising when pressed (upstream bug #19 -- it swaps
     * with the BOTTOM row). Guarding on position keeps that unreachable and keeps the button honest.
     */
    [Fact]
    public void TheEndsOfTheListCannotBeMovedPast()
    {
        var page = Load(Make("a", "A"), Make("b", "B"), Make("c", "C"));

        page.Select("a");

        Assert.False(page.CanMoveUp);
        Assert.True(page.CanMoveDown);

        page.Select("c");

        Assert.True(page.CanMoveUp);
        Assert.False(page.CanMoveDown);
    }

    [Fact]
    public void MovingReordersTheList()
    {
        var page = Load(Make("a", "A"), Make("b", "B"), Make("c", "C"));

        page.Select("b");
        page.MoveSelectedUp();

        Assert.Equal(["B", "A", "C"], page.Components.Select(c => c.Name));
    }

    /*
     * The selection follows the component, not the row. Otherwise a second press of Move Up moves a
     * different component -- which is how someone reorders their instance by accident.
     */
    [Fact]
    public void TheSelectionFollowsTheComponentThatMoved()
    {
        var page = Load(Make("a", "A"), Make("b", "B"), Make("c", "C"));

        page.Select("c");
        page.MoveSelectedUp();

        Assert.Equal("c", page.Selected?.Uid);

        page.MoveSelectedUp();

        Assert.Equal(["C", "A", "B"], page.Components.Select(c => c.Name));
    }

    /// <summary>The command does not trust the button, as everywhere else in this port.</summary>
    [Fact]
    public void MovingPastTheEndThroughTheCommandDoesNothing()
    {
        var page = Load(Make("a", "A"), Make("b", "B"));

        page.Select("a");
        page.MoveSelectedUp();

        Assert.Equal(["A", "B"], page.Components.Select(c => c.Name));
        Assert.False(page.HasUnsavedChanges);
    }

    // ================================================================== removing

    [Fact]
    public void RemovingTakesTheComponentOutOfTheList()
    {
        var page = Load(Make("net.minecraft", "Minecraft", important: true), Make("org.lwjgl3", "LWJGL 3"));

        page.Select("org.lwjgl3");
        page.RemoveSelected();

        Assert.Equal(["Minecraft"], page.Components.Select(c => c.Name));
        Assert.True(page.HasUnsavedChanges);
        Assert.False(page.HasSelection);
    }

    [Fact]
    public void RemovingAnImportantComponentThroughTheCommandDoesNothing()
    {
        var page = Load(Make("net.minecraft", "Minecraft", important: true));

        page.Select("net.minecraft");
        page.RemoveSelected();

        Assert.Single(page.Components);
        Assert.False(page.HasUnsavedChanges);
    }

    [Fact]
    public void RemovingWithNothingSelectedDoesNothing()
    {
        var page = Load(Make("a", "A"));

        page.RemoveSelected();

        Assert.Single(page.Components);
    }

    // ================================================================== problems

    /*
     * A component's problems depend on its NEIGHBOURS -- removing Fabric makes every Fabric mod's
     * requirement unsatisfied. So the rows are rebuilt from the profile after each edit; patching one
     * row in place would leave stale verdicts on screen next to a changed instance.
     */
    [Fact]
    public void ProblemsAreCarriedOntoTheRows()
    {
        var broken = Make("broken", "Broken");
        broken.AddComponentProblem(ProblemSeverity.Error, "Missing requirement org.lwjgl3");

        var page = Load(Make("fine", "Fine"), broken);

        Assert.True(page.HasProblems);
        Assert.Equal(ProblemSeverity.Error, page.WorstSeverity);

        var row = page.Components.Single(c => c.Uid == "broken");

        Assert.True(row.HasProblems);
        Assert.Contains("org.lwjgl3", row.Problems, StringComparison.Ordinal);

        Assert.False(page.Components.Single(c => c.Uid == "fine").HasProblems);
    }

    [Fact]
    public void AHealthyInstanceReportsNoProblems()
    {
        var page = Load(Make("a", "A"), Make("b", "B"));

        Assert.False(page.HasProblems);
        Assert.Equal(ProblemSeverity.None, page.WorstSeverity);
    }

    // ================================================================== saving

    [Fact]
    public void SavingClearsTheUnsavedFlagAndWritesTheChange()
    {
        var temp = Path.Combine(Path.GetTempPath(), "el-vp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var path = Path.Combine(temp, "mmc-pack.json");

            var page = Load(path, Make("net.minecraft", "Minecraft", important: true), Make("org.lwjgl3", "LWJGL 3"));

            page.Select("org.lwjgl3");
            page.RemoveSelected();

            Assert.True(page.Save());
            Assert.False(page.HasUnsavedChanges);

            // Read back rather than trusting the flag: what is on disk is the thing that launches.
            var reloaded = NewProfile();

            Assert.True(reloaded.Load(path));
            Assert.Equal(["net.minecraft"], reloaded.Components.Select(c => c.Uid));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    // ================================================================== reload and folders

    private sealed class StubFolderOpener : IFolderOpener
    {
        public string? Opened { get; private set; }

        public Task OpenAsync(string path)
        {
            Opened = path;

            return Task.CompletedTask;
        }

        public Task OpenKnownAsync(string kind) => Task.CompletedTask;
    }

    [Fact]
    public void ReloadPicksUpAChangeMadeOnDisk()
    {
        var temp = Path.Combine(Path.GetTempPath(), "el-vpr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var path = Path.Combine(temp, "mmc-pack.json");

            var full = NewProfile();
            full.AppendComponent(Make("net.minecraft", "Minecraft"));
            full.AppendComponent(Make("org.lwjgl3", "LWJGL 3"));
            Assert.True(full.Save(path));

            var page = new VersionPageViewModel();
            page.Load(full, path);
            Assert.Equal(2, page.Components.Count);

            // Something else rewrites the file with only one component.
            var slim = NewProfile();
            slim.AppendComponent(Make("net.minecraft", "Minecraft"));
            Assert.True(slim.Save(path));

            page.ReloadCommand.Execute(null);

            Assert.Equal(["net.minecraft"], page.Components.Select(c => c.Uid));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    // ================================================================== add empty

    private sealed class StubNewComponentPrompt(NewComponentChoice? choice) : INewComponentPrompt
    {
        public IReadOnlyList<string>? SawExisting { get; private set; }

        public Task<NewComponentChoice?> AskAsync(IReadOnlyList<string> existingUids)
        {
            SawExisting = existingUids;

            return Task.FromResult(choice);
        }
    }

    [Fact]
    public void AddEmptyIsOffWithoutAPrompt()
    {
        var page = new VersionPageViewModel();
        page.Load(NewProfile(), "p");

        Assert.False(page.CanAddEmpty);
    }

    [Fact]
    public async Task AddEmptyAddsACustomComponentAndWritesItsPatch()
    {
        var temp = Path.Combine(Path.GetTempPath(), "el-vpae-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var path = Path.Combine(temp, "mmc-pack.json");

            var profile = NewProfile();
            profile.AppendComponent(Make("net.minecraft", "Minecraft", important: true));

            var prompt = new StubNewComponentPrompt(new NewComponentChoice("com.example.tweaks", "My Tweaks"));
            var page = new VersionPageViewModel(newComponent: prompt);
            page.Load(profile, path);

            Assert.True(page.CanAddEmpty);

            await page.AddEmptyCommand.ExecuteAsync(null);

            // The dialog was told which uids were taken, the component appears, and its patch is on disk.
            Assert.Contains("net.minecraft", prompt.SawExisting!);
            Assert.Contains(page.Components, c => c.Uid == "com.example.tweaks" && c.Name == "My Tweaks");
            Assert.True(page.HasUnsavedChanges);
            Assert.True(File.Exists(Path.Combine(temp, "patches", "com.example.tweaks.json")));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task CancellingAddEmptyChangesNothing()
    {
        var profile = NewProfile();
        profile.AppendComponent(Make("net.minecraft", "Minecraft", important: true));

        var page = new VersionPageViewModel(newComponent: new StubNewComponentPrompt(null));
        page.Load(profile, "p");

        await page.AddEmptyCommand.ExecuteAsync(null);

        Assert.Single(page.Components);
        Assert.False(page.HasUnsavedChanges);
    }

    [Fact]
    public void WithNoFolderOpenerTheFolderButtonsAreOff()
    {
        var page = new VersionPageViewModel();
        page.Load(NewProfile(), "p", gameRoot: "/game", libraryPath: "/game/libraries");

        Assert.False(page.CanOpenMinecraftFolder);
        Assert.False(page.CanOpenLibrariesFolder);
    }

    [Fact]
    public async Task TheMinecraftAndLibrariesButtonsOpenTheirOwnFolders()
    {
        var temp = Path.Combine(Path.GetTempPath(), "el-vpf-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(temp, "minecraft");
        var libraries = Path.Combine(temp, "libraries");

        try
        {
            var opener = new StubFolderOpener();
            var page = new VersionPageViewModel(folders: opener);
            page.Load(NewProfile(), "p", gameRoot: game, libraryPath: libraries);

            Assert.True(page.CanOpenMinecraftFolder);
            Assert.True(page.CanOpenLibrariesFolder);

            await page.OpenMinecraftFolderCommand.ExecuteAsync(null);
            Assert.Equal(game.Replace('\\', '/'), opener.Opened!.Replace('\\', '/'));
            Assert.True(Directory.Exists(game));

            await page.OpenLibrariesFolderCommand.ExecuteAsync(null);
            Assert.Equal(libraries.Replace('\\', '/'), opener.Opened!.Replace('\\', '/'));
            Assert.True(Directory.Exists(libraries));
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }
}
