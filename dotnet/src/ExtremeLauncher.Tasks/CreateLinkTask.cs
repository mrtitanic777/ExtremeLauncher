// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2023 Rachel Powers <508861+Ryex@users.noreply.github.com>
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
 * Rewritten from `class FS::create_link` in launcher/FileSystem.{h,cpp}.
 *
 * Upstream splits the work in two: make_link_list() walks the sources and builds m_links_to_make,
 * then make_links() creates them. That split is preserved, because the dry-run mode depends on it and
 * because the elevated `filelink` helper consumes the same list over IPC.
 *
 * STILL DEFERRED (wave 9): runPrivileged() and ExternalLinkFileProcess, the QLocalServer handshake
 * that hands this list to the elevated helper when the user lacks symlink permission on Windows.
 * This class covers the unprivileged path only.
 */

using System.Runtime.InteropServices;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Tasks;

public readonly record struct LinkPair(string Source, string Destination);

public readonly record struct LinkResult(string Source, string Destination, string ErrorMessage, int ErrorValue);

public sealed partial class CreateLinkTask : LauncherTask
{
    private readonly List<LinkPair> _pathPairs = [];
    private readonly List<LinkPair> _linksToMake = [];
    private readonly List<LinkResult> _pathResults = [];

    private bool _useHardLinks;
    private IPathMatcher? _matcher;
    private bool _whitelist;
    private bool _recursive = true;
    private int _maxDepth = -1;

    public CreateLinkTask(string source, string destination, string name = "") : base(name)
        => _pathPairs.Add(new LinkPair(source, destination));

    public CreateLinkTask(IEnumerable<LinkPair> pathPairs, string name = "") : base(name)
        => _pathPairs.AddRange(pathPairs);

    public event EventHandler<LinkPair>? FileLinked;

    public event EventHandler<LinkResult>? LinkFailed;

    public override bool CanAbort => true;

    /// <summary>Build the link list and report, but create nothing.</summary>
    public bool DryRun { get; set; }

    public int TotalLinked { get; private set; }

    public int TotalToLink => _linksToMake.Count;

    public IReadOnlyList<LinkResult> Results => _pathResults;

    /// <remarks>Forces recursion on: a directory cannot be hard-linked.</remarks>
    public CreateLinkTask UseHardLinks(bool useHard)
    {
        _useHardLinks = useHard;
        return this;
    }

    public CreateLinkTask Matcher(IPathMatcher? matcher)
    {
        _matcher = matcher;
        return this;
    }

    public CreateLinkTask Whitelist(bool whitelist)
    {
        _whitelist = whitelist;
        return this;
    }

    public CreateLinkTask LinkRecursively(bool recursive)
    {
        _recursive = recursive;
        return this;
    }

    /// <summary>-1 is unlimited; 0 links <c>src/*</c> to <c>dst/*</c>; 1 links <c>src/*/*</c>, and so on.</summary>
    public CreateLinkTask SetMaxDepth(int depth)
    {
        _maxDepth = depth;
        return this;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        TotalLinked = 0;
        _pathResults.Clear();
        _linksToMake.Clear();

        MakeLinkList(cancellationToken);

        if (!DryRun)
        {
            MakeLinks(cancellationToken);
        }

        return Task.CompletedTask;
    }

    private void MakeLinkList(CancellationToken cancellationToken)
    {
        // A directory cannot be hard-linked, so hard links always imply a recursive file-by-file walk.
        if (_useHardLinks)
        {
            _recursive = true;
        }

        foreach (var pair in _pathPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = FileSystem.CleanPath(Path.GetFullPath(pair.Source));
            var destination = FileSystem.CleanPath(Path.GetFullPath(pair.Destination));

            if (!_recursive || !Directory.Exists(source))
            {
                AddLink(source, destination, string.Empty);
                continue;
            }

            var linkedPaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePath = FileSystem.CleanPath(file);
                var relativePath = FileSystem.CleanPath(Path.GetRelativePath(source, file));

                if (_maxDepth >= 0 && FileSystem.PathDepth(relativePath) > _maxDepth)
                {
                    // Too deep: link the containing directory at the depth limit instead, once.
                    relativePath = FileSystem.CleanPath(FileSystem.PathTruncate(relativePath, _maxDepth));
                    sourcePath = FileSystem.PathCombine(source, relativePath);

                    if (!linkedPaths.Add(sourcePath))
                    {
                        continue;
                    }
                }
                else
                {
                    linkedPaths.Add(sourcePath);
                }

                AddLink(sourcePath, destination, relativePath);
            }
        }
    }

    private void AddLink(string sourcePath, string destinationRoot, string relativePath)
    {
        // Same polarity as upstream: one matcher serves as blacklist or whitelist via the flag.
        if (_matcher is not null && _matcher.Matches(relativePath) != _whitelist)
        {
            return;
        }

        _linksToMake.Add(new LinkPair(sourcePath, FileSystem.PathCombine(destinationRoot, relativePath)));
    }

    private void MakeLinks(CancellationToken cancellationToken)
    {
        foreach (var link in _linksToMake)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileSystem.EnsureFilePathExists(link.Destination);

            try
            {
                if (_useHardLinks)
                {
                    CreateHardLink(link.Source, link.Destination);
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var result = new LinkResult(link.Source, link.Destination, e.Message, Marshal.GetLastWin32Error());
                _pathResults.Add(result);
                LinkFailed?.Invoke(this, result);

                // Upstream bails on the first failure rather than continuing.
                throw new TaskFailedException($"Failed to link {link.Source} to {link.Destination}: {e.Message}", e);
            }

            TotalLinked++;
            FileLinked?.Invoke(this, link);
        }
    }

    /// <remarks>
    /// Delegates to <see cref="NativeLink"/>, which owns the one declaration of the platform call --
    /// Untar needs the same primitive, and two copies of a P/Invoke is one too many.
    /// </remarks>
    private static void CreateHardLink(string source, string destination)
        => NativeLink.CreateHardLink(source, destination);
}
