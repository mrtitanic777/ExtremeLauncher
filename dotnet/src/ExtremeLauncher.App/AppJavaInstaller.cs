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
 * Opening the Java dialog and running the download it settles on.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppJavaInstaller(
    Func<Window?> owner,
    LauncherPaths paths,
    HttpClient client,
    Func<string> metaUrl,
    LauncherLog? log = null) : IJavaInstallUi
{
    public async Task OpenAsync()
    {
        var window = new JavaInstallWindow(
            new JavaInstallViewModel(new Service(paths, client, metaUrl(), log)));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }

    private sealed class Service(LauncherPaths paths, HttpClient client, string metaUrl, LauncherLog? log)
        : IJavaInstallService
    {
        private readonly JavaRuntimeSource _source = new(paths, client, metaUrl);

        /// <remarks>
        /// Task.Run: the listing walks every version document of four packages, which is a great many
        /// small requests, and the thread drawing the window must not carry them.
        /// </remarks>
        public Task<IReadOnlyList<InstallableJava>> ListAsync(CancellationToken cancellationToken)
            => Task.Run(() => _source.ListAsync(cancellationToken: cancellationToken), cancellationToken);

        public async Task<string> InstallAsync(InstallableJava java, CancellationToken cancellationToken)
        {
            var task = _source.CreateInstallTask(java);

            var ok = await Task.Run(() => task.RunAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            if (!ok)
            {
                log?.Warning($"Java install failed: {task.FailReason}");

                return task.FailReason.Length != 0 ? task.FailReason : "The download failed.";
            }

            var folder = FileSystem.PathCombine(paths.Java, JavaRuntimeSource.FolderNameFor(java));

            log?.Info($"Installed {java.DisplayName} into {folder}");

            /*
             * WHERE IT WENT is the useful half. The next thing somebody does is point an instance at
             * it, and a message that only says "done" leaves them hunting through the data folder.
             */
            return $"Installed {java.DisplayName} into {folder}.";
        }
    }
}
