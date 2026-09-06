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
 * Filling in the components an instance needs but was not told about.
 *
 * THE BUG THIS EXISTS FOR, found by launching a real 1.20.1 instance:
 *
 *     Minecraft: Minecraft is missing requirement org.lwjgl3 3.3.1
 *     libraries: 36        lwjgl libraries on the classpath: 0
 *     Game exited abnormally with code 1.
 *
 * `net.minecraft 1.20.1` REQUIRES `org.lwjgl3`, which carries the entire windowing and input layer.
 * Every instance this launcher created recorded only the components it was told about -- Minecraft,
 * and a loader if one was chosen -- and nothing ever added the rest. The game started, found no
 * LWJGL, and died immediately.
 *
 * WHY THE LAUNCH PATH CANNOT FIX IT. ComponentUpdateTask has two modes, and Launch mode deliberately
 * only REPORTS unmet requirements: upstream's comment is that resolution "must not start changing
 * versions under someone who is trying to play", and that is right. The other mode, Resolution, was
 * never called from anywhere in this port -- so the dependency was reported at every launch and added
 * at none of them.
 *
 * Upstream never hits this because its NewInstanceDialog runs an update in Resolution mode as part of
 * creating the instance. This is that pass, and it belongs at exactly the same moments: when an
 * instance is created, imported, or has its component list edited.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;
using MetaIndex = ExtremeLauncher.Meta.Index;

namespace ExtremeLauncher.Launch;

public static class ComponentResolution
{
    /// <summary>
    /// Adds whatever the chosen components require, and saves the profile.
    /// </summary>
    /// <param name="client">Null skips the pass: resolving needs the metadata this has not got.</param>
    /// <returns>True when the profile was resolved and written.</returns>
    public static async Task<bool> ApplyAsync(
        PackProfile profile,
        string packProfilePath,
        string patchesDirectory,
        LauncherPaths paths,
        HttpClient? client,
        string metaUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(paths);

        if (client is null)
        {
            /*
             * NOT AN ERROR. An import without a network already writes an instance with its mods
             * missing and says so; refusing to write it at all over a resolvable-later dependency
             * would be worse. The launch will report the missing requirement, as it does today.
             */
            return false;
        }

        var index = new MetaIndex();

        var update = new ComponentUpdateTask(
            profile,
            index,
            patchesDirectory,
            ComponentUpdateMode.Resolution,
            NetMode.Online,
            CreateLoader(paths, index, client, metaUrl));

        try
        {
            if (!await update.RunAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException)
        {
            return false;
        }

        // Written back, because the whole point is that the next launch reads a complete list rather
        // than repeating this work -- and repeating it needs a network the next launch may not have.
        return profile.Save(packProfilePath);
    }

    /// <summary>
    /// The metadata fetcher, shaped the way ComponentUpdateTask wants it.
    /// </summary>
    /// <remarks>
    /// The same shape LauncherService builds for a launch, minus the progress reporting: creating an
    /// instance already shows its own progress, and there is nobody here to report to.
    /// </remarks>
    private static Func<string, string, CancellationToken, Task<bool>> CreateLoader(
        LauncherPaths paths,
        MetaIndex index,
        HttpClient client,
        string metaUrl)
        => async (uid, version, token) =>
        {
            var task = index.CreateLoadVersionTask(
                uid, version, client, paths.CreateCache(), paths.Meta, metaUrl, NetMode.Online);

            return await task.RunAsync(token).ConfigureAwait(false)
                && index.GetOrCreate(uid, version).Data is not null;
        };
}
