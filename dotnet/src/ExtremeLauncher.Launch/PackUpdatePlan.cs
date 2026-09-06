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
 * Ported from ModrinthCreationTask::updateInstance in
 * launcher/modplatform/modrinth/ModrinthInstanceCreationTask.cpp.
 *
 * WHAT AN UPDATE WOULD DO TO A FOLDER. The hard half of updating a modpack is not downloading the new
 * version -- it is working out which of the files already there belong to the pack and may be
 * replaced, and which belong to the player and must not be touched.
 *
 * THE LEDGER IS THE ONLY THING THAT MAKES THIS ANSWERABLE. Nothing about a jar on disk says who put
 * it there. Without <instance>/mrpack/, the honest answer is "I cannot tell", and this refuses to
 * plan a single deletion rather than guessing -- which is a DIVERGENCE from upstream, whose
 * equivalent shows a "this may cause some of the files to be duplicated" warning and carries on.
 *
 * MATCHING IS BY HASH, upstream's rule and the right one: a mod whose file is byte for byte identical
 * in both versions is neither re-downloaded nor removed, whatever it happens to be called. Pack
 * authors rename and re-path files between releases far more often than they change them.
 *
 * NOTHING HERE TOUCHES THE DISK. It produces a plan; something else has to look at it, show it to
 * somebody, and act. That split is deliberate: the decision to delete forty files is one a person
 * should get to see before it happens.
 */

using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.Launch;

/// <summary>What updating from one pack version to another would involve.</summary>
public sealed record PackUpdatePlan
{
    /// <summary>Files the new version needs that the old one did not have.</summary>
    public IReadOnlyList<ModrinthPackFile> ToDownload { get; init; } = [];

    /// <summary>Paths the old version left that the new one does not have at all. Gone for good.</summary>
    public IReadOnlyList<string> ToRemove { get; init; } = [];

    /// <summary>
    /// The pack's own config files, which are deleted and written again from the new version.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM ToRemove BECAUSE THE NUMBERS MEAN DIFFERENT THINGS TO A PERSON, and running a
    /// real update is what showed it: updating Fabulously Optimized by one release reported "remove
    /// 71 files", of which 46 were overrides about to be re-written a second later. Anybody reading
    /// that before agreeing would reasonably think they were losing something.
    ///
    /// They are still deleted -- see the note on overrides below -- but they are counted apart.
    /// </remarks>
    public IReadOnlyList<string> ToReplace { get; init; } = [];

    /// <summary>Everything the update deletes, in the order it should be deleted.</summary>
    public IEnumerable<string> AllRemovals => ToRemove.Concat(ToReplace);

    /// <summary>Files present in both versions, untouched.</summary>
    public int Unchanged { get; init; }

    /// <summary>Whether the plan can be carried out at all.</summary>
    public bool Possible { get; init; }

    /// <summary>Why not, when it cannot.</summary>
    public string Blocker { get; init; } = string.Empty;

    /// <summary>A sentence describing the plan, for the confirmation.</summary>
    public string Summary { get; init; } = string.Empty;
}

public static class PackUpdatePlanner
{
    /// <summary>Works out what updating to <paramref name="newManifest"/> would do.</summary>
    /// <param name="ledger">What the installed version recorded about itself.</param>
    /// <param name="newManifest">The version being moved to.</param>
    /// <param name="newOverridePaths">
    /// What the new version's overrides folders contain, when the caller has the pack open and can
    /// say. Without it every old override counts as a replacement rather than a removal, which errs
    /// towards the reassuring answer -- so the caller SHOULD supply it when it can.
    /// </param>
    public static PackUpdatePlan Create(
        PackLedger ledger,
        ModrinthPackManifest newManifest,
        IReadOnlyCollection<string>? newOverridePaths = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(newManifest);

        if (!ledger.Exists)
        {
            /*
             * NO LEDGER, NO PLAN. Every pack this launcher installed before the ledger existed lands
             * here, as does one imported from a file by a launcher that does not keep one.
             *
             * Upstream carries on regardless, warning that files "may be duplicated" -- which
             * undersells it: without the old manifest it cannot remove the old version's mods at all,
             * so the instance ends up running two copies of half its mod list, which is a crash and a
             * confusing one. Refusing is the better answer, and reinstalling is a real alternative.
             */
            return new PackUpdatePlan
            {
                Possible = false,
                Blocker = "This instance has no record of which files came from the pack, so an update "
                          + "cannot tell them apart from files you added yourself. Installing the newer "
                          + "version as a separate instance is the safe way to move.",
            };
        }

        var oldFiles = ledger.Manifest!.Files;

        /*
         * Hash to path, for the old version. A pack CAN list the same file twice under different
         * paths, so this keeps every path a hash appeared at rather than the last one.
         */
        var oldByHash = new Dictionary<string, List<ModrinthPackFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in oldFiles)
        {
            if (HashOf(file) is not { Length: > 0 } hash)
            {
                continue;
            }

            if (!oldByHash.TryGetValue(hash, out var list))
            {
                oldByHash[hash] = list = [];
            }

            list.Add(file);
        }

        var toDownload = new List<ModrinthPackFile>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unchanged = 0;

        foreach (var file in newManifest.Files)
        {
            var hash = HashOf(file);

            if (hash.Length != 0 && oldByHash.ContainsKey(hash))
            {
                // Byte for byte the same file. Not fetched again, and not removed below.
                matched.Add(hash);
                unchanged++;

                continue;
            }

            toDownload.Add(file);
        }

        var toRemove = new List<string>();

        foreach (var (hash, files) in oldByHash)
        {
            if (matched.Contains(hash))
            {
                continue;
            }

            foreach (var file in files.Where(f => f.Path.Length != 0))
            {
                toRemove.Add(Normalise(file.Path));
            }
        }

        /*
         * EVERY OVERRIDE THE OLD PACK WROTE, which is upstream's rule and the uncomfortable one: an
         * override is a config file, and a player may well have edited it since. Upstream removes
         * them all and lets the new pack's copies land.
         *
         * Kept, because the alternative is worse in a way that is harder to see -- a pack that
         * changes a config's format leaves the instance running the old file, and the failure that
         * produces looks nothing like "my edits were kept". The mitigation is that the plan SAYS how
         * many, so somebody can be shown the number before agreeing to it.
         */
        var incoming = newOverridePaths is null
            ? null
            : new HashSet<string>(newOverridePaths.Select(Normalise), StringComparer.OrdinalIgnoreCase);

        var toReplace = new List<string>();

        foreach (var path in ledger.Overrides.Where(p => p.Length != 0).Select(Normalise))
        {
            /*
             * A config the new version ALSO ships is refreshed, not lost. One it has dropped really
             * is gone. Told apart only when the caller could open the new pack and say; without that
             * list everything counts as a refresh, which is the reassuring answer and therefore the
             * one that has to be earned rather than assumed -- hence PrepareAsync always supplies it.
             */
            if (incoming is null || incoming.Contains(path))
            {
                toReplace.Add(path);
            }
            else
            {
                toRemove.Add(path);
            }
        }

        // The same path can arrive from both halves -- an override that is also a listed file -- and
        // deleting it twice is not wrong but counting it twice would misreport the plan.
        var distinctRemovals = toRemove.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var distinctReplacements = toReplace
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !distinctRemovals.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return new PackUpdatePlan
        {
            Possible = true,
            ToDownload = toDownload,
            ToRemove = distinctRemovals,
            ToReplace = distinctReplacements,
            Unchanged = unchanged,
            Summary = Describe(toDownload.Count, distinctRemovals.Count, distinctReplacements.Count, unchanged),
        };
    }

    private static string Describe(int download, int remove, int replace, int unchanged)
    {
        var parts = new List<string>();

        if (download != 0)
        {
            parts.Add($"download {download} file{(download == 1 ? string.Empty : "s")}");
        }

        if (replace != 0)
        {
            // Worded as a refresh rather than a removal because that is what it is: the file goes and
            // the new version's copy lands in the same place a moment later.
            parts.Add($"refresh {replace} of the pack's own config file{(replace == 1 ? string.Empty : "s")}");
        }

        if (remove != 0)
        {
            parts.Add($"remove {remove} file{(remove == 1 ? string.Empty : "s")}");
        }

        if (parts.Count == 0)
        {
            return unchanged == 0
                ? "There is nothing to change."
                : $"Nothing would change: all {unchanged} of the pack's files are already in place.";
        }

        /*
         * "A and B and C" is what a naive join produces, and it reads like a child listing things.
         * Commas for all but the last pair.
         */
        var joined = parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };

        var sentence = "This would " + joined + ".";

        // The count that makes the other two readable: "remove 40 files" alone sounds like most of
        // the instance, and "and leave 180 alone" is what says it is not.
        return unchanged != 0
            ? sentence + $" {unchanged} file{(unchanged == 1 ? string.Empty : "s")} would be left alone."
            : sentence;
    }

    /// <summary>The file's hash as hex, or empty when the manifest gave none.</summary>
    /// <remarks>
    /// The .mrpack format requires sha512 on every file, and this port's parser keeps exactly that
    /// one -- so both sides of the comparison are always the same algorithm. A file with no hash at
    /// all is treated as unmatched, which errs towards downloading it again rather than towards
    /// removing something.
    /// </remarks>
    private static string HashOf(ModrinthPackFile file)
        => file.Hash.Length == 0 ? string.Empty : Convert.ToHexString(file.Hash);

    private static string Normalise(string path) => path.Replace('\\', '/').Trim();
}
