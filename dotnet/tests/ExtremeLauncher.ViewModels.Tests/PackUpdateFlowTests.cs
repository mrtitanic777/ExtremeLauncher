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
 * Offering a pack update on the Version page.
 *
 * THE BUTTON ONLY EXISTS AFTER A CHECK SAID SOMETHING. Everything here is about it not outliving the
 * answer that produced it -- an Update button pointing at a version that is now installed, or at an
 * instance somebody has since changed, is worse than no button.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class PackUpdateFlowTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-updflow-" + Guid.NewGuid().ToString("N"));

    public PackUpdateFlowTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubChecker(PackUpdateResult result) : IPackUpdateChecker
    {
        public int Calls { get; private set; }

        public Task<PackUpdateResult> CheckAsync(string packId, string versionId, string versionName)
        {
            Calls++;

            return Task.FromResult(result);
        }
    }

    private sealed class StubUpdater(PackUpdateOutcome? outcome = null) : IPackUpdater
    {
        public int Calls { get; private set; }

        public IndexedVersion? Asked { get; private set; }

        public Task<PackUpdateOutcome> UpdateAsync(IndexedVersion version)
        {
            Calls++;
            Asked = version;

            return Task.FromResult(outcome ?? new PackUpdateOutcome(true, "Updated to 2.0.0."));
        }
    }

    private static IndexedVersion Version(string fileId, string name)
        => new() { FileId = fileId, Version = name };

    private InstanceSettings Managed(string packId = "abc", string versionName = "1.0.0")
    {
        var globals = GlobalSettings.Create(Path.Combine(_temp, "extremelauncher.cfg"));
        var own = new IniSettingsObject(Path.Combine(_temp, $"instance-{Guid.NewGuid():N}.cfg"));

        var settings = new InstanceSettings(own, globals);

        settings.SetManagedPack("modrinth", packId, "A Pack", "v1", versionName);

        return settings;
    }

    private static PackUpdateResult Available(IndexedVersion version)
        => new() { Available = true, Message = $"{version.Version} is available.", Newest = version };

    private static PackUpdateResult UpToDate()
        => new() { Available = false, Message = "This is the newest version of the pack." };

    [Fact]
    public void NothingIsOfferedBeforeACheck()
    {
        var page = new VersionPageViewModel(updates: new StubChecker(UpToDate()), updater: new StubUpdater());

        page.LoadProvenance(Managed());

        Assert.False(page.CanApplyPackUpdate);
        Assert.True(page.CanCheckForPackUpdate);
    }

    [Fact]
    public async Task ACheckThatFindsNothingOffersNothing()
    {
        var page = new VersionPageViewModel(updates: new StubChecker(UpToDate()), updater: new StubUpdater());

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();

        Assert.False(page.CanApplyPackUpdate);
        Assert.Contains("newest version", page.PackUpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckThatFindsSomethingOffersIt()
    {
        var page = new VersionPageViewModel(
            updates: new StubChecker(Available(Version("v2", "2.0.0"))),
            updater: new StubUpdater());

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();

        Assert.True(page.CanApplyPackUpdate);
    }

    [Fact]
    public async Task TheButtonNamesTheVersionItWouldInstall()
    {
        /*
         * Pressing it deletes files. A generic "Update" makes somebody click to find out what it
         * means; "Update to 2.0.0" does not.
         */
        var page = new VersionPageViewModel(
            updates: new StubChecker(Available(Version("v2", "2.0.0"))),
            updater: new StubUpdater());

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();

        Assert.Equal("Update to 2.0.0", page.ApplyPackUpdateLabel);
    }

    [Fact]
    public async Task ApplyingPassesTheOfferedVersionThrough()
    {
        // Sounds obvious; it is what makes the confirmation the app shows honest, because that
        // dialog names the version this object was handed.
        var updater = new StubUpdater();

        var page = new VersionPageViewModel(updates: new StubChecker(Available(Version("v2", "2.0.0"))), updater: updater);

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();
        await page.ApplyPackUpdateAsync();

        Assert.Equal(1, updater.Calls);
        Assert.Equal("v2", updater.Asked?.FileId);
    }

    [Fact]
    public async Task TheOfferIsSpentOnceItHasBeenTaken()
    {
        /*
         * Otherwise Update stays on screen pointing at the version that is now installed, and
         * pressing it again diffs the pack against itself.
         */
        var page = new VersionPageViewModel(
            updates: new StubChecker(Available(Version("v2", "2.0.0"))),
            updater: new StubUpdater());

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();
        await page.ApplyPackUpdateAsync();

        Assert.False(page.CanApplyPackUpdate);
    }

    [Fact]
    public async Task AFailedUpdateKeepsTheOfferSoItCanBeRetried()
    {
        // The opposite case, and it matters: a download that timed out is exactly the situation
        // where somebody wants to press the button again.
        var page = new VersionPageViewModel(
            updates: new StubChecker(Available(Version("v2", "2.0.0"))),
            updater: new StubUpdater(new PackUpdateOutcome(false, "The server hung up.")));

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();
        await page.ApplyPackUpdateAsync();

        Assert.True(page.CanApplyPackUpdate);
        Assert.Contains("hung up", page.PackUpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingSaysNothingRatherThanReportingAFailure()
    {
        // Somebody who read the plan and decided against it has not had an error.
        var page = new VersionPageViewModel(
            updates: new StubChecker(Available(Version("v2", "2.0.0"))),
            updater: new StubUpdater(new PackUpdateOutcome(false, string.Empty)));

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();
        await page.ApplyPackUpdateAsync();

        Assert.Equal(string.Empty, page.PackUpdateStatus);
    }

    [Fact]
    public async Task WithNoUpdaterTheOfferIsNeverActionable()
    {
        /*
         * A build with no way to ask a question must not be able to delete files. The CHECK is still
         * offered, because reading is harmless and knowing is useful.
         */
        var page = new VersionPageViewModel(updates: new StubChecker(Available(Version("v2", "2.0.0"))));

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();

        Assert.False(page.CanApplyPackUpdate);
        Assert.Contains("is available", page.PackUpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningAnotherInstanceForgetsTheOffer()
    {
        /*
         * LoadProvenance is what a window calls when it shows an instance. An offer surviving that
         * would let somebody update the wrong instance to a version that was checked for a different
         * one -- the worst outcome this feature has available.
         */
        var page = new VersionPageViewModel(
            updates: new StubChecker(Available(Version("v2", "2.0.0"))),
            updater: new StubUpdater());

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();

        Assert.True(page.CanApplyPackUpdate);

        page.LoadProvenance(Managed(packId: "a-different-pack"));

        Assert.False(page.CanApplyPackUpdate);
        Assert.Equal(string.Empty, page.PackUpdateStatus);
    }

    /// <summary>Writes an mmc-pack.json with one Fabric version in it.</summary>
    private string WriteProfile(string fabricVersion)
    {
        var path = Path.Combine(_temp, "mmc-pack.json");

        File.WriteAllText(path, $$"""
        {
          "formatVersion": 1,
          "components": [
            { "uid": "net.minecraft", "version": "1.20.1", "important": true },
            { "uid": "net.fabricmc.fabric-loader", "version": "{{fabricVersion}}" }
          ]
        }
        """);

        return path;
    }

    [Fact]
    public void ReloadingPicksUpAProfileSomethingElseRewrote()
    {
        /*
         * AN INSTANCE CORRECT ON DISK AND WRONG ON SCREEN is the confusing way round. A pack that
         * bumped its loader version rewrites mmc-pack.json underneath this page, and without a
         * re-read the window keeps showing the old one until somebody closes and reopens it.
         */
        var path = WriteProfile("0.15.7");

        var profile = new PackProfile(new RuntimeContext());

        Assert.True(profile.Load(path));

        var page = new VersionPageViewModel();

        page.Load(profile, path);

        Assert.Equal("0.15.7", page.Components.Single(c => c.Uid == "net.fabricmc.fabric-loader").Version);

        WriteProfile("0.16.9");

        Assert.True(page.ReloadFromDisk());
        Assert.Equal("0.16.9", page.Components.Single(c => c.Uid == "net.fabricmc.fabric-loader").Version);
    }

    [Fact]
    public void AProfileThatWillNotParseLeavesThePageShowingWhatItHad()
    {
        /*
         * A page that empties itself because a re-read failed is worse than one briefly out of date,
         * and the instance on disk is fine either way.
         */
        var path = WriteProfile("0.15.7");

        var profile = new PackProfile(new RuntimeContext());

        Assert.True(profile.Load(path));

        var page = new VersionPageViewModel();

        page.Load(profile, path);

        File.WriteAllText(path, "{ not json at all");

        Assert.False(page.ReloadFromDisk());
        Assert.Equal("0.15.7", page.Components.Single(c => c.Uid == "net.fabricmc.fabric-loader").Version);
    }

    [Fact]
    public void ReloadingAPageWithNoProfileIsHarmless()
    {
        // The Version page is not built at all when mmc-pack.json will not load, but the update
        // flow calls this unconditionally.
        Assert.False(new VersionPageViewModel().ReloadFromDisk());
    }

    [Fact]
    public async Task ACancelledUpdateSaysNothingWasChanged()
    {
        /*
         * NOT AN ERROR, AND NOT SILENCE EITHER. Somebody who cancelled part way through a download
         * wants to know the instance is still fine -- "Cancelled." on its own leaves them wondering
         * whether it stopped before or after it started deleting things.
         *
         * The empty-message case (they declined the confirmation before anything began) stays silent;
         * this one has actually started work and then stopped.
         */
        var page = new VersionPageViewModel(
            updates: new StubChecker(Available(Version("v2", "2.0.0"))),
            updater: new StubUpdater(new PackUpdateOutcome(false, "Cancelled. Nothing was changed.")));

        page.LoadProvenance(Managed());

        await page.CheckForPackUpdateAsync();
        await page.ApplyPackUpdateAsync();

        Assert.Contains("Nothing was changed", page.PackUpdateStatus, StringComparison.Ordinal);

        // And the offer survives, because cancelling is not a reason to stop offering.
        Assert.True(page.CanApplyPackUpdate);
    }

    [Fact]
    public async Task AFileImportedPackIsNeverOfferedAnUpdate()
    {
        // It has no project id, so there is nothing to check and nothing to install.
        var globals = GlobalSettings.Create(Path.Combine(_temp, "extremelauncher.cfg"));
        var own = new IniSettingsObject(Path.Combine(_temp, "file-import.cfg"));

        var settings = new InstanceSettings(own, globals);

        settings.SetManagedPack("modrinth", string.Empty, "A Pack", string.Empty, "1.0.0");

        var checker = new StubChecker(Available(Version("v2", "2.0.0")));

        var page = new VersionPageViewModel(updates: checker, updater: new StubUpdater());

        page.LoadProvenance(settings);

        Assert.False(page.CanCheckForPackUpdate);

        await page.CheckForPackUpdateAsync();

        Assert.Equal(0, checker.Calls);
        Assert.False(page.CanApplyPackUpdate);
    }
}
