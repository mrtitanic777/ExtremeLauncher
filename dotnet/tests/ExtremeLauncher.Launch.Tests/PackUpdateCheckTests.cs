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
 * Deciding whether a modpack has a newer version.
 *
 * THE DECISION IS ABOUT WHAT NOT TO OFFER. Every rule here exists to stop a specific piece of bad
 * advice: pushing somebody onto a pre-release, guessing when the answer is unknowable, or moving them
 * to a different Minecraft version without saying so.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class PackUpdateCheckTests
{
    private static IndexedVersion V(string fileId, string name, VersionType type, params string[] mc)
    {
        var version = new IndexedVersion { FileId = fileId, Version = name, VersionType = type };

        version.McVersion.AddRange(mc.Length != 0 ? mc : ["1.20.1"]);

        return version;
    }

    [Fact]
    public void BeingOnTheNewestReleaseIsNotAnUpdate()
    {
        var result = PackUpdateCheck.Evaluate(
            "v2",
            "1.1.0",
            [V("v2", "1.1.0", VersionType.Release), V("v1", "1.0.0", VersionType.Release)]);

        Assert.False(result.Available);
        Assert.Contains("newest version", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewerReleaseIsAnUpdate()
    {
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "1.0.0",
            [V("v2", "1.1.0", VersionType.Release), V("v1", "1.0.0", VersionType.Release)]);

        Assert.True(result.Available);
        Assert.Equal("v2", result.Newest?.FileId);
        Assert.Contains("1.1.0 is available", result.Message, StringComparison.Ordinal);
        Assert.Contains("you have 1.0.0", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABetaIsNotAnUpdateForSomebodyOnAStableRelease()
    {
        /*
         * THE RULE THAT MATTERS MOST, and the live data is why: Fabulously Optimized's four newest
         * versions are betas for a Minecraft SNAPSHOT. Calling those an update would push a player on
         * 13.3.0 onto a pre-release build of a game version they do not even have.
         */
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "13.3.0",
            [
                V("v3", "14.0.0-beta.6", VersionType.Beta, "26.2"),
                V("v2", "14.0.0-beta.5", VersionType.Beta, "26.2"),
                V("v1", "13.3.0", VersionType.Release, "26.1.2"),
            ]);

        Assert.False(result.Available);
        Assert.Contains("newest release", result.Message, StringComparison.Ordinal);

        // But it SAYS they exist, rather than pretending the pack has gone quiet.
        Assert.Contains("2 newer pre-release", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneNewerPreReleaseIsCountedInTheSingular()
    {
        // "There are 1 newer pre-release versions" is the kind of thing nobody notices until a user
        // screenshots it.
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "1.0.0",
            [V("v2", "1.1.0-beta", VersionType.Beta), V("v1", "1.0.0", VersionType.Release)]);

        Assert.Contains("is 1 newer pre-release version,", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SomebodyAlreadyOnABetaIsOfferedTheNextBeta()
    {
        // They opted into the pre-release channel; leaving them stranded on an old beta because the
        // next one is also a beta would be its own kind of wrong.
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "14.0.0-beta.5",
            [
                V("v2", "14.0.0-beta.6", VersionType.Beta, "26.2"),
                V("v1", "14.0.0-beta.5", VersionType.Beta, "26.2"),
            ]);

        Assert.True(result.Available);
        Assert.Equal("v2", result.Newest?.FileId);
    }

    [Fact]
    public void AReleaseIsStillOfferedToSomebodyOnABeta()
    {
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "1.1.0-beta",
            [V("v2", "1.1.0", VersionType.Release), V("v1", "1.1.0-beta", VersionType.Beta)]);

        Assert.True(result.Available);
        Assert.Equal("v2", result.Newest?.FileId);
    }

    [Fact]
    public void AWithdrawnCurrentVersionIsInconclusiveRatherThanGuessed()
    {
        /*
         * NEITHER ANSWER IS HONEST HERE. The installed version has been removed from the listing, so
         * it might be older than everything or newer than everything -- and "you are up to date" is
         * as much a guess as "an update is available".
         */
        var result = PackUpdateCheck.Evaluate(
            "gone",
            "1.0.0",
            [V("v2", "1.1.0", VersionType.Release)]);

        Assert.False(result.Available);
        Assert.True(result.Inconclusive);
        Assert.Contains("no longer listed", result.Message, StringComparison.Ordinal);

        // It still says what the newest is, because that is the useful half of the answer.
        Assert.Contains("1.1.0", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APackWithNoVersionsAtAllIsInconclusive()
    {
        var result = PackUpdateCheck.Evaluate("v1", "1.0.0", []);

        Assert.True(result.Inconclusive);
        Assert.False(result.Available);
    }

    [Fact]
    public void MovingToADifferentMinecraftVersionIsSaidOutLoud()
    {
        /*
         * THE ONE PLACE BEING CONSERVATIVE WOULD BE WRONG. A pack moving to a new Minecraft version
         * IS the update people are waiting for -- but it changes what the instance is, and their
         * worlds and their other mods are on the old one. It must not slip past in a one-line
         * "update available".
         */
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "1.0.0",
            [V("v2", "2.0.0", VersionType.Release, "1.21.1"), V("v1", "1.0.0", VersionType.Release, "1.20.1")]);

        Assert.True(result.Available);
        Assert.Contains("Minecraft 1.21.1", result.Message, StringComparison.Ordinal);
        Assert.Contains("rather than 1.20.1", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StayingOnTheSameMinecraftVersionSaysNothingExtra()
    {
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "1.0.0",
            [V("v2", "1.1.0", VersionType.Release, "1.20.1"), V("v1", "1.0.0", VersionType.Release, "1.20.1")]);

        Assert.True(result.Available);
        Assert.DoesNotContain("rather than", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlappingMinecraftSupportIsNotAMove()
    {
        // A pack that supported 1.20.1 and 1.20.4 and now supports only 1.20.4 has not moved for
        // somebody on 1.20.4, and warning them would be noise.
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "1.0.0",
            [
                V("v2", "1.1.0", VersionType.Release, "1.20.4"),
                V("v1", "1.0.0", VersionType.Release, "1.20.1", "1.20.4"),
            ]);

        Assert.True(result.Available);
        Assert.DoesNotContain("rather than", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewestOfSeveralUpdatesIsTheOneOffered()
    {
        // Not the next one along: nobody wants to step through four versions one at a time.
        var result = PackUpdateCheck.Evaluate(
            "v1",
            "1.0.0",
            [
                V("v4", "1.3.0", VersionType.Release),
                V("v3", "1.2.0", VersionType.Release),
                V("v2", "1.1.0", VersionType.Release),
                V("v1", "1.0.0", VersionType.Release),
            ]);

        Assert.Equal("v4", result.Newest?.FileId);
    }

    [Fact]
    public void OrderComesFromThePlatformRatherThanFromParsingTheNames()
    {
        /*
         * A pack's numbering is whatever its author felt like -- "Release 12", "1.20.1-4",
         * "v14.0.0-beta.6". Ordering those with a version comparer means inventing a rule the author
         * never agreed to. Modrinth already returns them newest-first, so position is the answer.
         *
         * Asserted with names that sort the WRONG WAY on purpose.
         */
        var result = PackUpdateCheck.Evaluate(
            "old",
            "9.9.9",
            [V("new", "1.0.0", VersionType.Release), V("old", "9.9.9", VersionType.Release)]);

        Assert.True(result.Available);
        Assert.Equal("new", result.Newest?.FileId);
    }

    [Fact]
    public void AnUnnamedInstalledVersionStillReadsAsASentence()
    {
        var result = PackUpdateCheck.Evaluate(
            "v1",
            string.Empty,
            [V("v2", "1.1.0", VersionType.Release), V("v1", string.Empty, VersionType.Release)]);

        Assert.Contains("an unnamed version", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoVersionIdMeansNothingToCompare()
    {
        // A file-imported pack. It should never reach here -- CanCheckForPackUpdates is false for one
        // -- but answering "you are up to date" if it did would be a lie.
        var result = PackUpdateCheck.Evaluate(
            string.Empty,
            "1.0.0",
            [V("v2", "1.1.0", VersionType.Release)]);

        Assert.True(result.Inconclusive);
    }
}
