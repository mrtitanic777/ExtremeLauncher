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
 * Rewritten from `class FS::copy` in launcher/FileSystem.{h,cpp}.
 *
 * Upstream this is a QObject functor: you configure it with chained setters and invoke operator(),
 * which walks the source tree synchronously and emits fileCopied/copyFailed as it goes. Here it is a
 * LauncherTask, so it composes into the task tree and gets cancellation for free. The chained setters
 * are kept so call sites read the same.
 *
 * It lives in ExtremeLauncher.Tasks rather than alongside FileSystem in ExtremeLauncher.Core because
 * it now derives from LauncherTask, and Tasks references Core rather than the other way round.
 *
 * !! DELIBERATE BEHAVIOUR CHANGE -- see PORTING.md !!
 * Upstream returns `err.value() == 0`, where `err` is a single std::error_code reused across every
 * file in the walk. Each fs::copy overwrites it, so the return value reflects only the *last* file
 * copied: an early failure followed by a later success reports overall success. That is not
 * theoretical -- FS::moveByCopy() calls copy() and then deletePath(source) if it returns true, so a
 * partial copy can delete the original. This port fails if ANY file failed.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Tasks;

public sealed class CopyTask : LauncherTask
{
    private readonly string _source;
    private readonly string _destination;
    private readonly List<string> _failedPaths = [];

    private bool _followSymlinks = true;
    private IPathMatcher? _matcher;
    private bool _whitelist;
    private bool _overwrite;

    public CopyTask(string source, string destination, string name = "") : base(name)
    {
        _source = source;
        _destination = destination;
    }

    public event EventHandler<string>? FileCopied;

    public event EventHandler<string>? CopyFailed;

    public override bool CanAbort => true;

    /// <summary>Walk the tree and report, but do not touch the destination.</summary>
    public bool DryRun { get; set; }

    public int TotalCopied { get; private set; }

    public int TotalFailed => _failedPaths.Count;

    public IReadOnlyList<string> FailedPaths => _failedPaths;

    /// <remarks>Forced on for Windows, matching upstream's "always deep copy, the alternatives are too messy".</remarks>
    public CopyTask FollowSymlinks(bool follow)
    {
        _followSymlinks = follow;
        return this;
    }

    public CopyTask Matcher(IPathMatcher? matcher)
    {
        _matcher = matcher;
        return this;
    }

    public CopyTask Whitelist(bool whitelist)
    {
        _whitelist = whitelist;
        return this;
    }

    public CopyTask Overwrite(bool overwrite)
    {
        _overwrite = overwrite;
        return this;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        TotalCopied = 0;
        _failedPaths.Clear();

        if (OperatingSystem.IsWindows())
        {
            _followSymlinks = true;
        }

        var source = FileSystem.CleanPath(Path.GetFullPath(_source));
        var destination = FileSystem.CleanPath(Path.GetFullPath(_destination));

        if (Directory.Exists(source))
        {
            // Cannot use a plain recursive copy: the matcher has to be consulted per file, so the
            // walk is explicit. Hidden files are included, matching QDir::Filter::Hidden upstream.
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = FileSystem.CleanPath(Path.GetRelativePath(source, file));
                CopyOne(file, destination, relative);
            }
        }
        else
        {
            // If the root source is not a directory, the walk above never runs.
            CopyOne(source, destination, string.Empty);
        }

        SetProgress(TotalCopied, TotalCopied + _failedPaths.Count);

        if (_failedPaths.Count > 0)
        {
            throw new TaskFailedException($"Failed to copy {_failedPaths.Count} file(s) to {destination}");
        }

        return Task.CompletedTask;
    }

    private void CopyOne(string sourcePath, string destinationRoot, string relativePath)
    {
        // Same polarity as upstream: one matcher serves as blacklist or whitelist via the flag.
        if (_matcher is not null && _matcher.Matches(relativePath) != _whitelist)
        {
            return;
        }

        var destinationPath = FileSystem.PathCombine(destinationRoot, relativePath);

        try
        {
            if (!DryRun)
            {
                FileSystem.EnsureFilePathExists(destinationPath);

                var linkTarget = _followSymlinks ? null : File.ResolveLinkTarget(sourcePath, returnFinalTarget: false);

                if (linkTarget is not null)
                {
                    File.CreateSymbolicLink(destinationPath, linkTarget.FullName);
                }
                else
                {
                    File.Copy(sourcePath, destinationPath, _overwrite);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _failedPaths.Add(destinationPath);
            CopyFailed?.Invoke(this, relativePath);
            return;
        }

        TotalCopied++;
        FileCopied?.Invoke(this, relativePath);
    }
}
