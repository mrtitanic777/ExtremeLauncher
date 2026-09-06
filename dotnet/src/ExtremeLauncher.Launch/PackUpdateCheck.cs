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
 * Ported in behaviour from the version-comparison half of
 * launcher/ui/pages/instance/ManagedPackPage.cpp.
 *
 * IS THERE A NEWER VERSION OF THIS PACK? Answerable at all only since wave 39, because it needs the
 * project id that browsing records and importing a file cannot.
 *
 * THE COMPARISON IS BY POSITION, NOT BY PARSING THE VERSION STRING. Modrinth returns a project's
 * versions newest-first, and a pack's own numbering is whatever its author felt like -- "5.9.2",
 * "v14.0.0-beta.6", "1.20.1-4", "Release 12". Trying to order those with a version comparer means
 * inventing a rule the author never agreed to; the platform already knows the order and says so.
 *
 * WHAT COUNTS AS AN UPDATE is the real decision here, and it is deliberately conservative: see
 * Evaluate.
 */

using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.Launch;

/// <summary>What a check found.</summary>
public sealed record PackUpdateResult
{
    /// <summary>Whether a newer version is worth telling the user about.</summary>
    public bool Available { get; init; }

    /// <summary>A sentence for the window.</summary>
    public required string Message { get; init; }

    /// <summary>The version being offered, when there is one.</summary>
    public IndexedVersion? Newest { get; init; }

    /// <summary>Whether the check could not reach a conclusion at all.</summary>
    public bool Inconclusive { get; init; }
}

public static class PackUpdateCheck
{
    /// <summary>
    /// Decides whether any of <paramref name="versions"/> is a newer release than the one installed.
    /// </summary>
    /// <param name="currentVersionId">The platform's id for the installed version.</param>
    /// <param name="currentVersionName">What the installed version calls itself, for the message.</param>
    /// <param name="versions">The project's versions, newest first, as the platform returns them.</param>
    /// <remarks>
    /// THREE RULES, and each exists to avoid a specific way of being annoying or wrong:
    ///
    ///   A BETA IS NOT AN UPDATE for somebody on a stable release. Fabulously Optimized's four newest
    ///   versions are betas for a Minecraft snapshot; telling a player on 13.3.0 that 14.0.0-beta.6 is
    ///   "an update" would push them onto a pre-release build of a game version they do not have.
    ///   Somebody already ON a beta is a different case -- they opted in, so anything newer counts.
    ///
    ///   AN UNKNOWN CURRENT VERSION IS INCONCLUSIVE, not "up to date" and not "update available". A
    ///   version the author has withdrawn disappears from the list, and neither guess is honest: the
    ///   installed build might be older than everything or newer than everything.
    ///
    ///   A DIFFERENT MINECRAFT VERSION IS STILL OFFERED, but the message says so. This is the one
    ///   place where being conservative would be wrong -- packs move to a new Minecraft version and
    ///   that IS the update -- but it changes what the instance is, so it must not slip past silently.
    /// </remarks>
    public static PackUpdateResult Evaluate(
        string currentVersionId,
        string currentVersionName,
        IReadOnlyList<IndexedVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        if (versions.Count == 0)
        {
            return new PackUpdateResult
            {
                Available = false,
                Inconclusive = true,
                Message = "This pack has no published versions, which is odd enough to be worth a look on the site.",
            };
        }

        var index = IndexOf(versions, currentVersionId);

        if (index < 0)
        {
            /*
             * The installed version is not in the list any more -- withdrawn, or the pack was
             * republished. Saying "up to date" would be a guess, and so would "update available".
             */
            return new PackUpdateResult
            {
                Available = false,
                Inconclusive = true,
                Message = $"The installed version ({Describe(currentVersionName)}) is no longer listed on the "
                          + $"platform, so there is nothing to compare against. The newest available is "
                          + $"{versions[0].Version}.",
                Newest = versions[0],
            };
        }

        if (index == 0)
        {
            return new PackUpdateResult
            {
                Available = false,
                Message = $"This is the newest version of the pack ({Describe(currentVersionName)}).",
            };
        }

        var current = versions[index];

        // Everything published after the installed one. The list is newest-first, so that is the
        // slice in front of it.
        var newer = versions.Take(index).ToList();

        /*
         * A stable install only counts stable releases as updates; a pre-release install counts
         * anything. The author's own channel is the signal, and it is the only one that does not
         * require guessing at their numbering.
         */
        var candidates = current.VersionType == VersionType.Release
            ? newer.Where(v => v.VersionType == VersionType.Release).ToList()
            : newer;

        if (candidates.Count == 0)
        {
            var betas = newer.Count;

            return new PackUpdateResult
            {
                Available = false,
                Message = betas == 1
                    ? $"You are on the newest release ({Describe(currentVersionName)}). There is 1 newer "
                      + "pre-release version, which this does not offer."
                    : $"You are on the newest release ({Describe(currentVersionName)}). There are {betas} newer "
                      + "pre-release versions, which this does not offer.",
            };
        }

        var newest = candidates[0];

        var moved = MinecraftChanged(current, newest);

        var message = $"{newest.Version} is available (you have {Describe(currentVersionName)}).";

        if (moved.Length != 0)
        {
            // The one thing that must not slip past silently: a pack that has moved to a different
            // Minecraft version is a different thing from the one that is installed.
            message += $" {moved}";
        }

        return new PackUpdateResult { Available = true, Message = message, Newest = newest };
    }

    /// <summary>Says so when the newer version is for a different Minecraft, or empty when it is not.</summary>
    private static string MinecraftChanged(IndexedVersion current, IndexedVersion newest)
    {
        if (current.McVersion.Count == 0 || newest.McVersion.Count == 0)
        {
            return string.Empty;
        }

        // Overlapping at all is enough: a pack often supports several, and dropping one the player
        // does not use is not a move.
        if (current.McVersion.Intersect(newest.McVersion, StringComparer.Ordinal).Any())
        {
            return string.Empty;
        }

        return $"Note that it is for Minecraft {string.Join(", ", newest.McVersion)} "
               + $"rather than {string.Join(", ", current.McVersion)}.";
    }

    private static int IndexOf(IReadOnlyList<IndexedVersion> versions, string versionId)
    {
        if (versionId.Length == 0)
        {
            return -1;
        }

        for (var i = 0; i < versions.Count; i++)
        {
            if (string.Equals(versions[i].FileId, versionId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Describe(string versionName)
        => versionName.Length != 0 ? versionName : "an unnamed version";
}
