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
 * The mod download dialog through its real visual tree.
 *
 * The view model tests cover the basket and the searching. These cover what they cannot: whether the
 * buttons are wired, whether highlighting a row actually fetches its versions, and whether what the
 * user picked comes back out of the window.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;
using Xunit;

using ResourceProvider = ExtremeLauncher.ModPlatform.ResourceProvider;

namespace ExtremeLauncher.App.Tests;

public sealed class ModDownloadWindowTests : IDisposable
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
            }
        }
    }

    private sealed class StubSearch : IResourceSearch
    {
        public int VersionLoads { get; private set; }

        public bool IsAvailable(ResourceProvider provider) => true;

        public string UnavailableReason(ResourceProvider provider) => string.Empty;

        public Task<IReadOnlyList<IndexedPack>> SearchAsync(
            ResourceProvider provider,
            string query,
            CancellationToken cancellationToken)
        {
            var pack = new IndexedPack { AddonId = "AANobbMI", Name = "Sodium", Description = "Fast." };

            return Task.FromResult<IReadOnlyList<IndexedPack>>([pack]);
        }

        public Task LoadVersionsAsync(IndexedPack pack, CancellationToken cancellationToken)
        {
            VersionLoads++;

            pack.Versions.Clear();
            pack.Versions.Add(new IndexedVersion { Version = "0.5.13", FileName = "sodium.jar" });

            return Task.CompletedTask;
        }
    }

    private (ModDownloadWindow Window, ModDownloadViewModel Model, StubSearch Search) Show()
    {
        var search = new StubSearch();
        var model = new ModDownloadViewModel(search, "1.20.1", "Fabric");
        var window = new ModDownloadWindow(model);

        _windows.Add(window);

        window.Show();

        Settle(window);

        return (window, model, search);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Button Button(Visual root, string content)
        => root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content);

    [AvaloniaFact]
    public void TheFilterIsOnScreenSoTheListDoesNotLookLikeAThinCatalogue()
    {
        var (window, _, _) = Show();

        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();

        Assert.Contains("Showing Fabric mods for Minecraft 1.20.1", texts);
    }

    [AvaloniaFact]
    public async Task SearchingFillsTheList()
    {
        var (window, model, _) = Show();

        model.SearchText = "sodium";

        await model.SearchAsync();

        Settle(window);

        var list = window.GetLogicalDescendants().OfType<ListBox>().Single();

        Assert.Single(list.ItemsSource!.Cast<object>());
    }

    [AvaloniaFact]
    public async Task HighlightingAModFetchesItsVersions()
    {
        /*
         * Fetched when a row is highlighted, NOT for all twenty-five results at once -- which would be
         * twenty-five requests for a list where the user looks at two. The window is what arranges
         * this, so only a window test can see it.
         */
        var (window, model, search) = Show();

        model.SearchText = "sodium";
        await model.SearchAsync();

        Assert.Equal(0, search.VersionLoads);

        model.Highlighted = model.Results[0];

        Settle(window);

        Assert.Equal(1, search.VersionLoads);
        Assert.Single(model.Results[0].Versions);
    }

    [AvaloniaFact]
    public async Task InstallReturnsWhatWasAdded()
    {
        var (window, model, _) = Show();

        model.SearchText = "sodium";
        await model.SearchAsync();

        model.Highlighted = model.Results[0];

        await model.ToggleSelectedAsync();

        Settle(window);

        Button(window, "Install").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert.Single(window.Chosen);
        Assert.Equal("sodium.jar", window.Chosen[0].Version.FileName);
    }

    [AvaloniaFact]
    public void InstallIsDisabledWithAnEmptyBasket()
    {
        var (window, _, _) = Show();

        Assert.False(Button(window, "Install").IsEnabled);
    }

    [AvaloniaFact]
    public async Task CancellingReturnsNothing()
    {
        var (window, model, _) = Show();

        model.SearchText = "sodium";
        await model.SearchAsync();

        model.Highlighted = model.Results[0];
        await model.ToggleSelectedAsync();

        Settle(window);

        Button(window, "Cancel").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        // Nothing is installed even though the basket had something in it.
        Assert.Empty(window.Chosen);
    }
}
