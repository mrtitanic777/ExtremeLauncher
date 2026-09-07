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
 * The code-behind in this window, which exists for two reasons.
 *
 * FIRST: CLOSING HAS TO BE ASKED ABOUT.
 *
 * A window cannot be stopped from closing through a binding -- the decision is an event that has to be
 * cancelled, and the answer arrives asynchronously from a dialog. So the first close is cancelled, the
 * question is asked, and the window is closed again for real if the answer allows it.
 *
 * SECOND: DROPPING FILES IS AN EVENT, not a binding. Avalonia delivers a drop through DragOver and
 * Drop handlers on the control, and the payload is a platform object -- neither of which belongs in a
 * view model that has to run headlessly. What arrives here is turned into a list of paths and handed
 * straight over.
 *
 * Everything else about this window is in InstanceWindowViewModel, where it is tested.
 */

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class InstanceWindow : Window
{
    /// <summary>Set once the view model has agreed, so the second close is not questioned again.</summary>
    private bool _closingApproved;

    public InstanceWindow() => InitializeComponent();

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        /*
         * On the WINDOW, not on the mods page. A file dropped anywhere in the window is meant for the
         * instance, and making somebody hit a particular pane with the mouse is a precision the drop
         * does not need -- the file itself decides which folder it lands in.
         */
        DragDrop.SetAllowDrop(this, true);

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // The custom title bar is our own control, so it starts the window move itself (and
    // maximises/restores on a double click), the way the OS caption would have.
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not InstanceWindowViewModel window)
        {
            return;
        }

        var paths = e.Data.GetFiles()?
            .Select(f => f.Path.IsFile ? f.Path.LocalPath : null)
            .OfType<string>()
            .ToArray() ?? [];

        if (paths.Length == 0)
        {
            return;
        }

        /*
         * Handed to the MODS page whatever page is showing. Every kind of dropped resource is filed by
         * what it is rather than where it landed, and the mods page is simply the one that owns the
         * importer -- see ModsPageViewModel.DropFilesAsync.
         */
        var target = window.Pages.OfType<ModsPageViewModel>()
            .FirstOrDefault(p => p.Kind == ResourceFolderKind.Mods && p.AcceptsDrops);

        if (target is null)
        {
            return;
        }

        window.SelectPage(target);

        await target.DropFilesAsync(paths).ConfigureAwait(true);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_closingApproved || DataContext is not InstanceWindowViewModel viewModel)
        {
            return;
        }

        /*
         * Cancelled unconditionally first. The answer comes from a dialog, and by the time it arrives
         * this method has long returned -- so there is no way to answer "should I close?" in time. The
         * window is closed again below if the view model allows it.
         */
        e.Cancel = true;

        if (!await viewModel.RequestCloseAsync().ConfigureAwait(true))
        {
            // Refused: unsaved edits that could not be written. Staying open is the whole point.
            return;
        }

        _closingApproved = true;

        /*
         * Released once the answer is in and the window is really going: the log page holds a
         * filesystem watcher, and a launcher left open all day would otherwise accumulate one per
         * instance window ever opened.
         */
        viewModel.DisposePages();

        Close();
    }
}
