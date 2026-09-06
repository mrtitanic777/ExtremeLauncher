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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from launcher/minecraft/launch/ScanModFolders.{h,cpp}.
 *
 * Reads the instance's mod folders before the game starts, so the log records what was loaded. That is
 * its whole purpose: when a user reports a crash, the first question is which mods were installed, and
 * this is what answers it.
 *
 * NEVER FAILS A LAUNCH. A folder that cannot be read is worth a warning and nothing more -- refusing
 * to start the game because a mod list could not be enumerated would be a worse outcome than starting
 * without the list.
 *
 * Upstream runs three folder scans concurrently and joins them with three bool members and a
 * checkDone() called from each completion slot. Here it is a loop.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Mods;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class ScanModFolders : LaunchStep
{
    private readonly string _gameRoot;

    public ScanModFolders(string gameRoot) : base("Scan mod folders") => _gameRoot = gameRoot;

    /// <summary>What was found, keyed by filename. Available once the step has run.</summary>
    public IReadOnlyDictionary<string, FolderEntry> Mods { get; private set; }
        = new Dictionary<string, FolderEntry>(StringComparer.Ordinal);

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, FolderEntry>(StringComparer.Ordinal);

        foreach (var folder in ResourceFolder.ModFolderNames)
        {
            var path = FileSystem.PathCombine(_gameRoot, folder);

            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                foreach (var (id, entry) in ResourceFolder.LoadMods(path))
                {
                    found.TryAdd(id, entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogLine($"Could not scan {folder}: {e.Message}", MessageLevel.Warning);
            }
        }

        Mods = found;

        if (found.Count == 0)
        {
            return Task.CompletedTask;
        }

        var lines = new List<string> { $"Mods ({found.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}):" };

        foreach (var entry in found.Values.OrderBy(e => e.Resource.Name, StringComparer.OrdinalIgnoreCase))
        {
            // The filename first, because that is what a user sees in their folder and what they will
            // rename or delete; the mod's own name and version follow when it declared them.
            var detail = entry.Resource is Mod { Details: { ModId.Length: > 0 } details }
                ? $" ({details.ModId} {details.Version})"
                : string.Empty;

            var state = entry.Resource.Enabled ? string.Empty : " [disabled]";

            lines.Add($"  {entry.Resource.InternalId}{detail}{state}");
        }

        Log(lines, MessageLevel.Launcher);

        return Task.CompletedTask;
    }
}
