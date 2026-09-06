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
 * Ported from the FS::create_link class in launcher/FileSystem.{h,cpp}.
 *
 * COPYING AN INSTANCE WITHOUT COPYING THE FILES. A modpack instance is mostly a mods folder of jars
 * that already exist elsewhere on the disk, so duplicating one to try a change can cost gigabytes for
 * nothing. Linking instead makes the copy nearly free -- at the price that editing a linked file edits
 * the original too, which is why it is a choice the user makes rather than the default.
 *
 * THE DEPTH LIMIT IS THE INTERESTING PART, and it is not an optimisation. At depth 0 the launcher links
 * the mods FOLDER rather than each jar inside it: one link instead of three hundred, and adding a mod
 * to the copy adds it to the original as well. At unlimited depth every file is linked individually,
 * so the two instances share file contents but have independent folders. Those are genuinely different
 * products and the user picks between them.
 *
 * TWO PHASES, and the list is built first so the caller knows the total before anything happens. That
 * is why this needs no separate dry run, unlike FileCopy.
 */

namespace ExtremeLauncher.Core;

/// <summary>One link to create.</summary>
public readonly record struct LinkPair(string Source, string Destination);

/// <summary>Links a tree instead of copying it.</summary>
public sealed class FileLink
{
    private readonly List<LinkPair> _pathPairs;
    private readonly List<LinkPair> _linksToMake = [];

    private IPathMatcher? _matcher;
    private bool _whitelist;
    private bool _useHardLinks;
    private bool _recursive;
    private int _maxDepth = -1;

    public FileLink(string source, string destination)
        => _pathPairs = [new LinkPair(source, destination)];

    public FileLink(IEnumerable<LinkPair> pathPairs) => _pathPairs = [.. pathPairs];

    /// <summary>
    /// Hard links rather than symbolic ones.
    /// </summary>
    /// <remarks>
    /// FORCES RECURSION ON, because a directory cannot be hard linked -- the only way to honour the
    /// request is to link every file individually.
    /// </remarks>
    /// <remarks>
    /// UPSTREAM BUG #15, fixed here: it forces recursion but leaves the DEPTH LIMIT applying, so a
    /// truncated path plans a hard link to a directory -- which cannot exist and fails every time.
    /// Reachable straight from the UI: InstanceCopyTask passes depth 0 whenever "link recursively" is
    /// unticked, so ticking "hard links" without it produces a copy that always fails. The limit is
    /// ignored here for hard links, since unlimited depth is the only way the request can succeed.
    /// </remarks>
    public FileLink UseHardLinks(bool useHardLinks)
    {
        _useHardLinks = useHardLinks;

        return this;
    }

    public FileLink Matcher(IPathMatcher? matcher)
    {
        _matcher = matcher;

        return this;
    }

    /// <summary>Whether the matcher names what to keep rather than what to skip.</summary>
    public FileLink Whitelist(bool whitelist)
    {
        _whitelist = whitelist;

        return this;
    }

    /// <summary>Descend into the source rather than linking it as one entry.</summary>
    public FileLink LinkRecursively(bool recursive)
    {
        _recursive = recursive;

        return this;
    }

    /// <summary>
    /// How deep to descend before linking a directory instead of its contents.
    /// </summary>
    /// <remarks>
    /// Negative means no limit. Zero links the top-level entries -- the mods folder rather than each
    /// jar in it.
    /// </remarks>
    public FileLink SetMaxDepth(int depth)
    {
        _maxDepth = depth;

        return this;
    }

    /// <summary>How many links were created.</summary>
    public int TotalLinked { get; private set; }

    /// <summary>How many links the plan contains. Available after a dry run.</summary>
    public int TotalToLink => _linksToMake.Count;

    public event EventHandler<LinkPair>? FileLinked;

    /// <summary>Raised with the pair and the reason.</summary>
    public event EventHandler<(LinkPair Pair, string Reason)>? LinkFailed;

    /// <summary>The reason the first failure gave, or an empty string.</summary>
    public string FailReason { get; private set; } = string.Empty;

    /// <summary>
    /// Plans the links, and creates them unless this is a dry run.
    /// </summary>
    /// <returns>
    /// Whether every link was created. A dry run always succeeds — it only builds the plan, which is
    /// how <see cref="TotalToLink"/> becomes known before the caller commits to anything.
    /// </returns>
    public bool Run(bool dryRun = false, string offset = "")
    {
        TotalLinked = 0;
        FailReason = string.Empty;
        _linksToMake.Clear();

        BuildLinkList(offset);

        return dryRun || MakeLinks();
    }

    private void BuildLinkList(string offset)
    {
        // Set before the loop, matching upstream: it changes what every pair does, not just one.
        var recursive = _recursive || _useHardLinks;

        // See the note on UseHardLinks: a truncated path names a directory, and those cannot be
        // hard linked at all.
        var maxDepth = _useHardLinks ? -1 : _maxDepth;

        foreach (var pair in _pathPairs)
        {
            var source = FileSystem.PathCombine(Path.GetFullPath(pair.Source), offset);
            var destination = FileSystem.PathCombine(Path.GetFullPath(pair.Destination), offset);

            if (!recursive || !Directory.Exists(source))
            {
                // One link for the whole thing, whether it is a file or a directory.
                Consider(source, string.Empty, destination);

                continue;
            }

            /*
             * Truncating a deep path to the depth limit yields the same directory over and over -- one
             * per file inside it -- so already-planned sources are skipped. Without this a mods folder
             * of three hundred jars would plan three hundred links to the same folder.
             */
            var alreadyPlanned = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var sourcePath = file;

                if (maxDepth >= 0 && FileSystem.PathDepth(relative) > maxDepth)
                {
                    relative = FileSystem.PathTruncate(relative, maxDepth);
                    sourcePath = FileSystem.PathCombine(source, relative);

                    if (!alreadyPlanned.Add(sourcePath))
                    {
                        continue;
                    }
                }
                else
                {
                    alreadyPlanned.Add(sourcePath);
                }

                Consider(sourcePath, relative, destination);
            }
        }
    }

    private void Consider(string sourcePath, string relativePath, string destinationRoot)
    {
        if (_matcher is not null && _matcher.Matches(relativePath.Replace('\\', '/')) != _whitelist)
        {
            return;
        }

        var destinationPath = relativePath.Length == 0
            ? destinationRoot
            : FileSystem.PathCombine(destinationRoot, relativePath);

        _linksToMake.Add(new LinkPair(sourcePath, destinationPath));
    }

    /// <remarks>
    /// FAILS FAST, unlike <see cref="FileCopy"/>, which records every failure and carries on. Upstream
    /// returns on the first error here, and it is the better choice for linking: the usual failure is
    /// a missing privilege, which will fail identically for every remaining link, so continuing would
    /// produce hundreds of identical errors and a half-linked instance either way.
    /// </remarks>
    private bool MakeLinks()
    {
        foreach (var link in _linksToMake)
        {
            try
            {
                FileSystem.EnsureFilePathExists(link.Destination);

                if (_useHardLinks)
                {
                    NativeLink.CreateHardLink(link.Source, link.Destination);
                }
                else if (Directory.Exists(link.Source))
                {
                    Directory.CreateSymbolicLink(link.Destination, link.Source);
                }
                else
                {
                    File.CreateSymbolicLink(link.Destination, link.Source);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                        or LauncherException or PlatformNotSupportedException)
            {
                /*
                 * On Windows a symlink needs SeCreateSymbolicLinkPrivilege or developer mode, so this
                 * is the expected failure there rather than an exotic one. Upstream responds by
                 * re-running itself elevated; that path is not ported, so the reason is surfaced
                 * instead of swallowed -- a caller can suggest hard links, which need no privilege.
                 */
                FailReason = e.Message;
                LinkFailed?.Invoke(this, (link, e.Message));

                return false;
            }

            TotalLinked++;
            FileLinked?.Invoke(this, link);
        }

        return true;
    }
}
