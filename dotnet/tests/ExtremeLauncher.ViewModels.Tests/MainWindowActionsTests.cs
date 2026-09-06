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
 * Renaming an instance, moving it between groups, and opening folders.
 *
 * ASSERTED ON instance.cfg AND instgroups.json where the question is "did that stick". Both are
 * compatibility surfaces an upstream install reads.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class MainWindowActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "el-mwact-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public MainWindowActionsTests()
    {
        _instances = Path.Combine(_root, "instances");

        Directory.CreateDirectory(_instances);
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

    private void MakeInstance(string id, string name)
    {
        var path = Path.Combine(_instances, id);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "instance.cfg"), $"name={name}\nInstanceType=OneSix\n");
    }

    private InstanceList NewList()
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_root, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        return list;
    }

    /// <summary>Answers prompts with a fixed string, and records what it was asked.</summary>
    private sealed class StubPrompts(string? answer) : IUserPrompts
    {
        public string? AskedTitle { get; private set; }

        public string? AskedMessage { get; private set; }

        public string? Initial { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
            => Task.FromResult(true);

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
        {
            AskedTitle = title;
            AskedMessage = message;
            Initial = initialValue;

            return Task.FromResult(answer);
        }
    }

    private sealed class StubFolders : IFolderOpener
    {
        public List<string> Opened { get; } = [];

        public List<string> OpenedKinds { get; } = [];

        public Task OpenAsync(string path)
        {
            Opened.Add(path);

            return Task.CompletedTask;
        }

        public Task OpenKnownAsync(string kind)
        {
            OpenedKinds.Add(kind);

            return Task.CompletedTask;
        }
    }

    private MainWindowViewModel Load(IUserPrompts? prompts = null, IFolderOpener? folders = null)
    {
        var vm = new MainWindowViewModel(prompts: prompts, folders: folders);

        vm.Instances.Load(NewList());

        return vm;
    }

    [Fact]
    public async Task RenamingWritesTheNewNameToInstanceCfg()
    {
        MakeInstance("one", "Old Name");

        var vm = Load(new StubPrompts("New Name"));

        vm.Select("one");

        Assert.True(vm.CanRename);

        await vm.RenameSelectedAsync();

        // The FILE, which is what an upstream install would read.
        Assert.Contains("name=New Name", File.ReadAllText(Path.Combine(_instances, "one", "instance.cfg")), StringComparison.Ordinal);
        Assert.Equal("New Name", vm.Instances.Selected?.Name);
    }

    [Fact]
    public async Task TheRenamePromptStartsFromTheCurrentName()
    {
        // Retyping a name to change one letter of it is a small, avoidable annoyance.
        MakeInstance("one", "Old Name");

        var prompts = new StubPrompts("whatever");

        var vm = Load(prompts);

        vm.Select("one");

        await vm.RenameSelectedAsync();

        Assert.Equal("Old Name", prompts.Initial);
    }

    [Fact]
    public async Task CancellingTheRenameChangesNothing()
    {
        /*
         * NULL AND EMPTY ARE DIFFERENT ANSWERS. Null is "cancelled"; empty is a name somebody
         * actually typed, and the two want different treatment.
         */
        MakeInstance("one", "Old Name");

        var vm = Load(new StubPrompts(null));

        vm.Select("one");

        await vm.RenameSelectedAsync();

        Assert.Equal("Old Name", vm.Instances.Selected?.Name);
    }

    [Fact]
    public async Task AnEmptyNameIsRefusedAndSaidSo()
    {
        // An instance with no name is a blank tile the user cannot find again.
        MakeInstance("one", "Old Name");

        var vm = Load(new StubPrompts("   "));

        vm.Select("one");

        await vm.RenameSelectedAsync();

        Assert.Equal("Old Name", vm.Instances.Selected?.Name);
        Assert.Contains(vm.Launch.LogLines, l => l.Text.Contains("needs a name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangingTheGroupMovesTheInstance()
    {
        MakeInstance("one", "An Instance");

        var vm = Load(new StubPrompts("Modded"));

        vm.Select("one");

        Assert.True(vm.CanChangeGroup);

        await vm.ChangeGroupAsync();

        Assert.Equal("Modded", vm.Instances.Selected?.Group);
        Assert.Contains(vm.Instances.Groups, g => g.Name == "Modded");
    }

    [Fact]
    public async Task AnEmptyGroupTakesTheInstanceOutOfOne()
    {
        /*
         * EMPTY IS MEANINGFUL HERE, unlike renaming: it is the only way to leave a group, so refusing
         * it the way a blank name is refused would trap an instance in whatever group it was put in.
         */
        MakeInstance("one", "An Instance");

        var vm = Load(new StubPrompts("Modded"));

        vm.Select("one");
        await vm.ChangeGroupAsync();

        var second = Load(new StubPrompts(string.Empty));

        second.Select("one");
        await second.ChangeGroupAsync();

        Assert.Equal(string.Empty, second.Instances.Selected?.Group);
    }

    [Fact]
    public async Task TheGroupPromptNamesTheGroupsThatAlreadyExist()
    {
        // Typing a group name that differs by a capital letter makes a second group nobody wanted.
        MakeInstance("one", "First");
        MakeInstance("two", "Second");

        var vm = Load(new StubPrompts("Modded"));

        vm.Select("one");
        await vm.ChangeGroupAsync();

        var prompts = new StubPrompts("whatever");
        var second = Load(prompts);

        second.Select("two");
        await second.ChangeGroupAsync();

        Assert.Contains("Modded", prompts.AskedMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningTheInstanceFolderPassesItsOwnPath()
    {
        MakeInstance("one", "An Instance");

        var folders = new StubFolders();
        var vm = Load(folders: folders);

        vm.Select("one");

        Assert.True(vm.CanOpenInstanceFolder);

        await vm.OpenInstanceFolderAsync();

        Assert.Single(folders.Opened);
        Assert.EndsWith("one", folders.Opened[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLaunchersOwnFoldersAreAskedForByName()
    {
        /*
         * The view model never learns a path: the app resolves the name from LauncherPaths, which is
         * the single place that decides where anything lives.
         */
        var folders = new StubFolders();
        var vm = Load(folders: folders);

        await vm.OpenFolderAsync("logs");

        Assert.Equal(["logs"], folders.OpenedKinds);
    }

    [Fact]
    public void WithNoFolderOpenerTheMenuIsDisabled()
    {
        var vm = Load();

        Assert.False(vm.CanOpenFolders);
        Assert.False(vm.CanOpenInstanceFolder);
    }

    [Fact]
    public void WithNoPromptsRenamingIsNotOffered()
    {
        MakeInstance("one", "An Instance");

        var vm = Load();

        vm.Select("one");

        Assert.False(vm.CanRename);
        Assert.False(vm.CanChangeGroup);
    }

    [Fact]
    public void NothingSelectedMeansNothingToRenameOrGroup()
    {
        MakeInstance("one", "An Instance");

        var vm = Load(new StubPrompts("x"), new StubFolders());

        Assert.False(vm.CanRename);
        Assert.False(vm.CanChangeGroup);
        Assert.False(vm.CanOpenInstanceFolder);

        // But the launcher's own folders need no selection at all.
        Assert.True(vm.CanOpenFolders);
    }

    [Fact]
    public void SelectingAnnouncesThatTheButtonsShouldWakeUp()
    {
        // Bound to IsEnabled; a value assertion cannot see a missing notification.
        MakeInstance("one", "An Instance");

        var vm = Load(new StubPrompts("x"));

        var announced = false;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanRename))
            {
                announced = true;
            }
        };

        vm.Select("one");

        Assert.True(announced);
    }
}
