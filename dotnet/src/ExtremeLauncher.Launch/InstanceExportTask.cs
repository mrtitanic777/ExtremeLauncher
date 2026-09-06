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
 * Ported in behaviour from launcher/ui/dialogs/ExportInstanceDialog.cpp.
 *
 * GETTING AN INSTANCE BACK OUT AGAIN. The launcher could import a modpack from wave 14 and had no way
 * to produce one -- so an instance somebody spent an evening configuring could be moved to another
 * machine only by copying the folder by hand and hoping.
 *
 * A PLAIN ZIP OF THE FOLDER, which is upstream's instance export: it is not a modpack format and does
 * not pretend to be. Everything is in it, mods and worlds and configs alike, so it restores exactly
 * what was there -- and it needs no network, no metadata and no provider.
 *
 * THE DEFAULT EXCLUSIONS ARE THE POINT. An instance folder holds hundreds of megabytes of things that
 * should not travel: the game's own caches, the log files, the crash reports. Upstream's dialog opens
 * with a tree and lets you untick; the same effect is had here by starting from a sensible set and
 * letting the caller add to it.
 */

using System.IO.Compression;
using ExtremeLauncher.Core;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class InstanceExportTask : LauncherTask
{
    /// <summary>
    /// What is left out unless asked for.
    /// </summary>
    /// <remarks>
    /// Relative to the instance folder, matched as path prefixes. Every one of these is either
    /// regenerated on the next launch or is a record of a run that has already happened:
    ///
    ///   .minecraft/logs, crash-reports   the last run's noise, often the biggest thing in the folder
    ///   .minecraft/assets, versions      shared caches when they exist inside an instance at all
    ///   .minecraft/screenshots           somebody else's screenshots are not part of a pack
    ///   .index                           packwiz metadata, meaningful only to this launcher
    ///   instance.cfg's siblings          NOT excluded: the instance's own configuration IS the point
    /// </remarks>
    public static readonly string[] DefaultExclusions =
    [
        ".minecraft/logs",
        ".minecraft/crash-reports",
        ".minecraft/assets",
        ".minecraft/versions",
        ".minecraft/screenshots",
        ".minecraft/.fabric",
        ".minecraft/realms_persistence.json",
        "minecraft/logs",
        "minecraft/crash-reports",
        "minecraft/assets",
        "minecraft/versions",
        "minecraft/screenshots",
        "minecraft/.fabric",
        "minecraft/realms_persistence.json",
    ];

    private readonly string _instanceFolder;

    private readonly string _targetPath;

    private readonly HashSet<string> _exclusions;

    /// <param name="exclusions">
    /// Relative paths to leave out, or null for <see cref="DefaultExclusions"/>. An empty set exports
    /// everything, which is a legitimate thing to want when the point is a backup rather than a share.
    /// </param>
    public InstanceExportTask(
        string instanceFolder,
        string targetPath,
        IEnumerable<string>? exclusions = null)
        : base("Exporting instance")
    {
        _instanceFolder = instanceFolder;
        _targetPath = targetPath;

        _exclusions = new HashSet<string>(
            (exclusions ?? DefaultExclusions).Select(Normalise),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How many files went in.</summary>
    public int FileCount { get; private set; }

    /// <summary>How big the archive turned out.</summary>
    public long ArchiveSize { get; private set; }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_instanceFolder))
        {
            throw new LauncherException($"There is no instance at {_instanceFolder}.");
        }

        SetStatus("Working out what to include");

        var files = new List<string>();

        if (!MMCZip.CollectFileListRecursively(_instanceFolder, null, files, IsExcluded))
        {
            throw new LauncherException("Could not read the instance folder.");
        }

        if (files.Count == 0)
        {
            // Refused rather than written: a zip with nothing in it looks like a successful export
            // right up until somebody tries to use it.
            throw new LauncherException("There is nothing to export -- everything was excluded.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        SetStatus($"Compressing {files.Count} files");

        /*
         * WRITTEN TO A TEMPORARY FILE and moved into place. An export is usually aimed at a folder the
         * user is watching, and a half-written zip that appears under the name they chose is one they
         * will pick up and send to somebody.
         */
        var temporary = _targetPath + ".part";

        try
        {
            if (!MMCZip.CompressDirFiles(temporary, _instanceFolder, files, followSymlinks: true))
            {
                throw new LauncherException("Could not write the archive.");
            }

            ArchiveSize = new FileInfo(temporary).Length;
            FileCount = files.Count;

            File.Move(temporary, _targetPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Could not write {_targetPath}: {e.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // Best effort; the export already failed and this is only tidying.
                }
            }
        }

        SetProgress(1, 1);

        return Task.CompletedTask;
    }

    /// <summary>Whether a path relative to the instance folder is left out.</summary>
    /// <remarks>
    /// Prefix matching on path SEGMENTS, not a plain StartsWith. ".minecraft/logs" must not also
    /// exclude ".minecraft/logsomething", which a naive prefix test would.
    /// </remarks>
    private bool IsExcluded(string relativePath)
    {
        var path = Normalise(relativePath);

        foreach (var exclusion in _exclusions)
        {
            if (string.Equals(path, exclusion, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(exclusion + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalise(string path)
        => path.Replace('\\', '/').Trim('/');
}
