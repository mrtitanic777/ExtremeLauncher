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
 * Ported from launcher/DataMigrationTask.{h,cpp} and the migration setup in Application.cpp.
 *
 * ADOPTING A MULTIMC OR POLYMC DATA DIRECTORY. This lineage of launchers has forked repeatedly and the
 * data layout barely changed, so a new install offers to bring the old one across: instances, accounts,
 * icons, the shared library and asset caches.
 *
 * IT IS A WHITELIST, and that is the whole safety property. The old data directory contains whatever
 * else its owner put there, and the launcher copies only the eleven paths it actually understands --
 * so an unknown file is left behind rather than imported into a directory this launcher will later
 * write to. The list is upstream's, verbatim, including the entry for this launcher's OWN config file
 * whose comment reads "it is possible that we already used that directory before".
 *
 * TWO PASSES, and the first is not a diagnostic: it counts what will be copied so the progress bar has
 * a denominator. Both passes apply the same filter. See FileCopy.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public static class DataMigration
{
    /// <summary>
    /// Everything worth bringing from an older launcher's data directory.
    /// </summary>
    /// <param name="oldConfigFile">
    /// The other launcher's settings file, e.g. "multimc.cfg". Named separately because it differs per
    /// fork, and its settings still apply once renamed.
    /// </param>
    /// <remarks>
    /// Prefix matches, so "instances/" takes the whole tree under it. Order is irrelevant -- any match
    /// admits the file.
    /// </remarks>
    public static IPathMatcher CreateMatcher(string oldConfigFile)
    {
        var matcher = new MultiMatcher();

        matcher.Add(new SimplePrefixMatcher(oldConfigFile));

        // This launcher's own config, because the directory may already have been used by it.
        matcher.Add(new SimplePrefixMatcher(BuildConfig.Instance.LauncherConfigFile));

        foreach (var prefix in (ReadOnlySpan<string>)[
            "logs/",
            "accounts.json",
            "accounts/",
            "assets/",
            "icons/",
            "instances/",
            "libraries/",
            "mods/",
            "themes/",
        ])
        {
            matcher.Add(new SimplePrefixMatcher(prefix));
        }

        return matcher;
    }

    /// <summary>
    /// Shortens a path for a one-line status message.
    /// </summary>
    /// <remarks>
    /// Upstream's numbers: over 50 characters becomes the first 20, an ellipsis, and the last 29 --
    /// which totals exactly 50. The tail gets more than the head deliberately, because the interesting
    /// part of a path being copied is its filename, not the directory it sits in.
    /// </remarks>
    public static string ShortenForDisplay(string relativeName)
    {
        ArgumentNullException.ThrowIfNull(relativeName);

        return relativeName.Length > 50
            ? relativeName[..20] + "…" + relativeName[^29..]
            : relativeName;
    }
}

/// <summary>Copies an older launcher's data directory into this one.</summary>
public sealed class DataMigrationTask : LauncherTask
{
    private readonly string _source;
    private readonly string _destination;
    private readonly IPathMatcher _matcher;

    public DataMigrationTask(string source, string destination, IPathMatcher matcher)
        : base("Migrating launcher data")
    {
        _source = source;
        _destination = destination;
        _matcher = matcher;
    }

    public override bool CanAbort => true;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus("Scanning files...");

        var copy = new FileCopy(_source, _destination)
            .Matcher(_matcher)
            .Whitelist(true);

        // Pass one: how much there is. Off the caller's thread -- a full data directory is a lot of
        // stat calls, and upstream hands both passes to a thread pool for the same reason.
        var total = await Task.Run(
            () =>
            {
                copy.Run(dryRun: true);

                return copy.TotalCopied;
            },
            cancellationToken).ConfigureAwait(false);

        SetProgress(0, total);

        copy.FileCopied += (_, relative) =>
        {
            SetProgress(copy.TotalCopied, total);
            SetStatus($"Copying {DataMigration.ShortenForDisplay(relative)}…");
        };

        var succeeded = await Task.Run(() => copy.Run(), cancellationToken).ConfigureAwait(false);

        /*
         * Upstream reports "Some paths could not be copied!" and stops there. Naming a few of them
         * costs nothing and is the difference between a user who can fix the problem and one who
         * cannot -- a locked file or a full disk looks identical otherwise.
         */
        if (!succeeded)
        {
            var examples = string.Join('\n', copy.Failed.Take(5));
            var more = copy.Failed.Count > 5 ? $"\n...and {copy.Failed.Count - 5} more" : string.Empty;

            throw new TaskFailedException($"Some paths could not be copied!\n{examples}{more}");
        }
    }
}
