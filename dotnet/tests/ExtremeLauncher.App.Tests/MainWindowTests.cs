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
 * THE WINDOW, ACTUALLY RENDERED. Everything checked here was previously beyond reach: the view models
 * were tested and the XAML was compiled, but nothing had ever confirmed the two were connected.
 *
 * The specific failure this catches is the one this port has already shipped once -- a button with an
 * IsEnabled binding and NO Command, which lights up and does nothing when clicked. That was invisible
 * to the build, to every unit test, and to me until a screenshot arrived.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-ui-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public MainWindowTests()
    {
        _instances = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(_instances);
    }

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

    private void MakeInstance(string id, string type = "OneSix")
    {
        var path = Path.Combine(_instances, id);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "instance.cfg"), $"name={id}\nInstanceType={type}\n");
    }

    private MainWindowViewModel LoadViewModel(IInstanceEditor? editor = null)
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        var viewModel = new MainWindowViewModel(editor: editor);
        viewModel.Instances.Load(list);

        return viewModel;
    }

    /// <summary>Finds a button by the text on it, which is what a user identifies it by.</summary>
    private static Button Button(Visual root, string content)
        => root.GetLogicalDescendants()
            .OfType<Button>()
            .Single(b => b.Content as string == content);

    private MainWindow Show(MainWindowViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel };

        _windows.Add(window);

        window.Show();

        return window;
    }

    /*
     * Realises the visual tree after the view model changes. Bindings post their updates and item
     * containers are created during a layout pass, so a test that mutates the model and looks straight
     * at the controls is reading the tree from before the change.
     */
    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();

        window.UpdateLayout();
    }

    // ================================================================== the news button

    [AvaloniaFact]
    public void WithNoFeedTheNewsButtonIsNotOnTheToolbarAtAll()
    {
        /*
         * Hidden rather than disabled. A permanently greyed-out "No news available." occupies the
         * same space as news and tells nobody anything, and a fork that leaves the feed URL empty
         * should get a toolbar with no news on it -- which is the state every other test in this
         * file runs in, since none of them pass a checker.
         */
        var window = Show(LoadViewModel());

        Flush(window);

        // Asserted on IsVisible, not on absence: a collapsed control is still in the logical tree,
        // so "the button does not exist" would fail for a window that is behaving correctly.
        var button = window.GetLogicalDescendants()
            .OfType<Button>()
            .Single(b => b.Content as string == "No news available.");

        Assert.False(button.IsVisible);
    }

    [AvaloniaFact]
    public void TheHeadlineIsTheButtonAndItFollowsTheFeed()
    {
        /*
         * Upstream puts the newest post's title on the toolbar rather than the word "News", and the
         * title arrives long after the window opens. So this is the binding AND the announcement: a
         * label that never updates is what a missing PropertyChanged looks like from out here.
         */
        var checker = new NewsChecker(
            new HttpClient(new CannedFeed()),
            "https://example.invalid/news.xml");

        var viewModel = new MainWindowViewModel(news: new NoWindow(), newsChecker: checker);

        var window = Show(viewModel);

        Flush(window);

        var button = window.GetLogicalDescendants()
            .OfType<Button>()
            .Single(b => b.Content as string == "No news available.");

        Assert.False(button.IsEnabled);

        viewModel.News.LoadAsync().GetAwaiter().GetResult();

        Flush(window);

        Assert.Equal("Newest post", button.Content);
        Assert.True(button.IsEnabled);
    }

    private sealed class CannedFeed : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <feed xmlns="http://www.w3.org/2005/Atom">
                      <entry><title>Newest post</title><id>https://example.invalid/1</id></entry>
                    </feed>
                    """),
            });
    }

    private sealed class NoWindow : INewsUi
    {
        public Task ShowAsync(IReadOnlyList<ExtremeLauncher.Core.NewsEntry> entries, bool startWithListHidden)
            => Task.CompletedTask;
    }

    // ================================================================== it renders at all

    [AvaloniaFact]
    public void TheWindowOpensAndListsTheInstances()
    {
        MakeInstance("Alpha");
        MakeInstance("Beta");

        var window = Show(LoadViewModel());

        var names = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .ToList();

        Assert.Contains("Alpha", names);
        Assert.Contains("Beta", names);
    }

    [AvaloniaFact]
    public void TheTitleFollowsTheSelection()
    {
        MakeInstance("Alpha");

        var viewModel = LoadViewModel();

        var window = Show(viewModel);

        Assert.Equal("Extreme Launcher", window.Title);

        viewModel.Select("Alpha");

        Assert.Equal("Extreme Launcher - Alpha", window.Title);
    }

    /*
     * An instance the launcher cannot start is still listed and SAYS SO. Hiding it would look to the
     * user like it had been deleted.
     */
    [AvaloniaFact]
    public void AnUnsupportedInstanceIsShownWithItsWarning()
    {
        MakeInstance("Broken", type: "Nonsense");

        var window = Show(LoadViewModel());

        Assert.Contains(
            window.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.Text == "unsupported" && t.IsVisible);
    }

    // ================================================================== every button is wired

    /*
     * THE BUG THIS PROJECT EXISTS FOR. Edit, Copy and Delete once had IsEnabled bindings and no Command
     * at all: they lit up on selection and did nothing when clicked. The build was happy, every unit
     * test passed, and it took a screenshot to find.
     *
     * A null Command is now a failing test.
     */
    [AvaloniaTheory]
    [InlineData("New")]
    [InlineData("Import")]
    [InlineData("Launch")]
    [InlineData("Edit")]
    [InlineData("Copy")]
    [InlineData("Delete")]
    public void EveryToolbarButtonHasACommandBehindIt(string label)
    {
        MakeInstance("Alpha");

        var window = Show(LoadViewModel());

        Assert.NotNull(Button(window, label).Command);
    }

    /*
     * The enabled states come from the view model, so the toolbar cannot disagree with the selection.
     * Checked against the RENDERED buttons rather than the properties, which is the half that was
     * never verified.
     */
    [AvaloniaFact]
    public void TheToolbarFollowsTheSelection()
    {
        MakeInstance("Alpha");

        var viewModel = LoadViewModel();

        var window = Show(viewModel);

        Assert.False(Button(window, "Delete").IsEffectivelyEnabled);
        Assert.False(Button(window, "Copy").IsEffectivelyEnabled);

        viewModel.Select("Alpha");

        Assert.True(Button(window, "Delete").IsEffectivelyEnabled);
        Assert.True(Button(window, "Copy").IsEffectivelyEnabled);
    }

    /// <summary>An unsupported instance can be edited and deleted, but not launched.</summary>
    [AvaloniaFact]
    public void AnUnsupportedInstanceCannotBeLaunchedButCanBeTidiedUp()
    {
        MakeInstance("Broken", type: "Nonsense");

        var viewModel = LoadViewModel();

        var window = Show(viewModel);

        viewModel.Select("Broken");

        Assert.False(Button(window, "Launch").IsEffectivelyEnabled);
        Assert.True(Button(window, "Delete").IsEffectivelyEnabled);
    }

    /*
     * WITH NO EDITOR SUPPLIED, Edit is disabled rather than live-and-useless. That is the whole reason
     * CanOpenEditor exists, and without a rendered window there was no way to check it.
     */
    [AvaloniaFact]
    public void EditIsDisabledInABuildWithNoInstanceWindow()
    {
        MakeInstance("Alpha");

        var viewModel = LoadViewModel(editor: null);

        var window = Show(viewModel);

        viewModel.Select("Alpha");

        Assert.False(Button(window, "Edit").IsEffectivelyEnabled);
    }

    private sealed class RecordingEditor : IInstanceEditor
    {
        public List<string> Opened { get; } = [];

        public void Open(InstanceRecord instance) => Opened.Add(instance.Id);
    }

    [AvaloniaFact]
    public void EditOpensTheSelectedInstance()
    {
        MakeInstance("Alpha");

        var editor = new RecordingEditor();
        var viewModel = LoadViewModel(editor);

        var window = Show(viewModel);

        viewModel.Select("Alpha");

        var edit = Button(window, "Edit");

        Assert.True(edit.IsEffectivelyEnabled);

        // Invoked the way the button does, so a command bound to the wrong thing would show up here.
        edit.Command!.Execute(edit.CommandParameter);

        Assert.Equal(["Alpha"], editor.Opened);
    }

    // ================================================================== the status strips

    /// <summary>The launch strip is hidden until something is happening, so no empty bar sits there.</summary>
    [AvaloniaFact]
    public void TheStatusStripsAreHiddenWhenThereIsNothingToSay()
    {
        MakeInstance("Alpha");

        var viewModel = LoadViewModel();

        var window = Show(viewModel);

        Assert.DoesNotContain(
            window.GetLogicalDescendants().OfType<ProgressBar>(),
            bar => bar.IsVisible && bar.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void AFailedLaunchIsShownInTheWindow()
    {
        MakeInstance("Alpha");

        var viewModel = LoadViewModel();

        var window = Show(viewModel);

        viewModel.Select("Alpha");

        // No launcher was supplied, so this fails with the "not wired up" message by design.
        viewModel.LaunchSelectedCommand.Execute(null);

        Assert.Contains(
            window.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && (t.Text?.Contains("not wired up", StringComparison.Ordinal) ?? false));
    }

    // ================================================================== searching

    [AvaloniaFact]
    public void SearchingFiltersTheRenderedList()
    {
        MakeInstance("Alpha");
        MakeInstance("Beta");

        var viewModel = LoadViewModel();

        var window = Show(viewModel);

        viewModel.Instances.SearchText = "Alph";

        // The view model filtered; the question is whether the window followed.
        Assert.Equal(["Alpha"], viewModel.Instances.Groups.SelectMany(g => g.Instances).Select(i => i.Name));

        Flush(window);

        var names = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Alpha", names);
        Assert.DoesNotContain("Beta", names);
    }

    /// <summary>"Nothing here" and "nothing matched" are different things to tell a user.</summary>
    [AvaloniaFact]
    public void ASearchThatMatchesNothingSaysSo()
    {
        MakeInstance("Alpha");

        var viewModel = LoadViewModel();

        var window = Show(viewModel);

        viewModel.Instances.SearchText = "zzzz";

        Flush(window);

        Assert.Contains(
            window.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible
                 && (t.Text?.Contains("No instances match", StringComparison.Ordinal) ?? false));
    }

    // ================================================================== creating an instance

    private sealed class RecordingCreator(string id = "Made") : IInstanceCreator
    {
        public int Calls { get; private set; }

        public Task<string> CreateAsync(InstanceList list)
        {
            Calls++;

            // A real one appears on disk; this stands in for the dialog having done so.
            return Task.FromResult(id);
        }
    }

    private MainWindowViewModel LoadViewModel(IInstanceCreator creator)
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        var viewModel = new MainWindowViewModel(creator: creator);
        viewModel.Instances.Load(list);

        return viewModel;
    }

    /*
     * NEW IS THE ONE BUTTON THAT WORKS WITH NOTHING SELECTED -- and with nothing in the list at all,
     * which is exactly the state a new user is in. Every other button here needs a selection, so this
     * is the one most likely to be wired like the others by mistake.
     */
    [AvaloniaFact]
    public void NewIsEnabledWithAnEmptyInstanceList()
    {
        var viewModel = LoadViewModel(new RecordingCreator());

        var window = Show(viewModel);

        Assert.Empty(viewModel.Instances.Groups);
        Assert.True(Button(window, "New").IsEffectivelyEnabled);

        // ...while the ones that act on a selection are not.
        Assert.False(Button(window, "Delete").IsEffectivelyEnabled);
    }

    /// <summary>With no creator supplied, New is disabled rather than live-and-useless.</summary>
    [AvaloniaFact]
    public void NewIsDisabledInABuildThatCannotCreate()
    {
        var window = Show(LoadViewModel());

        Assert.False(Button(window, "New").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task PressingNewAsksTheCreator()
    {
        var creator = new RecordingCreator();
        var viewModel = LoadViewModel(creator);

        var window = Show(viewModel);

        var button = Button(window, "New");

        Assert.NotNull(button.Command);

        await viewModel.NewInstanceAsync().ConfigureAwait(true);

        Assert.Equal(1, creator.Calls);
    }

    // ================================================================== importing a pack

    private sealed class RecordingImporter(string id = "Imported") : IPackImporter
    {
        public int Calls { get; private set; }

        public Task<string> ImportAsync(InstanceList list)
        {
            Calls++;

            return Task.FromResult(id);
        }
    }

    private MainWindowViewModel LoadViewModel(IPackImporter importer)
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        var viewModel = new MainWindowViewModel(importer: importer);
        viewModel.Instances.Load(list);

        return viewModel;
    }

    /*
     * Import needs no selection either: it is the other way somebody gets their first instance, and
     * quite often the only way they ever make one.
     */
    [AvaloniaFact]
    public void ImportIsEnabledWithAnEmptyInstanceList()
    {
        var window = Show(LoadViewModel(new RecordingImporter()));

        Assert.True(Button(window, "Import").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void ImportIsDisabledInABuildThatCannotImport()
    {
        var window = Show(LoadViewModel());

        Assert.False(Button(window, "Import").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task PressingImportAsksTheImporter()
    {
        var importer = new RecordingImporter();
        var viewModel = LoadViewModel(importer);

        var window = Show(viewModel);

        Assert.NotNull(Button(window, "Import").Command);

        await viewModel.ImportPackAsync().ConfigureAwait(true);

        Assert.Equal(1, importer.Calls);
    }
}
