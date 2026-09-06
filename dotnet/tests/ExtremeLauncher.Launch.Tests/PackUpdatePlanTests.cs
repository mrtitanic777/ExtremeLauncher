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
 * Working out what updating a modpack would do to the folder.
 *
 * EVERY TEST HERE IS ABOUT A DELETION. Downloading the wrong file wastes bandwidth; removing the
 * wrong one destroys something a player made. So the interesting cases are all "what does this NOT
 * remove", and the most important is the one where the answer is "nothing, because I cannot tell".
 */

using System.Security.Cryptography;
using System.Text;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class PackUpdatePlanTests
{
    private static byte[] HashFor(string content) => SHA512.HashData(Encoding.UTF8.GetBytes(content));

    private static ModrinthPackFile File(string path, string content)
        => new() { Path = path, Hash = HashFor(content) };

    private static ModrinthPackManifest Manifest(params ModrinthPackFile[] files)
    {
        var manifest = new ModrinthPackManifest { Name = "A Pack", VersionId = "1.0.0" };

        manifest.Files.AddRange(files);

        return manifest;
    }

    private static PackLedger Ledger(ModrinthPackManifest manifest, params string[] overrides)
        => new() { Manifest = manifest, Overrides = overrides };

    [Fact]
    public void WithNoLedgerNothingIsPlannedAtAll()
    {
        /*
         * THE CASE THAT MATTERS MOST, and a deliberate divergence. Every pack this launcher installed
         * before the ledger existed lands here, as does one imported by a launcher that keeps none.
         *
         * Upstream carries on with a "files may be duplicated" warning, which undersells it: without
         * the old manifest it cannot remove the old version's mods at all, so the instance ends up
         * running two copies of half its mod list. Refusing is the better answer.
         */
        var plan = PackUpdatePlanner.Create(new PackLedger(), Manifest(File("mods/a.jar", "a")));

        Assert.False(plan.Possible);
        Assert.Empty(plan.ToRemove);
        Assert.Empty(plan.ToDownload);
        Assert.Contains("no record of which files came from the pack", plan.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnchangedFileIsNeitherFetchedNorRemoved()
    {
        var old = Manifest(File("mods/a.jar", "a"));

        var plan = PackUpdatePlanner.Create(Ledger(old), Manifest(File("mods/a.jar", "a")));

        Assert.True(plan.Possible);
        Assert.Empty(plan.ToDownload);
        Assert.Empty(plan.ToRemove);
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public void ARenamedButIdenticalFileIsStillUnchanged()
    {
        /*
         * WHY MATCHING IS BY HASH rather than by path, which is upstream's rule too. Pack authors
         * re-path and rename files between releases constantly -- a version bump in a filename is the
         * normal case -- and matching on the name would re-download and re-delete the entire mod list
         * every single update.
         */
        var old = Manifest(File("mods/sodium-0.5.8.jar", "the same bytes"));

        var plan = PackUpdatePlanner.Create(Ledger(old), Manifest(File("mods/sodium-0.5.9.jar", "the same bytes")));

        Assert.Empty(plan.ToDownload);
        Assert.Empty(plan.ToRemove);
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public void ANewFileIsDownloaded()
    {
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/a.jar", "a"))),
            Manifest(File("mods/a.jar", "a"), File("mods/b.jar", "b")));

        Assert.Equal(["mods/b.jar"], plan.ToDownload.Select(f => f.Path));
        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void AFileTheNewVersionDroppedIsRemoved()
    {
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/a.jar", "a"), File("mods/gone.jar", "gone"))),
            Manifest(File("mods/a.jar", "a")));

        Assert.Equal(["mods/gone.jar"], plan.ToRemove);
        Assert.Empty(plan.ToDownload);
    }

    [Fact]
    public void AChangedFileIsBothRemovedAndFetched()
    {
        // The ordinary case of a mod being updated: different bytes, so the old one goes and the new
        // one arrives. They happen to share a path, and that is fine -- the download lands after.
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/a.jar", "old bytes"))),
            Manifest(File("mods/a.jar", "new bytes")));

        Assert.Equal(["mods/a.jar"], plan.ToDownload.Select(f => f.Path));
        Assert.Equal(["mods/a.jar"], plan.ToRemove);
    }

    [Fact]
    public void AFileThePlayerAddedIsNotTouched()
    {
        /*
         * THE WHOLE POINT OF THE LEDGER. The player's own mod is in neither manifest, so it appears
         * in neither list -- the plan simply has nothing to say about it, which is exactly right.
         */
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/a.jar", "a"))),
            Manifest(File("mods/a.jar", "a"), File("mods/b.jar", "b")));

        Assert.DoesNotContain("mods/my-own-mod.jar", plan.ToRemove);
        Assert.DoesNotContain(plan.ToDownload, f => f.Path.Contains("my-own", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOldVersionsOverridesAreRemoved()
    {
        /*
         * UPSTREAM'S RULE, and the uncomfortable one: an override is usually a config file and the
         * player may well have edited it. It is kept because the alternative is worse in a way that
         * is harder to see -- a pack that changes a config's format leaves the instance running the
         * old file, and that failure looks nothing like "my edits were kept".
         *
         * The mitigation is the Summary, which says how many, so somebody sees the number first.
         */
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/a.jar", "a")), "config/sodium-options.json", "options.txt"),
            Manifest(File("mods/a.jar", "a")),
            newOverridePaths: []);

        // With an EMPTY new override list the new version ships neither of them, so both are real
        // losses rather than refreshes.
        Assert.Equal(["config/sodium-options.json", "options.txt"], plan.ToRemove);
    }

    [Fact]
    public void AConfigTheNewVersionAlsoShipsIsARefreshRatherThanALoss()
    {
        /*
         * FOUND BY RUNNING A REAL UPDATE. Updating Fabulously Optimized by one release reported
         * "remove 71 files", of which 46 were overrides about to be re-written a second later.
         * Anybody reading that before agreeing would reasonably think they were losing something.
         *
         * The two are still deleted the same way on disk; they are counted apart because the numbers
         * mean different things to a person.
         */
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(), "config/kept.json", "config/dropped.json"),
            Manifest(),
            newOverridePaths: ["config/kept.json"]);

        Assert.Equal(["config/dropped.json"], plan.ToRemove);
        Assert.Equal(["config/kept.json"], plan.ToReplace);

        Assert.Contains("refresh 1 of the pack's own config file", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("remove 1 file", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BothGroupsAreStillDeleted()
    {
        // The distinction is for the message. Everything in it still goes.
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(), "config/kept.json", "config/dropped.json"),
            Manifest(),
            newOverridePaths: ["config/kept.json"]);

        Assert.Equal(
            ["config/dropped.json", "config/kept.json"],
            plan.AllRemovals.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WithoutTheNewOverrideListEverythingCountsAsARefresh()
    {
        /*
         * The reassuring answer, and therefore the one that has to be EARNED. A caller that cannot
         * open the new pack gets it, which is why PackUpdateTask always supplies the list.
         */
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(), "config/a.json", "config/b.json"),
            Manifest());

        Assert.Empty(plan.ToRemove);
        Assert.Equal(2, plan.ToReplace.Count);
    }

    [Fact]
    public void ReinstallingTheSameVersionRefreshesConfigsAndLosesNothing()
    {
        // The case that exposed the problem: nothing is actually lost, and the summary must not
        // suggest otherwise.
        var same = Manifest(File("mods/a.jar", "a"));

        var plan = PackUpdatePlanner.Create(
            Ledger(same, "config/thing.json"),
            same,
            newOverridePaths: ["config/thing.json"]);

        Assert.Empty(plan.ToRemove);
        Assert.Equal(["config/thing.json"], plan.ToReplace);
        Assert.DoesNotContain("remove", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeClausesReadAsASentence()
    {
        // A naive join gives "download 26 files and refresh 44 files and remove 27 files", which
        // reads like a child listing things. Seen in a real update before it was fixed.
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/old.jar", "old")), "config/kept.json", "config/dropped.json"),
            Manifest(File("mods/new.jar", "new")),
            newOverridePaths: ["config/kept.json"]);

        Assert.Contains(
            "download 1 file, refresh 1 of the pack's own config file and remove 2 files",
            plan.Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void APathListedTwiceIsCountedOnce()
    {
        // An override that is also a listed file. Deleting it twice is harmless; reporting "remove 2
        // files" when there is one is not.
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("config/thing.json", "old")), "config/thing.json"),
            Manifest(),
            newOverridePaths: []);

        Assert.Equal(["config/thing.json"], plan.ToRemove);
    }

    [Fact]
    public void BackslashesAreNormalisedSoTheSamePathMatches()
    {
        // A ledger written on Windows by a launcher that did not normalise, read on Linux. Left
        // alone, "config\thing.json" and "config/thing.json" are two different files to the dedupe.
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("config/thing.json", "old")), "config\\thing.json"),
            Manifest(),
            newOverridePaths: []);

        Assert.Equal(["config/thing.json"], plan.ToRemove);
    }

    [Fact]
    public void AFileWithNoHashIsFetchedRatherThanMatched()
    {
        /*
         * Errs towards downloading, never towards removing. A manifest entry with no hash cannot be
         * compared, and a wasted download is a much better mistake than a deletion.
         */
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/a.jar", "a"))),
            Manifest(new ModrinthPackFile { Path = "mods/odd.jar" }));

        Assert.Single(plan.ToDownload);
    }

    [Fact]
    public void TheSummarySaysWhatWouldHappen()
    {
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/a.jar", "a"), File("mods/gone.jar", "gone"))),
            Manifest(File("mods/a.jar", "a"), File("mods/new.jar", "new")));

        Assert.Contains("download 1 file", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("remove 1 file", plan.Summary, StringComparison.Ordinal);

        // The count that makes the other two readable: "remove 40 files" alone sounds like most of
        // the instance is going.
        Assert.Contains("1 file would be left alone", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PluralsAreRight()
    {
        // The kind of thing nobody notices until a user screenshots it.
        var plan = PackUpdatePlanner.Create(
            Ledger(Manifest(File("mods/x.jar", "x"), File("mods/y.jar", "y"))),
            Manifest(File("mods/a.jar", "a"), File("mods/b.jar", "b")));

        Assert.Contains("download 2 files", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("remove 2 files", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReinstallingTheSameVersionChangesNothing()
    {
        // Worth stating, because it is what "update" to the version you already have must do.
        var same = Manifest(File("mods/a.jar", "a"), File("mods/b.jar", "b"));

        var plan = PackUpdatePlanner.Create(Ledger(same), same);

        Assert.Empty(plan.ToDownload);
        Assert.Empty(plan.ToRemove);
        Assert.Contains("Nothing would change", plan.Summary, StringComparison.Ordinal);
    }
}
