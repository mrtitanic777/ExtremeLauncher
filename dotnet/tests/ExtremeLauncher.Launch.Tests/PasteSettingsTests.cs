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
 * The paste-service setting, and the migration into it.
 *
 * ASSERTED THROUGH A CONFIG FILE ON DISK wherever the migration is involved, because that is the only
 * thing a migration can actually act on: an old install's file is the input, and a Set() call in a
 * test would be testing something that never happens.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Net;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class PasteSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "el-paste-" + Guid.NewGuid().ToString("N"));

    public PasteSettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes a config the way an older install left it, then opens it.</summary>
    private SettingsObject Existing(params string[] lines)
    {
        var path = Path.Combine(_folder, "extremelauncher.cfg");

        File.WriteAllLines(path, lines);

        return GlobalSettings.Create(path);
    }

    [Fact]
    public void AFreshInstallUsesMcLogs()
    {
        // The one service of the four that understands a Minecraft log.
        Assert.Equal(PasteType.Mclogs, GlobalSettings.ResolvePasteType(Existing()));
        Assert.Equal(string.Empty, GlobalSettings.ResolvePasteBase(Existing()));
    }

    [Fact]
    public void AnOldInstallPointedAtItsOwnPasteBinKeepsIt()
    {
        /*
         * THE MIGRATION'S REAL JOB. PastebinURL is the old single-service setting, from when 0x0.st
         * was the only option -- so a non-default value means somebody deliberately pointed the
         * launcher at their own instance, and silently moving them to mclo.gs would send their logs
         * somewhere they did not choose.
         */
        var settings = Existing("PastebinURL=https://paste.mycompany.invalid");

        Assert.Equal(PasteType.NullPointer, GlobalSettings.ResolvePasteType(settings));
        Assert.Equal("https://paste.mycompany.invalid", GlobalSettings.ResolvePasteBase(settings));
    }

    [Fact]
    public void TheOldKeyIsClearedSoTheMigrationDoesNotRunForever()
    {
        // Left in place, it would re-apply on every start and undo any later change of service.
        var path = Path.Combine(_folder, "extremelauncher.cfg");

        File.WriteAllLines(path, ["PastebinURL=https://paste.mycompany.invalid"]);

        GlobalSettings.Create(path);

        Assert.DoesNotContain("PastebinURL=https", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOldInstallOnTheOldDefaultGetsTheNewDefault()
    {
        /*
         * The other half, and the reason the migration checks the value rather than merely its
         * presence: somebody who never changed it was not expressing a preference for 0x0.st, so
         * they get mclo.gs like everybody else.
         */
        var settings = Existing("PastebinURL=https://0x0.st");

        Assert.Equal(PasteType.Mclogs, GlobalSettings.ResolvePasteType(settings));
        Assert.Equal(string.Empty, GlobalSettings.ResolvePasteBase(settings));
    }

    [Fact]
    public void AnAlreadyMigratedConfigIsLeftAlone()
    {
        var settings = Existing("PastebinType=0", "PastebinCustomAPIBase=https://paste.mycompany.invalid");

        Assert.Equal(PasteType.NullPointer, GlobalSettings.ResolvePasteType(settings));
        Assert.Equal("https://paste.mycompany.invalid", GlobalSettings.ResolvePasteBase(settings));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("banana")]
    public void ATypeNobodyRecognisesResetsBothSettings(string value)
    {
        /*
         * A hand-edited config, or one written by a newer version that knows a fifth service. BOTH
         * are reset, because a custom base belongs to a service and means nothing without one --
         * keeping it would point the default service at somebody else's instance.
         */
        var settings = Existing($"PastebinType={value}", "PastebinCustomAPIBase=https://paste.mycompany.invalid");

        Assert.Equal(PasteType.Mclogs, GlobalSettings.ResolvePasteType(settings));
        Assert.Equal(string.Empty, GlobalSettings.ResolvePasteBase(settings));
    }

    [Fact]
    public void EveryValidTypeSurvivesARoundTrip()
    {
        // The numbers are shared on-disk format, so this is really asserting the enum's order.
        foreach (var type in new[] { PasteType.NullPointer, PasteType.Hastebin, PasteType.PasteGG, PasteType.Mclogs })
        {
            var settings = Existing($"PastebinType={(int)type}");

            Assert.Equal(type, GlobalSettings.ResolvePasteType(settings));
        }
    }

    [Fact]
    public void TheRangeCheckMatchesTheEnum()
    {
        Assert.True(GlobalSettings.IsKnownPasteType(0));
        Assert.True(GlobalSettings.IsKnownPasteType(3));
        Assert.False(GlobalSettings.IsKnownPasteType(4));
        Assert.False(GlobalSettings.IsKnownPasteType(-1));
    }

    [Fact]
    public void WithNoSettingsAtAllThereIsStillAService()
    {
        // The CLI resolves this before it has read anything.
        Assert.Equal(PasteType.Mclogs, GlobalSettings.ResolvePasteType(null));
        Assert.Equal(string.Empty, GlobalSettings.ResolvePasteBase(null));
    }
}
