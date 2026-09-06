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
 * The instance window, rendered. The thing most worth checking here is that the page DataTemplates
 * resolve AT ALL: they are matched by view-model type, against an interface-typed collection, and a
 * template that fails to match shows the type name as text instead of the page. The build cannot tell.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class InstanceWindowTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-iw-ui-" + Guid.NewGuid().ToString("N"));

    public InstanceWindowTests() => Directory.CreateDirectory(_temp);

    /*
     * WINDOWS ARE CLOSED, not left open. Headless Avalonia keeps one application and one dispatcher for
     * the whole assembly, so a window left showing is re-laid-out by later tests -- and once the
     * platform has been torn down that throws "Unable to locate IFontManagerImpl", in whichever test
     * happens to be running. The symptom was a different test failing on each run.
     */
    private readonly List<Window> _windows = [];

    public void Dispose()
    {
        foreach (var window in _windows)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
                // Already gone, which is what the close-behaviour tests leave behind.
            }
        }

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static Component Make(string uid, string name, bool important = false)
    {
        var component = new Component(uid)
        {
            Version = "1.0",
            IsImportant = important,
            LocalFile = new VersionFile { Uid = uid, Name = name, Version = "1.0" },
        };

        component.SetCachedData(name, "1.0", [], [], isVolatile: false);

        return component;
    }

    private static VersionPageViewModel VersionPage(params Component[] components)
    {
        var profile = new PackProfile(
            new RuntimeContext { System = "windows", JavaArchitecture = "64", JavaRealArchitecture = "x86_64" });

        foreach (var component in components)
        {
            profile.AppendComponent(component);
        }

        var page = new VersionPageViewModel();
        page.Load(profile);

        return page;
    }

    private InstanceWindow Show(InstanceWindowViewModel viewModel)
    {
        var window = new InstanceWindow { DataContext = viewModel };

        _windows.Add(window);

        window.Show();

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        return window;
    }

    /// <summary>Lets bindings post their updates and item containers be created.</summary>
    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Button Button(Visual root, string content)
        => root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content);

    private static IReadOnlyList<string?> Texts(Visual root)
        => root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    // ================================================================== the shell

    [AvaloniaFact]
    public void TheWindowNamesTheInstanceAndListsItsPages()
    {
        var viewModel = new InstanceWindowViewModel("My Pack");

        viewModel.AddPage(VersionPage(Make("net.minecraft", "Minecraft", important: true)));
        viewModel.AddPage(new NotesPageViewModel());

        var window = Show(viewModel);

        Assert.Contains("My Pack", window.Title, StringComparison.Ordinal);

        var texts = Texts(window);

        Assert.Contains("Version", texts);
        Assert.Contains("Notes", texts);
    }

    /*
     * THE TEMPLATE ACTUALLY RESOLVES. Matched by view-model type against an interface-typed collection;
     * when it fails to match, Avalonia falls back to showing the type name as text -- which looks like
     * a rendered page until you read it.
     */
    [AvaloniaFact]
    public void TheVersionPageRendersItsComponentsRatherThanItsTypeName()
    {
        var viewModel = new InstanceWindowViewModel("My Pack");

        viewModel.AddPage(VersionPage(
            Make("net.minecraft", "Minecraft", important: true),
            Make("org.lwjgl3", "LWJGL 3")));

        var window = Show(viewModel);

        var texts = Texts(window);

        Assert.Contains("Minecraft", texts);
        Assert.Contains("LWJGL 3", texts);

        // The fallback a broken template produces.
        Assert.DoesNotContain(texts, t => t?.Contains("VersionPageViewModel", StringComparison.Ordinal) ?? false);
    }

    [AvaloniaFact]
    public void TheNotesPageRendersAnEditableBox()
    {
        var viewModel = new InstanceWindowViewModel("My Pack");

        var notes = new NotesPageViewModel();
        viewModel.AddPage(notes);

        var window = Show(viewModel);

        var box = window.GetLogicalDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);

        // Typed into the control, so a one-way binding would fail here.
        box.Text = "Remember to update Sodium.";

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Remember to update Sodium.", notes.Text);
        Assert.True(notes.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void SwitchingPagesSwapsWhatIsShown()
    {
        var viewModel = new InstanceWindowViewModel("My Pack");

        var version = VersionPage(Make("net.minecraft", "Minecraft"));
        var notes = new NotesPageViewModel();

        viewModel.AddPage(version);
        viewModel.AddPage(notes);

        var window = Show(viewModel);

        Assert.Contains("Minecraft", Texts(window));

        viewModel.SelectedPage = notes;

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // The version page's rows are gone; the notes box is there instead.
        Assert.DoesNotContain("Minecraft", Texts(window));
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBox>(), b => b.AcceptsReturn);
    }

    // ================================================================== the version page's buttons

    [AvaloniaTheory]
    [InlineData("Remove")]
    [InlineData("Move up")]
    [InlineData("Move down")]
    public void EveryLiveVersionButtonHasACommand(string label)
    {
        var viewModel = new InstanceWindowViewModel("My Pack");

        viewModel.AddPage(VersionPage(Make("net.minecraft", "Minecraft")));

        var window = Show(viewModel);

        Assert.NotNull(Button(window, label).Command);
    }

    /*
     * Minecraft is `important`, so Remove must be disabled on it -- the single most destructive button
     * in the instance window, and the rule was only ever checked as a property before now.
     */
    [AvaloniaFact]
    public void RemoveIsDisabledForAnImportantComponent()
    {
        var viewModel = new InstanceWindowViewModel("My Pack");

        var page = VersionPage(Make("net.minecraft", "Minecraft", important: true), Make("org.lwjgl3", "LWJGL 3"));
        viewModel.AddPage(page);

        var window = Show(viewModel);

        page.Select("net.minecraft");

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.False(Button(window, "Remove").IsEffectivelyEnabled);

        page.Select("org.lwjgl3");

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.True(Button(window, "Remove").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void TheEndsOfTheListDisableTheMoveButtons()
    {
        var viewModel = new InstanceWindowViewModel("My Pack");

        var page = VersionPage(Make("a", "A"), Make("b", "B"));
        viewModel.AddPage(page);

        var window = Show(viewModel);

        page.Select("a");

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.False(Button(window, "Move up").IsEffectivelyEnabled);
        Assert.True(Button(window, "Move down").IsEffectivelyEnabled);
    }

    // ================================================================== problems

    [AvaloniaFact]
    public void AComponentsProblemsAreShownOnItsRow()
    {
        var broken = Make("broken", "Broken");
        broken.AddComponentProblem(Core.ProblemSeverity.Error, "Missing requirement org.lwjgl3");

        var viewModel = new InstanceWindowViewModel("My Pack");
        viewModel.AddPage(VersionPage(broken));

        var window = Show(viewModel);

        Assert.Contains(
            Texts(window),
            t => t?.Contains("Missing requirement org.lwjgl3", StringComparison.Ordinal) ?? false);
    }

    // ================================================================== saving on close

    /*
     * THE FAILURE THIS WINDOW EXISTS TO AVOID: edits vanishing on close. Checked through the real
     * window's OnClosing, which cancels, asks, and closes again -- none of which the view-model tests
     * could reach.
     */
    [AvaloniaFact]
    public async Task ClosingWithUnsavedNotesSavesThemWhenTheUserAgrees()
    {
        var path = Path.Combine(_temp, "instance.cfg");

        File.WriteAllText(path, "name=My Pack\nInstanceType=OneSix\n");

        var settings = new InstanceSettings(
            new IniSettingsObject(path),
            GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        var notes = new NotesPageViewModel();
        notes.Load(settings);

        var viewModel = new InstanceWindowViewModel("My Pack", new AlwaysYes());
        viewModel.AddPage(notes);

        var window = Show(viewModel);

        notes.Text = "Saved on close.";

        window.Close();

        // OnClosing is async: it cancels, awaits the prompt, then closes again.
        await WaitUntilAsync(() => !window.IsVisible).ConfigureAwait(true);

        Assert.Contains("Saved on close.", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>A save that fails keeps the window open, rather than discarding the edits.</summary>
    [AvaloniaFact]
    public async Task AFailedSaveLeavesTheWindowOpen()
    {
        var viewModel = new InstanceWindowViewModel("My Pack", new AlwaysYes());

        viewModel.AddPage(new UnsaveablePage());

        var window = Show(viewModel);

        window.Close();

        await PumpAsync().ConfigureAwait(true);

        Assert.True(window.IsVisible);
        Assert.True(viewModel.HasSaveFailure);
    }

    private sealed class AlwaysYes : IUserPrompts
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
            => Task.FromResult(true);

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult<string?>(null);
    }

    private sealed class UnsaveablePage : IInstancePage
    {
        public string Title => "Version";

        public bool HasUnsavedChanges => true;

        public bool Save() => false;
    }

    /// <summary>Runs the dispatcher until a condition holds, or gives up.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await PumpAsync().ConfigureAwait(true);
        }
    }

    private static async Task PumpAsync()
    {
        Dispatcher.UIThread.RunJobs();

        await Task.Yield();

        Dispatcher.UIThread.RunJobs();
    }

    // ================================================================== the mods page

    /*
     * Rendered, with a real jar on disk. The toggle button's label comes from the view model and says
     * what will HAPPEN, so it must change with the selection -- a button reading "Disable" over a mod
     * that is already off would do the opposite of what it says.
     */
    [AvaloniaFact]
    public void TheModsPageRendersAndItsToggleNamesTheAction()
    {
        var mods = Path.Combine(_temp, "mods");
        Directory.CreateDirectory(mods);

        using (var archive = System.IO.Compression.ZipFile.Open(
                   Path.Combine(mods, "sodium.jar"),
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("nothing.txt");
        }

        var page = new ModsPageViewModel();
        page.Load(_temp);

        var viewModel = new InstanceWindowViewModel("My Pack");
        viewModel.AddPage(page);

        var window = Show(viewModel);

        Assert.Contains("sodium", Texts(window));

        // Nothing selected: the action buttons are dead, and the label defaults to the safe direction.
        Assert.False(Button(window, "Enable").IsEffectivelyEnabled);

        page.Select(page.Mods[0].Path);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // Enabled mod selected, so the button now offers to turn it off.
        var toggle = Button(window, "Disable");

        Assert.True(toggle.IsEffectivelyEnabled);
        Assert.NotNull(toggle.Command);

        Assert.NotNull(Button(window, "Remove").Command);
        Assert.NotNull(Button(window, "Refresh").Command);
    }

    /// <summary>Pressing the toggle really does rename the file, through the rendered button.</summary>
    [AvaloniaFact]
    public void PressingDisableRenamesTheJar()
    {
        var mods = Path.Combine(_temp, "mods");
        Directory.CreateDirectory(mods);

        using (var archive = System.IO.Compression.ZipFile.Open(
                   Path.Combine(mods, "sodium.jar"),
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("nothing.txt");
        }

        var page = new ModsPageViewModel();
        page.Load(_temp);

        var viewModel = new InstanceWindowViewModel("My Pack");
        viewModel.AddPage(page);

        var window = Show(viewModel);

        page.Select(page.Mods[0].Path);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var toggle = Button(window, "Disable");
        toggle.Command!.Execute(toggle.CommandParameter);

        Dispatcher.UIThread.RunJobs();

        Assert.False(File.Exists(Path.Combine(mods, "sodium.jar")));
        Assert.True(File.Exists(Path.Combine(mods, "sodium.jar.disabled")));
    }

    // ================================================================== the worlds page

    /*
     * The worlds page rendered, with a real save on disk. What is checked beyond "it appears" is that
     * Rename is DISABLED for a save that will not parse while Delete stays live -- the name lives
     * inside level.dat, so renaming means rewriting a file the launcher just failed to read, but
     * clearing the folder up is the main reason to be here.
     */
    [AvaloniaFact]
    public void TheWorldsPageRendersAndOffersDeleteForAnUnreadableSave()
    {
        var saves = Path.Combine(_temp, "saves", "corrupted");

        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "level.dat"), "not nbt at all");

        var page = new WorldsPageViewModel();
        page.Load(_temp);

        var viewModel = new InstanceWindowViewModel("My Pack");
        viewModel.AddPage(page);

        var window = Show(viewModel);

        Assert.Contains("corrupted", Texts(window));
        Assert.Contains("unreadable", Texts(window));

        page.Select(page.Worlds[0].Path);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.False(Button(window, "Rename").IsEffectivelyEnabled);
        Assert.True(Button(window, "Delete").IsEffectivelyEnabled);
        Assert.NotNull(Button(window, "Delete").Command);
    }

    // ================================================================== the servers page

    private ServersPageViewModel ServersPageWith(params (string Name, string Address)[] servers)
    {
        var root = Path.Combine(_temp, "game-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        ExtremeLauncher.Minecraft.ServerList.Save(
            root,
            servers.Select(s => new ExtremeLauncher.Minecraft.MinecraftServer { Name = s.Name, Address = s.Address }));

        var page = new ServersPageViewModel();

        page.Load(root);

        return page;
    }

    private (InstanceWindow Window, ServersPageViewModel Page) ShowServers(
        params (string Name, string Address)[] servers)
    {
        var page = ServersPageWith(servers);

        var viewModel = new InstanceWindowViewModel("My Pack");

        viewModel.AddPage(page);

        var window = Show(viewModel);

        viewModel.SelectedPage = page;

        Flush(window);

        return (window, page);
    }

    [AvaloniaFact]
    public void TheServerEditorAppearsOnlyOnceARowIsSelected()
    {
        // Otherwise the page opens with three empty boxes above the list, which reads as "fill this
        // in" rather than as "pick a server".
        var (window, page) = ShowServers(("Home", "home.example.invalid"));

        // Asserted on visibility, not existence: a collapsed panel still builds its children, so
        // "the box is not in the tree" fails for a window that is behaving correctly.
        var box = window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "ServerName");

        Assert.False(box.IsEffectivelyVisible);

        page.Select("home.example.invalid");

        Flush(window);

        Assert.Equal(
            "Home",
            window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "ServerName").Text);
    }

    [AvaloniaFact]
    public void TypingInTheEditorReachesTheRowInTheList()
    {
        // The full binding path: control -> row view model -> the list the page will save.
        var (window, page) = ShowServers(("Home", "home.example.invalid"));

        page.Select("home.example.invalid");

        Flush(window);

        window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "ServerAddress").Text
            = "moved.example.invalid";

        Flush(window);

        Assert.Equal("moved.example.invalid", page.Servers[0].Address);
        Assert.True(page.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void ARunningGameGreysOutTheWholeServerEditor()
    {
        /*
         * The one that matters. The game rewrites servers.dat when it exits, so an edit made while it
         * is up is silently replaced. The view model refuses to save -- this asserts the user is told
         * BEFORE typing rather than after.
         */
        var (window, page) = ShowServers(("Home", "home.example.invalid"));

        page.Select("home.example.invalid");

        Flush(window);

        var name = window.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "ServerName");

        Assert.True(name.IsEffectivelyEnabled);

        page.IsLocked = true;

        Flush(window);

        Assert.False(name.IsEffectivelyEnabled);
        Assert.False(Button(window, "Add").IsEffectivelyEnabled);
        Assert.False(Button(window, "Remove").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void TheReasonThePageIsLockedIsOnScreen()
    {
        var (window, page) = ShowServers(("Home", "a"));

        page.IsLocked = true;

        Flush(window);

        Assert.Contains(Texts(window), t => t is not null && t.Contains("game is running", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void TheEditingButtonsAreRealButtonsNow()
    {
        /*
         * They were placeholders: IsEnabled="False" with a tooltip saying this launcher could not
         * edit servers.dat. A test that only checked they existed would still pass.
         */
        var (window, page) = ShowServers(("Home", "a"));

        page.Select("a");

        Flush(window);

        foreach (var label in new[] { "Add", "Remove", "Move up", "Move down", "Refresh" })
        {
            Assert.NotNull(Button(window, label).Command);
        }

        Assert.True(Button(window, "Add").IsEffectivelyEnabled);
        Assert.True(Button(window, "Remove").IsEffectivelyEnabled);
    }

    /// <summary>Every page kind resolves its own template rather than falling back to a type name.</summary>
    [AvaloniaFact]
    public void EveryPageKindHasATemplate()
    {
        var viewModel = new InstanceWindowViewModel("My Pack");

        viewModel.AddPage(VersionPage(Make("net.minecraft", "Minecraft")));
        viewModel.AddPage(new ModsPageViewModel());
        viewModel.AddPage(new WorldsPageViewModel());
        viewModel.AddPage(new ModsPageViewModel(ResourceFolderKind.ResourcePacks));
        viewModel.AddPage(new ModsPageViewModel(ResourceFolderKind.ShaderPacks));
        viewModel.AddPage(new ScreenshotsPageViewModel());
        viewModel.AddPage(new LogPageViewModel(new LaunchCoordinator(new NeverLaunches())));
        viewModel.AddPage(new OtherLogsPageViewModel());
        viewModel.AddPage(new ServersPageViewModel());
        viewModel.AddPage(SettingsPage());
        viewModel.AddPage(new NotesPageViewModel());

        var window = Show(viewModel);

        // All six are listed by name...
        var texts = Texts(window);

        foreach (var title in new[]
                 {
                     "Version", "Mods", "Worlds", "Resource packs", "Shader packs", "Screenshots",
                     "Log", "Other logs", "Servers", "Settings", "Notes",
                 })
        {
            Assert.Contains(title, texts);
        }

        // ...and each one renders as a page rather than as the fallback a missing template produces.
        foreach (var page in viewModel.Pages)
        {
            viewModel.SelectedPage = page;

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.DoesNotContain(
                Texts(window),
                t => t?.Contains("ViewModel", StringComparison.Ordinal) ?? false);
        }
    }

    // ================================================================== the settings page

    private SettingsPageViewModel SettingsPage()
    {
        var cfg = Path.Combine(_temp, "instance.cfg");

        if (!File.Exists(cfg))
        {
            File.WriteAllText(cfg, "name=My Pack" + Environment.NewLine + "InstanceType=OneSix" + Environment.NewLine);
        }

        var page = new SettingsPageViewModel();

        page.Load(new InstanceSettings(
            new IniSettingsObject(cfg),
            GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg"))));

        return page;
    }

    /*
     * THE FIELDS FOLLOW THE GROUP'S TICK BOX. An inheriting group shows the values the instance is
     * actually running with, and shows them as not editable -- so nobody types into a box whose value
     * the launcher will ignore. That is the whole shape of this page, and it is a binding, so only a
     * rendered window can confirm it.
     */
    [AvaloniaFact]
    public void SettingsFieldsAreOnlyEditableWhileTheGroupOverrides()
    {
        var page = SettingsPage();

        var viewModel = new InstanceWindowViewModel("My Pack");
        viewModel.AddPage(page);

        var window = Show(viewModel);

        Assert.Contains("Memory", Texts(window));
        Assert.Contains("Java path", Texts(window));

        var numbers = window.GetLogicalDescendants().OfType<NumericUpDown>().ToList();

        Assert.NotEmpty(numbers);
        Assert.All(numbers, n => Assert.False(n.IsEffectivelyEnabled));

        foreach (var group in page.Groups)
        {
            group.IsOverriding = true;
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.All(
            window.GetLogicalDescendants().OfType<NumericUpDown>(),
            n => Assert.True(n.IsEffectivelyEnabled));
    }

    // ================================================================== the window as a launch window

    private sealed class NeverLaunches : IInstanceLauncher
    {
        public Task LaunchAsync(string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
            => Task.CompletedTask;
    }

    private sealed class HeldLauncher : IInstanceLauncher
    {
        private readonly TaskCompletionSource _release = new();

        public TaskCompletionSource Started { get; } = new();

        public void Release() => _release.TrySetResult();

        public async Task LaunchAsync(string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
        {
            progress.Log($"Starting {instanceId}");
            Started.TrySetResult();

            await _release.Task.ConfigureAwait(false);
        }
    }

    /*
     * PRESSING LAUNCH IN THE RENDERED WINDOW jumps to the log page and shows the output there. This is
     * upstream's runningStateChanged, and it is the whole reason the instance window is also the launch
     * window -- checked here through the real buttons rather than the view model's properties.
     */
    [AvaloniaFact]
    public async Task PressingLaunchShowsTheLog()
    {
        var launcher = new HeldLauncher();
        var coordinator = new LaunchCoordinator(launcher);

        var viewModel = new InstanceWindowViewModel("My Pack", launch: coordinator) { InstanceId = "MyPack" };

        viewModel.AddPage(VersionPage(Make("net.minecraft", "Minecraft")));
        viewModel.AddPage(new LogPageViewModel(coordinator));

        var window = Show(viewModel);

        var launch = Button(window, "Launch");
        var kill = Button(window, "Kill");

        Assert.True(launch.IsEffectivelyEnabled);
        Assert.False(kill.IsEffectivelyEnabled);

        launch.Command!.Execute(launch.CommandParameter);

        await launcher.Started.Task.ConfigureAwait(true);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // The log page is showing, and the line the launcher wrote is on screen.
        Assert.IsType<LogPageViewModel>(viewModel.SelectedPage);
        Assert.Contains("Starting MyPack", Texts(window));

        // Launch is now dead and Kill is live.
        Assert.False(Button(window, "Launch").IsEffectivelyEnabled);
        Assert.True(Button(window, "Kill").IsEffectivelyEnabled);

        launcher.Release();

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheLaunchAndKillButtonsHaveCommands()
    {
        var viewModel = new InstanceWindowViewModel(
            "My Pack",
            launch: new LaunchCoordinator(new NeverLaunches()));

        viewModel.AddPage(new NotesPageViewModel());

        var window = Show(viewModel);

        Assert.NotNull(Button(window, "Launch").Command);
        Assert.NotNull(Button(window, "Kill").Command);
    }
}
