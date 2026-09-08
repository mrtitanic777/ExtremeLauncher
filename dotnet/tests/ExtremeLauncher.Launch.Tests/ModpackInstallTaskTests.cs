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
 * Both providers the browser offers are now installable — Modrinth through its importer, CurseForge
 * through the Flame import task. These pin that the install routes by provider: each provider reaches
 * the download step (shown by the empty-URL check firing), rather than one being refused. Neither test
 * touches the network.
 */

using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class ModpackInstallTaskTests
{
    private static (IndexedPack, IndexedVersion) PackAndVersion(ResourceProvider provider, string downloadUrl)
    {
        var pack = new IndexedPack { AddonId = "1", Provider = provider, Name = "Some Pack" };
        var version = new IndexedVersion { Version = "1.0", DownloadUrl = downloadUrl };

        return (pack, version);
    }

    /// <summary>
    /// Each provider passes the guard and reaches the download step — proven by an empty download URL,
    /// whose message only fires once the install has accepted the provider and gone looking for a file.
    /// </summary>
    [Theory]
    [InlineData(ResourceProvider.Modrinth)]
    [InlineData(ResourceProvider.Flame)]
    public async Task EachProviderReachesTheDownloadStep(ResourceProvider provider)
    {
        var (pack, version) = PackAndVersion(provider, string.Empty);
        using var client = new HttpClient();

        var task = new ModpackInstallTask(client, pack, version) { StagingPath = Path.GetTempPath() };

        Assert.False(await task.RunAsync());
        Assert.Contains("no file to download", task.FailReason, StringComparison.Ordinal);
    }
}
