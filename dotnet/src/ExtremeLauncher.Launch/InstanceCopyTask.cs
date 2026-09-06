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
 * Ported from launcher/InstanceCopyTask.{h,cpp}.
 *
 * THE THING THE THREE ENGINES WERE FOR. Duplicating an instance is a choice between copying it,
 * linking it and cloning it, and this is what picks -- then does the several things that are only
 * true of the option chosen.
 *
 * LINKING IS NOT JUST A CHEAPER COPY. Two of the steps below exist only because a linked instance
 * shares files with its original:
 *
 *   - SAVES ARE COPIED EVEN WHEN EVERYTHING ELSE IS LINKED. Sharing a world between two instances is
 *     not a saving, it is two copies of Minecraft writing to one region file.
 *   - THE ORIGINAL'S GAME DIRECTORY IS RECORDED IN allowed_symlinks.txt, because the launcher refuses
 *     to follow a symlink out of an instance unless it is listed. Without this the linked instance
 *     starts and then cannot read its own mods.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>How to duplicate an instance.</summary>
public enum InstanceCopyStrategy
{
    Copy,

    /// <summary>Copy-on-write. Independent files, shared blocks.</summary>
    Clone,

    /// <summary>Shared files. Cheapest, and edits are visible to both.</summary>
    Link,
}

public sealed class InstanceCopyTask : LauncherTask, IInstanceTask
{
    private readonly string _sourceRoot;
    private string _stagingPath;
    private readonly InstanceCopyPrefs _prefs;
    private readonly string _name;
    private readonly string _iconKey;

    public InstanceCopyTask(
        string sourceRoot,
        string stagingPath,
        InstanceCopyPrefs prefs,
        string name,
        string iconKey = "default")
        : base($"Copying instance {name}")
    {
        _sourceRoot = sourceRoot;
        _stagingPath = stagingPath;
        _prefs = prefs;
        _name = name;
        _iconKey = iconKey;
    }

    public override bool CanAbort => true;

    /*
     * IInstanceTask, so InstanceStagingTask can drive this the way upstream does -- there, InstanceCopyTask
     * simply IS an InstanceTask and inherits the staging path. This port split the two, so the path is a
     * settable property: the staging wrapper picks the directory and assigns it before the copy runs, and
     * the constructor argument stays as the initial value for callers that stage it themselves.
     */

    /// <summary>Where to build. Assigned by <see cref="InstanceStagingTask"/> before this runs.</summary>
    public string StagingPath
    {
        get => _stagingPath;
        set => _stagingPath = value;
    }

    /*
     * EXPLICIT, because LauncherTask.Name already means something else: the task's display name, which
     * here is "Copying instance X". IInstanceTask.Name is the INSTANCE's name, which becomes its
     * directory. Hiding one behind the other would give the staging wrapper a folder called
     * "Copying instance X".
     */
    string IInstanceTask.Name => _name;

    /// <summary>The group to file the copy under, or empty.</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>Always false: a copy creates an instance rather than replacing one.</summary>
    public bool ShouldOverride => false;

    /// <summary>Never set, since a copy overrides nothing.</summary>
    public string OriginalInstanceId => string.Empty;

    /// <summary>Which of the three was used. Known after the task runs.</summary>
    public InstanceCopyStrategy Strategy => ChooseStrategy(_prefs);

    /// <summary>
    /// Picks the duplication strategy from the preferences.
    /// </summary>
    /// <remarks>
    /// CLONE WINS OVER LINK, matching upstream's ordering: it is strictly better where available --
    /// the same near-zero cost, without the copy and the original sharing edits. The caller is
    /// expected to have offered clone only where <c>FileSystem.CanClone</c> said yes.
    /// </remarks>
    public static InstanceCopyStrategy ChooseStrategy(InstanceCopyPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);

        if (prefs.UseClone)
        {
            return InstanceCopyStrategy.Clone;
        }

        return prefs.UseSymLinks || prefs.UseHardLinks
            ? InstanceCopyStrategy.Link
            : InstanceCopyStrategy.Copy;
    }

    /// <summary>
    /// The game directory inside a staging folder.
    /// </summary>
    /// <remarks>
    /// An instance holds its game files in either ".minecraft" or "minecraft" depending on how old it
    /// is. Upstream prefers the DOTTED name only when the plain one is absent, so an instance
    /// containing both -- which happens after a botched migration -- keeps using the visible one.
    /// </remarks>
    public static string StagingGameRoot(string stagingPath)
    {
        var visible = FileSystem.PathCombine(stagingPath, "minecraft");
        var dotted = FileSystem.PathCombine(stagingPath, ".minecraft");

        return Directory.Exists(dotted) && !Directory.Exists(visible) ? dotted : visible;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus($"Copying instance {_name}");

        var filters = _prefs.GetSelectedFiltersAsRegex();

        // Case-insensitive, as upstream intends: a "Saves" folder is still the saves folder. See the
        // note on RegexpMatcher.CaseSensitive -- upstream's own call gets the opposite of this.
        IPathMatcher? matcher = filters.Length == 0
            ? null
            : new RegexpMatcher(filters).CaseSensitive(false);

        var strategy = ChooseStrategy(_prefs);

        var succeeded = await Task.Run(
            () => strategy switch
            {
                InstanceCopyStrategy.Clone => RunClone(matcher),
                InstanceCopyStrategy.Link => RunLink(matcher),
                _ => RunCopy(matcher),
            },
            cancellationToken).ConfigureAwait(false);

        if (!succeeded)
        {
            throw new TaskFailedException("Instance folder copy failed.");
        }

        FinishInstance(strategy);
    }

    private bool RunClone(IPathMatcher? matcher)
    {
        var clone = new FileClone(_sourceRoot, _stagingPath).Matcher(matcher).Whitelist(false);

        clone.Run(dryRun: true);

        var total = clone.TotalCloned;
        SetProgress(0, total);

        clone.FileCloned += (_, _) => SetProgress(clone.TotalCloned, total);

        return clone.Run();
    }

    private bool RunCopy(IPathMatcher? matcher)
    {
        /*
         * followSymlinks(false): a plain copy preserves links rather than expanding them. An instance
         * whose mods folder is already a link to a shared one should stay that way, not become a
         * private duplicate of it.
         */
        var copy = new FileCopy(_sourceRoot, _stagingPath)
            .Matcher(matcher)
            .Whitelist(false)
            .FollowSymlinks(false);

        copy.Run(dryRun: true);

        var total = copy.TotalCopied;
        SetProgress(0, total);

        copy.FileCopied += (_, _) => SetProgress(copy.TotalCopied, total);

        return copy.Run();
    }

    private bool RunLink(IPathMatcher? matcher)
    {
        /*
         * SAVES ARE COPIED, NOT LINKED. Sharing a world between two instances is not a saving -- it is
         * two copies of Minecraft writing to one region file.
         *
         * DEFECT FOUND HERE, and the cause is upstream's ordering. Upstream links everything (saves
         * included -- they are only excluded from the filter when the user unticks them) and THEN
         * copies the saves over the top, with a FileCopy whose overwrite defaults to false. The copy
         * therefore lands on files that already exist and fails, and with them the whole instance
         * copy. Linking a folder at depth 0 is worse still: the copy would then be writing through the
         * link into the original instance.
         *
         * Saves are excluded from the LINK instead, so the copy has somewhere to land. That is the
         * only ordering in which the stated intent works. (Inferred from reading upstream, not from
         * running it -- but this port reproduced the failure exactly, and the fix is what makes the
         * behaviour upstream describes achievable at all.)
         */
        FileCopy? savesCopy = null;
        var linkMatcher = matcher;

        if (_prefs.CopySaves)
        {
            savesCopy = new FileCopy(
                    FileSystem.PathCombine(_sourceRoot, "minecraft", "saves"),
                    FileSystem.PathCombine(StagingGameRoot(_stagingPath), "saves"))
                .FollowSymlinks(true);

            var combined = new MultiMatcher();

            if (matcher is not null)
            {
                combined.Add(matcher);
            }

            // Both spellings of the game directory, since either may be the one on disk.
            combined.Add(new RegexpMatcher("^[.]?minecraft/saves").CaseSensitive(false));

            linkMatcher = combined;
        }

        /*
         * Depth 0 when not recursive: link the top-level entries, not the instance folder itself --
         * upstream's comment, and the reason is that a link to the whole instance would give the copy
         * no instance.cfg of its own to rename.
         */
        var link = new FileLink(_sourceRoot, _stagingPath)
            .LinkRecursively(true)
            .SetMaxDepth(_prefs.LinkRecursively ? -1 : 0)
            .UseHardLinks(_prefs.UseHardLinks)
            .Matcher(linkMatcher)
            .Whitelist(false);

        link.Run(dryRun: true);

        var total = link.TotalToLink;
        SetProgress(0, total);

        link.FileLinked += (_, _) => SetProgress(link.TotalLinked, total);

        if (!link.Run())
        {
            /*
             * Upstream re-runs itself elevated here, because the usual cause on Windows is the missing
             * symlink privilege. That path is not ported; the reason is surfaced instead, and hard
             * links need no privilege.
             */
            SetStatus($"Linking failed: {link.FailReason}");

            return false;
        }

        if (savesCopy is not null && !savesCopy.Run())
        {
            SetStatus($"Copying saves failed: {string.Join(", ", savesCopy.Failed.Take(3))}");

            return false;
        }

        return true;
    }

    /// <summary>Renames the copy and records what linking made true of it.</summary>
    private void FinishInstance(InstanceCopyStrategy strategy)
    {
        var settings = new IniSettingsObject(FileSystem.PathCombine(_stagingPath, "instance.cfg"));

        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("totalTimePlayed", 0);
        settings.RegisterSetting("lastTimePlayed", 0);

        settings.Set("name", _name);
        settings.Set("iconKey", _iconKey);

        // A duplicated instance has not been played; keeping the original's hours is a choice.
        if (!_prefs.KeepPlaytime)
        {
            settings.Set("totalTimePlayed", 0);
            settings.Set("lastTimePlayed", 0);
        }

        if (strategy == InstanceCopyStrategy.Link)
        {
            RecordAllowedSymlink();
        }
    }

    /// <summary>
    /// Lets the linked instance follow links back into the original.
    /// </summary>
    /// <remarks>
    /// The launcher refuses to follow a symlink out of an instance unless the target is listed here,
    /// so without this a linked instance starts and then cannot read its own mods.
    /// </remarks>
    /// <remarks>
    /// THE FILE IS DELETED FIRST IF IT IS ITSELF A LINK, and upstream's comment says why: "we don't
    /// want to modify the original". After linking, the copy's allowed_symlinks.txt may BE the
    /// original's file, so appending to it would grant the original permissions it never asked for --
    /// and the entry being appended is a path outside itself. Breaking the link first keeps the change
    /// where it belongs.
    /// </remarks>
    private void RecordAllowedSymlink()
    {
        var gameRoot = StagingGameRoot(_stagingPath);
        var path = FileSystem.PathCombine(gameRoot, "allowed_symlinks.txt");

        var existing = string.Empty;

        if (File.Exists(path))
        {
            existing = System.Text.Encoding.UTF8.GetString(FileSystem.Read(path));

            // Appending to a file with no trailing newline would join two paths into one entry.
            if (existing.Length != 0 && !existing.EndsWith('\n'))
            {
                existing += "\n";
            }

            if (new FileInfo(path).LinkTarget is not null)
            {
                FileSystem.DeletePath(path);
            }
        }

        var original = FileSystem.PathCombine(_sourceRoot, "minecraft");

        FileSystem.EnsureFilePathExists(path);
        FileSystem.Write(path, System.Text.Encoding.UTF8.GetBytes(existing + original + "\n"));
    }
}
