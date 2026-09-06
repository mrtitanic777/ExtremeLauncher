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
 * Ported from launcher/InstanceList.{h,cpp} -- discovery, loading, and the group file.
 *
 * WHAT MAKES A DIRECTORY AN INSTANCE: an instance.cfg inside it. Nothing else. The folder name is the
 * instance's id, which is why renaming an instance in the UI does not rename its folder -- the id has
 * to stay stable or the group file and every reference to it break.
 *
 * NOT PORTED: the QAbstractListModel surface (rows, roles, drag and drop), the filesystem watcher, and
 * the trash/undo mechanism, which needs the UI to offer the undo. Discovery, loading, grouping and the
 * on-disk group file are here, and those are what the UI wave will build a model over.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.Launch;

/// <summary>One instance on disk, as the list knows it.</summary>
public sealed class InstanceRecord
{
    public InstanceRecord(string id, InstancePaths paths, InstanceSettings settings)
    {
        Id = id;
        Paths = paths;
        Settings = settings;
    }

    /// <summary>The folder name. Stable for the instance's whole life.</summary>
    public string Id { get; }

    public InstancePaths Paths { get; }

    public InstanceSettings Settings { get; }

    /// <summary>The name the user sees, which is free to change without moving the folder.</summary>
    public string Name => Settings.Name;

    /// <summary>
    /// What kind of instance this is.
    /// </summary>
    /// <remarks>
    /// An EMPTY type is treated as "OneSix", the normal kind. Some launcher versions did not write
    /// InstanceType at all, so refusing to load those would hide instances that work perfectly well.
    /// </remarks>
    public bool IsSupported
        => Settings.InstanceType is "OneSix" or "";

    public override string ToString() => $"{Id} ({Name})";
}

public sealed class InstanceList
{
    /// <summary>The group file's format version. A different one is refused rather than guessed at.</summary>
    private const int GroupFileFormatVersion = 1;

    private const string GroupFileName = "instgroups.json";

    private readonly SettingsObject _globalSettings;

    private readonly Dictionary<string, InstanceRecord> _instances = new(StringComparer.Ordinal);

    /// <summary>Instance id to group name. An instance absent from this is ungrouped.</summary>
    private readonly Dictionary<string, string> _groupIndex = new(StringComparer.Ordinal);

    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);

    private bool _probed;

    public InstanceList(string instancesDirectory, SettingsObject globalSettings)
    {
        InstancesDirectory = instancesDirectory;
        _globalSettings = globalSettings;
    }

    public string InstancesDirectory { get; }

    public IReadOnlyCollection<InstanceRecord> Instances => _instances.Values;

    public int Count => _instances.Count;

    public InstanceRecord? GetInstanceById(string id) => _instances.GetValueOrDefault(id);

    // ================================================================== discovery

    /// <summary>
    /// Finds every instance directory.
    /// </summary>
    /// <remarks>
    /// A directory qualifies when it holds an instance.cfg, and NOTHING ELSE is required -- a
    /// half-created instance with no mmc-pack.json still shows up, because hiding it would leave the
    /// user unable to see or delete it.
    ///
    /// A SYMLINK THAT LEADS BACK INTO THE INSTANCES FOLDER IS SKIPPED. Without that, an instance
    /// linked to its own parent is discovered twice under two ids, and the group file ends up with a
    /// phantom entry that can never be cleaned up.
    /// </remarks>
    public List<string> DiscoverInstances()
    {
        var found = new List<string>();

        if (!Directory.Exists(InstancesDirectory))
        {
            _probed = true;
            return found;
        }

        var root = Path.GetFullPath(InstancesDirectory);

        foreach (var directory in Directory.EnumerateDirectories(InstancesDirectory))
        {
            if (!File.Exists(FileSystem.PathCombine(directory, "instance.cfg")))
            {
                continue;
            }

            if (LeadsBackIntoInstancesFolder(directory, root))
            {
                continue;
            }

            found.Add(Path.GetFileName(directory));
        }

        _probed = true;
        return found;
    }

    private static bool LeadsBackIntoInstancesFolder(string directory, string root)
    {
        try
        {
            var info = new DirectoryInfo(directory);

            if (info.LinkTarget is null)
            {
                return false;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;

            return target is not null
                   && string.Equals(Path.GetFullPath(Path.Combine(target, "..")), root, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Discovers and loads every instance, replacing what was known before.</summary>
    public void LoadList()
    {
        LoadGroupList();

        _instances.Clear();

        foreach (var id in DiscoverInstances())
        {
            if (LoadInstance(id) is { } record)
            {
                _instances[id] = record;
            }
        }
    }

    /// <summary>Loads one instance by id.</summary>
    public InstanceRecord? LoadInstance(string id)
    {
        var root = FileSystem.PathCombine(InstancesDirectory, id);

        if (!File.Exists(FileSystem.PathCombine(root, "instance.cfg")))
        {
            return null;
        }

        var paths = new InstancePaths(root);
        var settings = InstanceSettings.Open(paths, _globalSettings);

        return new InstanceRecord(id, paths, settings);
    }

    // ================================================================== groups

    /// <summary>The group an instance belongs to, or an empty string when it is ungrouped.</summary>
    public string GetInstanceGroup(string id) => _groupIndex.GetValueOrDefault(id, string.Empty);

    /// <summary>Moves an instance into a group. An empty name removes it from any group.</summary>
    public void SetInstanceGroup(string id, string group)
    {
        group = group.Trim();

        if (group.Length == 0)
        {
            _groupIndex.Remove(id);
        }
        else
        {
            _groupIndex[id] = group;
        }

        SaveGroupList();
    }

    // ================================================================== removing an instance

    /// <summary>One instance moved to the trash, remembered so it can be put back.</summary>
    /// <param name="Id">The instance id it had.</param>
    /// <param name="OriginalPath">Where it was.</param>
    /// <param name="TrashPath">Where it went.</param>
    /// <param name="Group">The group it was in, so undo restores that too.</param>
    public sealed record TrashedInstance(string Id, string OriginalPath, string TrashPath, string Group);

    private readonly Stack<TrashedInstance> _trashHistory = new();

    /// <summary>Whether there is anything an undo could bring back.</summary>
    public bool TrashedSomething => _trashHistory.Count != 0;

    /// <summary>
    /// Moves an instance to the desktop trash.
    /// </summary>
    /// <remarks>
    /// PREFERRED OVER <see cref="DeleteInstance"/> WHEREVER IT WORKS. An instance directory holds
    /// worlds nobody else has a copy of, and the difference between the two methods is the difference
    /// between a misclick and a loss.
    ///
    /// Returns false when this platform or filesystem has no trash -- a normal state, not an error.
    /// The caller is expected to ask whether to delete permanently instead.
    /// </remarks>
    public bool TrashInstance(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var instance = GetInstanceById(id);

        if (instance is null)
        {
            // Deleted from outside the launcher, most likely. Nothing to do and nothing wrong.
            return false;
        }

        var group = GetInstanceGroup(id);

        /*
         * UPSTREAM BUG #18, FIXED HERE by ordering. InstanceList::trashInstance removes the instance
         * from the group index and SAVES THE GROUP LIST before calling FS::trash, then returns false if
         * the trash fails -- leaving an instance that is still on disk but no longer in its group, with
         * that loss already written to instgroups.json.
         *
         * Trash failure is not exotic: upstream returns false outright under Flatpak and on Windows
         * Server, and any filesystem without a reachable trash does the same. So the user presses
         * Delete, is told it did not work, and quietly loses their grouping.
         *
         * Nothing is recorded here until the directory has actually moved.
         */
        if (!Trash.TryTrash(instance.Paths.InstanceRoot, out var trashedPath))
        {
            return false;
        }

        _instances.Remove(id);

        if (_groupIndex.Remove(id))
        {
            SaveGroupList();
        }

        /*
         * Only undoable when the platform said where it put it. Windows does not, and its Recycle Bin
         * has its own Restore -- offering an Undo that cannot work would be worse than offering none.
         */
        if (trashedPath.Length != 0)
        {
            _trashHistory.Push(new TrashedInstance(id, instance.Paths.InstanceRoot, trashedPath, group));
        }

        return true;
    }

    /// <summary>
    /// Puts back the last instance moved to the trash.
    /// </summary>
    /// <returns>The id it was restored under, or empty when there was nothing to restore.</returns>
    public string UndoTrashInstance()
    {
        if (_trashHistory.Count == 0)
        {
            return string.Empty;
        }

        var top = _trashHistory.Pop();

        var id = top.Id;
        var path = top.OriginalPath;

        /*
         * Upstream's collision handling, kept: while something already sits where this came from, add
         * a "1" to both the id and the path and try again. It is a strange scheme -- "Pack" becomes
         * "Pack1", then "Pack11" -- but it is what existing installs would produce, and inventing a
         * tidier one would restore under a different name than the launcher it came from.
         */
        while (Directory.Exists(path) || File.Exists(path))
        {
            id += "1";
            path += "1";
        }

        Directory.Move(top.TrashPath, path);

        if (top.Group.Length != 0)
        {
            _groupIndex[id] = top.Group;
            SaveGroupList();
        }

        // Read back rather than reconstructed: whatever is in that directory is the truth about it --
        // and REGISTERED in the list, not just constructed. LoadInstance returns a record; LoadList
        // assigns it into _instances and so must this, or the restored instance is on disk but absent
        // from the list, and GetInstanceById cannot find what Undo just brought back.
        if (LoadInstance(id) is { } restored)
        {
            _instances[id] = restored;
        }

        return id;
    }

    /// <summary>
    /// Deletes an instance permanently.
    /// </summary>
    /// <remarks>
    /// IRREVERSIBLE, AND THERE IS NO SECOND CHANCE AFTER IT. Only for when <see cref="TrashInstance"/>
    /// has already failed and the user has been asked and said yes anyway. Ordered the same way as
    /// trashing, and for the same reason -- see upstream bug #18 above.
    /// </remarks>
    public bool DeleteInstance(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var instance = GetInstanceById(id);

        if (instance is null)
        {
            return false;
        }

        if (!FileSystem.DeletePath(instance.Paths.InstanceRoot))
        {
            return false;
        }

        _instances.Remove(id);

        if (_groupIndex.Remove(id))
        {
            SaveGroupList();
        }

        return true;
    }

    /// <summary>Every group that currently has at least one instance in it.</summary>
    /// <remarks>
    /// Derived rather than stored: a group is nothing but the set of instances naming it, so one whose
    /// last instance leaves stops existing. Upstream keeps a count alongside and they can disagree.
    /// </remarks>
    public List<string> GetGroups()
        => _groupIndex.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    public bool IsGroupCollapsed(string group) => _collapsedGroups.Contains(group);

    public void SetGroupCollapsed(string group, bool collapsed)
    {
        if (collapsed)
        {
            _collapsedGroups.Add(group);
        }
        else
        {
            _collapsedGroups.Remove(group);
        }

        SaveGroupList();
    }

    /// <summary>Removes a group, leaving its instances ungrouped.</summary>
    public void DeleteGroup(string name)
    {
        foreach (var id in _groupIndex.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList())
        {
            _groupIndex.Remove(id);
        }

        _collapsedGroups.Remove(name);
        SaveGroupList();
    }

    public void RenameGroup(string from, string to)
    {
        to = to.Trim();

        if (from == to || to.Length == 0)
        {
            return;
        }

        foreach (var id in _groupIndex.Where(kv => kv.Value == from).Select(kv => kv.Key).ToList())
        {
            _groupIndex[id] = to;
        }

        if (_collapsedGroups.Remove(from))
        {
            _collapsedGroups.Add(to);
        }

        SaveGroupList();
    }

    // ================================================================== the group file

    /// <summary>
    /// Writes instgroups.json.
    /// </summary>
    /// <remarks>
    /// REFUSES TO WRITE BEFORE DISCOVERY HAS RUN, which is upstream's guard and load-bearing: the file
    /// is written as a complete picture, so saving it while the instance list is still empty would
    /// erase every user's grouping. The same reasoning drops entries for instances that are not on
    /// disk -- an instance deleted outside the launcher should not keep its group forever.
    ///
    /// The file is keyed BY GROUP, not by instance, which is why this has to invert the index.
    /// </remarks>
    public void SaveGroupList()
    {
        if (!_probed)
        {
            return;
        }

        var byGroup = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var (id, group) in _groupIndex)
        {
            if (group.Length == 0 || !_instances.ContainsKey(id))
            {
                continue;
            }

            if (!byGroup.TryGetValue(group, out var members))
            {
                members = new SortedSet<string>(StringComparer.Ordinal);
                byGroup[group] = members;
            }

            members.Add(id);
        }

        var groups = new JsonObject();

        foreach (var (name, members) in byGroup)
        {
            var instances = new JsonArray();

            foreach (var id in members)
            {
                instances.Add(id);
            }

            groups[name] = new JsonObject
            {
                ["hidden"] = _collapsedGroups.Contains(name),
                ["instances"] = instances,
            };
        }

        // NOTE: the format version is written as a STRING, not a number. Inherited, and a launcher
        // that wrote a number here would be refused by every existing version.
        var root = new JsonObject
        {
            ["formatVersion"] = GroupFileFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["groups"] = groups,
        };

        // The ungrouped pseudo-group only appears when it is collapsed; there is nothing else to say
        // about it.
        if (_collapsedGroups.Contains(string.Empty))
        {
            root["ungrouped"] = new JsonObject { ["hidden"] = true };
        }

        try
        {
            FileSystem.EnsureFolderPathExists(InstancesDirectory);
            File.WriteAllText(FileSystem.PathCombine(InstancesDirectory, GroupFileName), root.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the grouping is not worth failing anything over; the instances are still there.
        }
    }

    /// <summary>Reads the format version, accepting either a JSON string or a JSON number.</summary>
    private static int ReadFormatVersion(JsonObject root)
    {
        if (root["formatVersion"] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
               && int.TryParse(text, System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    /// <summary>
    /// Reads instgroups.json.
    /// </summary>
    /// <remarks>
    /// EVERY FAILURE IS SILENT AND LEAVES THE GROUPING EMPTY, which is upstream's behaviour: a missing,
    /// unreadable, malformed or wrong-version file means the user sees an ungrouped list rather than an
    /// error they cannot act on. A malformed individual group is skipped and the rest still load.
    /// </remarks>
    public void LoadGroupList()
    {
        _groupIndex.Clear();
        _collapsedGroups.Clear();

        var path = FileSystem.PathCombine(InstancesDirectory, GroupFileName);

        if (!File.Exists(path))
        {
            return;
        }

        JsonObject root;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject parsed)
            {
                return;
            }

            root = parsed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return;
        }

        /*
         * READ LENIENTLY. The version is WRITTEN as a string -- see SaveGroupList -- and Qt reads it
         * back with toVariant().toInt(), which parses a JSON string or a JSON number alike. A strict
         * numeric read refuses every instgroups.json that exists, silently ungrouping every user's
         * whole library. An earlier draft of this did exactly that.
         */
        if (ReadFormatVersion(root) != GroupFileFormatVersion)
        {
            return;
        }

        if (root["groups"] is not JsonObject groups)
        {
            return;
        }

        foreach (var (name, value) in groups)
        {
            // An empty group name is the ungrouped pseudo-group, which has its own key.
            if (name.Length == 0 || value is not JsonObject group || group["instances"] is not JsonArray instances)
            {
                continue;
            }

            if (Json.EnsureBoolean(group, "hidden"))
            {
                _collapsedGroups.Add(name);
            }

            foreach (var id in instances)
            {
                if (id is JsonValue json && json.TryGetValue<string>(out var text) && text.Length != 0)
                {
                    _groupIndex[text] = name;
                }
            }
        }

        if (root["ungrouped"] is JsonObject ungrouped && Json.EnsureBoolean(ungrouped, "hidden"))
        {
            _collapsedGroups.Add(string.Empty);
        }
    }

    // ================================================================== staging

    /// <summary>The hidden directory staged instances are built in, inside the instances folder.</summary>
    /// <remarks>
    /// INSIDE the instances folder rather than the system temp directory, and that is deliberate: the
    /// commit is a MOVE, and a move across volumes is a copy. Staging next to the destination keeps it
    /// on the same filesystem, so committing a ten-gigabyte pack is a rename rather than a second full
    /// write of everything just downloaded.
    /// </remarks>
    public string StagingRoot => FileSystem.PathCombine(InstancesDirectory, ".tmp");

    /// <summary>Creates an empty directory for an instance being built.</summary>
    /// <remarks>
    /// Six characters of a GUID, retried on collision. Upstream gives up after 256 tries -- which
    /// cannot happen with six hex characters and a handful of concurrent imports, and is the right
    /// shape anyway: a loop that cannot terminate is worse than one that admits defeat.
    /// </remarks>
    public string CreateStagingPath()
    {
        var root = StagingRoot;

        for (var attempt = 0; attempt < 256; attempt++)
        {
            var candidate = FileSystem.PathCombine(root, Guid.NewGuid().ToString("N")[..6]);

            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate);
            HideStagingRoot(root);

            return candidate;
        }

        throw new FileSystemException("Could not create a staging directory for the new instance.");
    }

    /// <summary>
    /// Keeps the staging directory out of the user's way and out of the search index.
    /// </summary>
    /// <remarks>
    /// Upstream sets FILE_ATTRIBUTE_HIDDEN and FILE_ATTRIBUTE_NOT_CONTENT_INDEXED on Windows. The
    /// second matters more than it looks: an indexer opening files mid-extraction is the same class of
    /// interference as the antivirus the commit retries exist for. On other platforms the leading dot
    /// already does the hiding half.
    /// </remarks>
    private static void HideStagingRoot(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetAttributes(root, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cosmetic: a visible staging folder is untidy, not broken.
        }
    }

    /// <summary>Throws away a staging directory whose build failed.</summary>
    public void DestroyStagingPath(string stagingPath) => FileSystem.DeletePath(stagingPath);

    /// <summary>
    /// Moves a staged instance into the instances folder.
    /// </summary>
    /// <returns>
    /// False when the move is blocked, which the caller is expected to retry. See
    /// <see cref="InstanceStagingTask"/> for why that is a normal outcome rather than an error.
    /// </returns>
    public bool CommitStagedInstance(string stagingPath, IInstanceTask description)
        => CommitStagedInstance(stagingPath, description, out _);

    /// <inheritdoc cref="CommitStagedInstance(string, IInstanceTask)"/>
    /// <param name="committedId">
    /// The directory name the instance actually got, which is NOT the display name when something
    /// already occupied it -- DirNameFromString appends "(1)" and so on. A caller that wants to select
    /// what it just made has to be told, rather than guessing from the name.
    /// </param>
    public bool CommitStagedInstance(string stagingPath, IInstanceTask description, out string committedId)
    {
        ArgumentNullException.ThrowIfNull(description);

        committedId = string.Empty;

        /*
         * OVERRIDING REPLACES IN PLACE. An update to an existing instance keeps its id -- and so its
         * group, its icon and anything else keyed by id -- where a fresh install gets a new directory
         * named after it.
         */
        var id = description.ShouldOverride
            ? description.OriginalInstanceId
            : FileSystem.DirNameFromString(description.Name, InstancesDirectory);

        if (id.Length == 0)
        {
            return false;
        }

        var destination = FileSystem.PathCombine(InstancesDirectory, id);

        try
        {
            if (description.ShouldOverride)
            {
                if (!FileSystem.OverrideFolder(destination, stagingPath))
                {
                    return false;
                }

                // The staged copy has been merged in; what is left is a duplicate.
                FileSystem.DeletePath(stagingPath);
            }
            else
            {
                if (Directory.Exists(destination))
                {
                    return false;
                }

                Directory.Move(stagingPath, destination);

                /*
                 * REGISTERED BEFORE THE GROUP IS SAVED. SaveGroupList skips any id it does not know as
                 * an instance -- upstream's saveGroupList has the same guard, and upstream does
                 * `instanceSet.insert(instID)` right here for exactly this reason.
                 *
                 * Without it, a newly created instance's group was written to instgroups.json and
                 * immediately filtered back out, so every new instance landed ungrouped. Found by the
                 * first test that created one INTO a group and read it back.
                 */
                if (LoadInstance(id) is { } record)
                {
                    _instances[id] = record;
                }

                // Only a new instance joins a group; an override already has one.
                SetInstanceGroup(id, description.Group);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The usual cause is a scanner holding a file open. The caller retries.
            return false;
        }

        committedId = id;

        return true;
    }
}
