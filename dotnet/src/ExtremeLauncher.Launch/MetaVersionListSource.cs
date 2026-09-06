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
 * Fetches a component's version list from the metadata server, for the new-instance dialog.
 *
 * The version LIST, not a version: the whole point is to offer what exists so a user does not type a
 * version that does not. Only the index and the list are fetched -- the individual version documents
 * are hundreds of files and none of them is needed to show a name and a date.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;

using MetaIndex = ExtremeLauncher.Meta.Index;

namespace ExtremeLauncher.Launch;

public sealed class MetaVersionListSource
{
    private readonly LauncherPaths _paths;

    private readonly HttpClient _client;

    private readonly string _metaUrl;

    private readonly MetaIndex _index = new();

    public MetaVersionListSource(LauncherPaths paths, HttpClient client, string metaUrl)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(client);

        _paths = paths;
        _client = client;
        _metaUrl = metaUrl;
    }

    /// <summary>Fetches the versions of one component, newest first as the metadata orders them.</summary>
    public async Task<IReadOnlyList<MetaVersion>> LoadAsync(string uid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uid);

        var cache = _paths.CreateCache();
        var list = _index.Get(uid);

        var task = new SequentialTask($"Load versions of {uid}");

        task.AddTask(_index.CreateLoadTask(_client, cache, _paths.Meta, _metaUrl));
        task.AddTask(list.CreateLoadTask(_client, cache, _paths.Meta, _metaUrl));

        if (!await task.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            /*
             * Thrown rather than returned empty. The caller shows a list, and an empty one reads as
             * "there are no versions of Minecraft" -- which is never the truth. HttpRequestException is
             * what the view model already catches and reports.
             */
            throw new HttpRequestException(
                task.FailReason.Length != 0 ? task.FailReason : "The metadata server could not be reached.");
        }

        cache.SaveNow();

        return list.Versions;
    }
}
