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
 * Ported from the FS::copy class in launcher/FileSystem.{h,cpp}.
 *
 * COPYING WITH AN OPINION ABOUT WHAT TO SKIP. Used for duplicating an instance and for migrating a
 * MultiMC or PolyMC data directory, both of which need a subset rather than the whole tree.
 *
 * IT WALKS THE SOURCE ITSELF rather than asking the filesystem for a recursive copy, and upstream's
 * comment says why: a recursive copy has nowhere to consult a matcher. Every file is offered to the
 * filter and copied only if it survives.
 *
 * THE DRY RUN IS NOT A DIAGNOSTIC -- it is how the progress bar gets a denominator. The caller runs
 * the whole traversal once to count what will be copied, then again to do it. Both passes apply the
 * same filter, so the count is exact rather than an estimate.
 */

namespace ExtremeLauncher.Core;

/// <summary>Copies a tree, skipping whatever a matcher rejects.</summary>
public sealed class FileCopy
{
    private readonly string _source;
    private readonly string _destination;

    private IPathMatcher? _matcher;
    private bool _whitelist;
    private bool _overwrite;
    private bool _followSymlinks = true;

    public FileCopy(string source, string destination)
    {
        _source = source;
        _destination = destination;
    }

    /// <summary>The filter. Without one, everything is copied.</summary>
    public FileCopy Matcher(IPathMatcher? matcher)
    {
        _matcher = matcher;

        return this;
    }

    /// <summary>
    /// Whether the matcher names what to KEEP rather than what to skip.
    /// </summary>
    /// <remarks>
    /// Upstream expresses the whole decision as <c>matches(path) != whitelist</c>, so the same matcher
    /// serves both readings and this flag inverts it. Instance copying passes a blacklist of the boxes
    /// the user unticked; data migration passes a whitelist of what is worth bringing over.
    /// </remarks>
    public FileCopy Whitelist(bool whitelist)
    {
        _whitelist = whitelist;

        return this;
    }

    public FileCopy Overwrite(bool overwrite)
    {
        _overwrite = overwrite;

        return this;
    }

    /// <summary>
    /// Whether to copy what a symlink points at rather than the link.
    /// </summary>
    /// <remarks>
    /// FORCED ON FOR WINDOWS by upstream, whose comment reads "always deep copy on windows, the
    /// alternatives are too messy" -- creating a symlink there needs a privilege the launcher may not
    /// have, and a broken link is worse than a duplicated file.
    /// </remarks>
    public FileCopy FollowSymlinks(bool follow)
    {
        _followSymlinks = follow;

        return this;
    }

    /// <summary>How many files were copied, or would be.</summary>
    public int TotalCopied { get; private set; }

    /// <summary>The destination paths that could not be written.</summary>
    public List<string> Failed { get; } = [];

    /// <summary>Raised per file, for progress reporting.</summary>
    public event EventHandler<string>? FileCopied;

    public event EventHandler<string>? CopyFailed;

    /// <summary>
    /// Runs the copy.
    /// </summary>
    /// <param name="dryRun">Count what would be copied without writing anything.</param>
    /// <param name="offset">A subdirectory of both source and destination to work within.</param>
    /// <returns>
    /// Whether EVERY file was copied.
    /// </returns>
    /// <remarks>
    /// UPSTREAM BUG, fixed here. Its return is <c>err.value() == 0</c> on an error code shared by every
    /// iteration and overwritten by each -- so it reports only whether the LAST file succeeded. A run
    /// where file one fails and file two succeeds returns true, and the caller
    /// (<c>DataMigrationTask</c>) takes that as "everything copied" and reports success. The failures
    /// are recorded in the list either way, which is what makes the wrong answer so quiet: the
    /// evidence is right there and nothing looks at it.
    /// </remarks>
    public bool Run(bool dryRun = false, string offset = "")
    {
        TotalCopied = 0;
        Failed.Clear();

        var source = FileSystem.PathCombine(_source, offset);
        var destination = FileSystem.PathCombine(_destination, offset);

        // A single file, not a tree: upstream's fallback for when the iterator finds nothing to walk.
        if (File.Exists(source))
        {
            CopyOne(source, string.Empty, destination, dryRun);

            return Failed.Count == 0;
        }

        if (!Directory.Exists(source))
        {
            return true;
        }

        var root = Path.GetFullPath(source);

        /*
         * FILES ONLY, hidden ones included. Directories are never copied as entries -- they come into
         * existence because a file inside them needed a parent. So an EMPTY directory is not copied at
         * all, which is upstream's behaviour and occasionally surprising: an instance with an empty
         * "shaderpacks" folder arrives without one.
         */
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            CopyOne(file, Path.GetRelativePath(root, file), destination, dryRun);
        }

        return Failed.Count == 0;
    }

    private void CopyOne(string sourcePath, string relativePath, string destinationRoot, bool dryRun)
    {
        // The matcher is consulted with the RELATIVE path, so patterns are written against the tree
        // rather than against wherever it happens to live.
        if (_matcher is not null && _matcher.Matches(relativePath.Replace('\\', '/')) != _whitelist)
        {
            return;
        }

        var destinationPath = relativePath.Length == 0
            ? destinationRoot
            : FileSystem.PathCombine(destinationRoot, relativePath);

        if (!dryRun)
        {
            try
            {
                FileSystem.EnsureFilePathExists(destinationPath);

                CopyFile(sourcePath, destinationPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Failed.Add(destinationPath);
                CopyFailed?.Invoke(this, relativePath);

                return;
            }
        }

        TotalCopied++;
        FileCopied?.Invoke(this, relativePath);
    }

    private void CopyFile(string sourcePath, string destinationPath)
    {
        /*
         * A symlink is recreated rather than followed only when asked. .NET reports the target through
         * ResolveLinkTarget; recreating the link keeps a shared mods folder shared instead of silently
         * doubling it.
         */
        if (!_followSymlinks && !OperatingSystem.IsWindows())
        {
            var info = new FileInfo(sourcePath);

            if (info.LinkTarget is { } target)
            {
                if (_overwrite && File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                File.CreateSymbolicLink(destinationPath, target);

                return;
            }
        }

        File.Copy(sourcePath, destinationPath, _overwrite);
    }
}
