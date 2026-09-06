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
 * Shows the launcher's own log, using the same view model the instance's Other logs page uses.
 */

using Avalonia.Controls;
using Avalonia.Threading;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppLauncherLogViewer(
    Func<Window?> owner,
    LauncherPaths paths,
    IClipboard clipboard,
    ILogUploader? uploader,
    IUserPrompts prompts) : ILauncherLogViewer
{
    public async Task ShowAsync()
    {
        if (owner() is not { } parent)
        {
            return;
        }

        var model = new OtherLogsPageViewModel(
            action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
            clipboard,
            uploader,
            prompts);

        /*
         * POINTED AT <root>/logs RATHER THAN <root>. The view model searches recursively, and the
         * data root contains every instance's game logs as well -- so aiming it at the root would
         * bury five launcher logs under several hundred belonging to Minecraft.
         */
        model.Load(FileSystem.PathCombine(paths.Root, "logs"));

        var window = new LauncherLogWindow(model);

        await window.ShowDialog(parent).ConfigureAwait(true);
    }
}
