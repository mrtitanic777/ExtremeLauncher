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
 * Opening the update dialog and running what it settles on.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppModUpdater(
    Func<Window?> owner,
    HttpClient client,
    Func<(string Minecraft, string Loader)> instanceInfo,
    LauncherLog? log = null) : IModUpdater
{
    public async Task<bool> CheckAsync(string gameRoot)
    {
        var (minecraft, loader) = instanceInfo();

        var window = new ModUpdateWindow(
            new ModUpdateViewModel(new Service(gameRoot, client, minecraft, loader, log), loader));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }

        return window.Applied;
    }

    private sealed class Service(
        string gameRoot,
        HttpClient client,
        string minecraft,
        string loader,
        LauncherLog? log) : IModUpdateService
    {
        public Task<ModUpdateReport> CheckAsync(CancellationToken cancellationToken)
            => new ModUpdateCheck(client).FindAsync(gameRoot, loader, minecraft, cancellationToken);

        public async Task<string> ApplyAsync(
            IReadOnlyList<ModUpdate> updates,
            CancellationToken cancellationToken)
        {
            var task = new ModUpdateTask(updates, gameRoot, client);

            // Task.Run: the downloads and file moves block, and the one thread that must never block
            // is the one drawing the window.
            await Task.Run(() => task.RunAsync(cancellationToken), cancellationToken).ConfigureAwait(true);

            foreach (var failure in task.Failures)
            {
                log?.Warning($"Mod update failed: {failure}");
            }

            log?.Info($"Updated {task.UpdatedCount} mod(s) in {gameRoot}");

            if (task.Failures.Count == 0)
            {
                return task.UpdatedCount == 1
                    ? "Updated 1 mod. The old file was kept alongside it as .old."
                    : $"Updated {task.UpdatedCount} mods. The old files were kept alongside them as .old.";
            }

            /*
             * The failures are NAMED. "3 of 5 updated" leaves somebody guessing which two, and the two
             * that failed are the ones they need to know about.
             */
            return $"Updated {task.UpdatedCount}, and {task.Failures.Count} failed: "
                + string.Join("; ", task.Failures);
        }
    }
}
