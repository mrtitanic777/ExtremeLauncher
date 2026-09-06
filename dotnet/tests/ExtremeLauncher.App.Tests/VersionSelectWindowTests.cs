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
 * The version picker through its real visual tree, for what the view model tests cannot see: whether
 * OK and Cancel are wired to anything, and whether the chosen version actually comes back out.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ExtremeLauncher.Meta;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class VersionSelectWindowTests : IDisposable
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

    private sealed class StubSource(params MetaVersion[] versions) : IVersionListSource
    {
        public Task<IReadOnlyList<MetaVersion>> LoadAsync(string uid, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetaVersion>>(versions);
    }

    private static MetaVersion V(string version, string type = "release")
        => new("net.minecraft", version) { Type = type };

    private (VersionSelectWindow Window, VersionSelectViewModel Model) Show(params MetaVersion[] versions)
    {
        var model = new VersionSelectViewModel(
            "net.minecraft",
            "Change Minecraft version",
            new StubSource(versions));

        var window = new VersionSelectWindow(model);

        _windows.Add(window);

        window.Show();

        // The load is started from Opened, so the list is not there until the queue drains.
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        return (window, model);
    }

    private static Button Button(Visual root, string content)
        => root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content);

    [AvaloniaFact]
    public void TheListFillsInWhenTheWindowOpens()
    {
        // The fetch is kicked off by the window itself, not by the caller.
        var (window, model) = Show(V("1.20.1"), V("1.20.4"));

        Assert.Equal(2, model.Versions.Count);

        var list = window.GetLogicalDescendants().OfType<ListBox>().Single();

        Assert.Equal(2, list.ItemsSource!.Cast<object>().Count());
    }

    [AvaloniaFact]
    public void ChoosingAVersionAndPressingOkReturnsIt()
    {
        var (window, model) = Show(V("1.20.1"), V("1.20.4"));

        var list = window.GetLogicalDescendants().OfType<ListBox>().Single();

        // Through the control, so the two-way binding is what is under test.
        list.SelectedIndex = 1;

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal("1.20.4", model.Selected?.Version);

        Button(window, "OK").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert.Equal("1.20.4", window.ChosenVersion);
    }

    [AvaloniaFact]
    public void CancellingReturnsNothingSoTheCallerChangesNothing()
    {
        var (window, _) = Show(V("1.20.1"));

        Button(window, "Cancel").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        // An empty answer is exactly what VersionPageViewModel treats as "leave it alone".
        Assert.Equal(string.Empty, window.ChosenVersion);
    }

    [AvaloniaFact]
    public void OkIsDisabledWhileThereIsNothingToChoose()
    {
        var (window, model) = Show();

        Assert.Empty(model.Versions);
        Assert.False(Button(window, "OK").IsEnabled);
    }

    [AvaloniaFact]
    public void CancelIsTheDefaultSoReturnDoesNotChangeAnInstancesVersion()
    {
        var (window, _) = Show(V("1.20.1"));

        Assert.True(Button(window, "Cancel").IsDefault);
        Assert.False(Button(window, "OK").IsDefault);
    }
}
