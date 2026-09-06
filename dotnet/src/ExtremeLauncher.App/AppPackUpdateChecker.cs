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
 * The app's half of checking a modpack for updates: fetch the version list, hand it to the rule.
 *
 * THE RULE ITSELF IS IN PackUpdateCheck, deliberately -- deciding what counts as an update is the
 * part worth testing without a network, and this is the part that cannot be.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;

// Avalonia.Controls has a ResourceProvider of its own; this is the mod-platform one.
using ResourceProvider = ExtremeLauncher.ModPlatform.ResourceProvider;

namespace ExtremeLauncher.App;

public sealed class AppPackUpdateChecker(HttpClient client, LauncherLog? log = null) : IPackUpdateChecker
{
    public async Task<PackUpdateResult> CheckAsync(string packId, string versionId, string versionName)
    {
        var source = new ResourceSearchSource(client);

        /*
         * A bare IndexedPack rather than a searched one: the version endpoint takes the project id
         * and nothing else, and searching for a pack whose id is already known would be a request
         * spent confirming what the instance already recorded.
         */
        var pack = new IndexedPack { Provider = ResourceProvider.Modrinth, AddonId = packId };

        await source.LoadVersionsAsync(pack, new VersionSearchArgs { Pack = pack }).ConfigureAwait(false);

        var result = PackUpdateCheck.Evaluate(versionId, versionName, pack.Versions);

        log?.Info($"Update check for pack {packId}: {result.Message}");

        return result;
    }
}
