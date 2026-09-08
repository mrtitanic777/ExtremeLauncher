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
 * The browser can list CurseForge packs (the modpack index is parsed) but installing one is fed to the
 * Modrinth importer, which reads a different format. These pin the provider guard: a non-Modrinth pack
 * is refused with a clear message before anything is downloaded, and a Modrinth pack is let through to
 * the next check. Neither test touches the network.
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

    [Fact]
    public async Task ACurseForgePackIsRefusedBeforeAnyDownload()
    {
        var (pack, version) = PackAndVersion(ResourceProvider.Flame, "https://x.invalid/pack.zip");
        using var client = new HttpClient();

        var task = new ModpackInstallTask(client, pack, version) { StagingPath = Path.GetTempPath() };

        Assert.False(await task.RunAsync());
        Assert.False(task.WasSuccessful);
        Assert.Contains("not supported yet", task.FailReason, StringComparison.Ordinal);
        Assert.Contains("Flame", task.FailReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Modrinth pack passes the provider guard and reaches the next check — proven here by an empty
    /// download URL, whose distinct message shows the guard let it through rather than stopping it.
    /// </summary>
    [Fact]
    public async Task AModrinthPackPassesTheProviderGuard()
    {
        var (pack, version) = PackAndVersion(ResourceProvider.Modrinth, string.Empty);
        using var client = new HttpClient();

        var task = new ModpackInstallTask(client, pack, version) { StagingPath = Path.GetTempPath() };

        Assert.False(await task.RunAsync());
        Assert.Contains("no file to download", task.FailReason, StringComparison.Ordinal);
    }
}
