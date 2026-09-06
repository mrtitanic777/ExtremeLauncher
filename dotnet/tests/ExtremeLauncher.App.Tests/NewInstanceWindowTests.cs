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
 * THE DIALOG A NEW USER MEETS FIRST, and until now nobody had looked at it. Its rules are tested in
 * NewInstanceViewModelTests and verified against live metadata; what is unverified is whether the
 * window is connected to any of that.
 *
 * The specific thing worth rendering is the loader row: a combo box that APPEARS AND DISAPPEARS with
 * the loader choice, which is a binding and therefore exactly the sort of thing the build cannot check.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class NewInstanceWindowTests : IDisposable
{
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
                // Already gone.
            }
        }
    }

    private static MetaVersion Version(string version, string type, int year, bool recommended = false)
        => new("net.minecraft", version)
        {
            Type = type,
            IsRecommended = recommended,
            RawTime = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        };

    private sealed class FakeVersions(params MetaVersion[] versions) : IVersionListSource
    {
        public Dictionary<string, MetaVersion[]> ByUid { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<MetaVersion>> LoadAsync(string uid, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetaVersion>>(
                ByUid.TryGetValue(uid, out var forUid) ? forUid : versions);
    }

    private NewInstanceWindow Show(NewInstanceViewModel viewModel)
    {
        var window = new NewInstanceWindow { DataContext = viewModel };

        _windows.Add(window);

        window.Show();

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        return window;
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();

        window.UpdateLayout();
    }

    private static Button Button(Visual root, string content)
        => root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content);

    private static IReadOnlyList<string?> Texts(Visual root)
        => root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    private static NewInstanceViewModel Model(FakeVersions versions)
        => new(
            versions,
            new RuntimeContext { System = "windows", JavaArchitecture = "64", JavaRealArchitecture = "x86_64" });

    // ================================================================== it renders

    [AvaloniaFact]
    public async Task TheDialogListsTheVersionsItFetched()
    {
        var model = Model(new FakeVersions(
            Version("1.20.1", "release", 2023, recommended: true),
            Version("1.16.5", "release", 2021)));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        var window = Show(model);

        var texts = Texts(window);

        Assert.Contains("1.20.1", texts);
        Assert.Contains("1.16.5", texts);
    }

    /*
     * CREATE IS DISABLED UNTIL THERE IS A NAME. The version is pre-selected, so the name is the half a
     * user has to supply -- and a disabled button with no explanation is the thing this port keeps
     * removing, so it must at least be disabled for a reason that becomes true as they type.
     */
    [AvaloniaFact]
    public async Task CreateOnlyBecomesAvailableOnceThereIsAName()
    {
        var model = Model(new FakeVersions(Version("1.20.1", "release", 2023, recommended: true)));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        var window = Show(model);

        Assert.False(Button(window, "Create").IsEffectivelyEnabled);

        model.Name = "My Pack";

        Flush(window);

        Assert.True(Button(window, "Create").IsEffectivelyEnabled);
    }

    /// <summary>Cancel is the default, so Return never commits a directory.</summary>
    [AvaloniaFact]
    public async Task CancelIsTheDefaultButton()
    {
        var model = Model(new FakeVersions(Version("1.20.1", "release", 2023, recommended: true)));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        var window = Show(model);

        Assert.True(Button(window, "Cancel").IsDefault);
        Assert.False(Button(window, "Create").IsDefault);
    }

    [AvaloniaFact]
    public async Task TypingInTheNameBoxReachesTheViewModel()
    {
        var model = Model(new FakeVersions(Version("1.20.1", "release", 2023, recommended: true)));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        var window = Show(model);

        // The first text box is the name; a one-way binding would fail here.
        var box = window.GetLogicalDescendants().OfType<TextBox>().First();

        box.Text = "Typed In";

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Typed In", model.Name);
    }

    // ================================================================== the loader row

    [AvaloniaFact]
    public async Task EveryLoaderIsOffered()
    {
        var model = Model(new FakeVersions(Version("1.20.1", "release", 2023, recommended: true)));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        var window = Show(model);

        var loaders = window.GetLogicalDescendants()
            .OfType<ComboBox>()
            .First()
            .ItemsSource!
            .Cast<LoaderOption>()
            .Select(l => l.Name)
            .ToList();

        Assert.Equal(["None", "Fabric", "Quilt", "Forge", "NeoForge"], loaders);
    }

    /*
     * THE LOADER VERSION BOX APPEARS WITH THE CHOICE. It is a binding on IsVisible, which the build
     * cannot check -- and a version box shown for "None" would ask a question with no answers, while
     * one hidden for Fabric would leave Create disabled with nothing on screen to explain why.
     */
    [AvaloniaFact]
    public async Task TheLoaderVersionBoxFollowsTheLoaderChoice()
    {
        var versions = new FakeVersions(Version("1.20.1", "release", 2023, recommended: true));

        versions.ByUid["net.fabricmc.fabric-loader"] =
        [
            new MetaVersion("net.fabricmc.fabric-loader", "0.15.7")
            {
                Type = "release",
                IsRecommended = true,
                RawTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            },
        ];

        var model = Model(versions);

        await model.LoadVersionsAsync().ConfigureAwait(true);

        var window = Show(model);

        // "None" to begin with: one combo box for the loader, and no second one for its version.
        Assert.Single(window.GetLogicalDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);

        model.Loader = LoaderOption.All.Single(l => l.Name == "Fabric");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        Flush(window);

        var visible = window.GetLogicalDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible).ToList();

        Assert.Equal(2, visible.Count);

        /*
         * Asserted on the SELECTION rather than on rendered text: a ComboBox only realises its items
         * when the dropdown is opened, so the version never appears as a TextBlock in a closed one. My
         * first version of this looked for "0.15.7" in the window's text and failed against a perfectly
         * correct dialog.
         */
        Assert.Equal("0.15.7", ((VersionViewModel)visible[1].SelectedItem!).Version);
    }

    /// <summary>The note explaining an empty loader list is rendered, not just held.</summary>
    [AvaloniaFact]
    public async Task TheReasonAnEmptyLoaderListIsEmptyIsShown()
    {
        var versions = new FakeVersions(
            Version("1.20.1", "release", 2023, recommended: true),
            Version("1.12.2", "release", 2017));

        versions.ByUid["net.fabricmc.fabric-loader"] =
        [
            new MetaVersion("net.fabricmc.fabric-loader", "0.15.7") { Type = "release" },
        ];

        var model = Model(versions);

        await model.LoadVersionsAsync().ConfigureAwait(true);

        var window = Show(model);

        model.Loader = LoaderOption.All.Single(l => l.Name == "Fabric");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        model.Select("1.12.2");

        Flush(window);

        Assert.Contains(
            Texts(window),
            t => t?.Contains("does not support Minecraft 1.12.2", StringComparison.Ordinal) ?? false);
    }

    // ================================================================== failures

    /// <summary>A failed fetch is shown in the dialog rather than leaving an unexplained empty list.</summary>
    [AvaloniaFact]
    public void AFetchFailureIsShownInTheWindow()
    {
        var model = new NewInstanceViewModel();

        var window = Show(model);

        model.Error = "Could not fetch the version list: no such host";

        Flush(window);

        Assert.Contains(
            Texts(window),
            t => t?.Contains("no such host", StringComparison.Ordinal) ?? false);
    }
}
