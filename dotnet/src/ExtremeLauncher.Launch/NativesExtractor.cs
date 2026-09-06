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
 * Ported from launcher/minecraft/launch/ExtractNatives.{h,cpp}.
 *
 * LWJGL and friends ship their platform binaries inside jars. The JVM cannot load a .dll or .so from
 * within an archive, so before launch they are unpacked into a scratch directory that becomes
 * -Djava.library.path. The directory is disposable and is cleared out afterwards.
 *
 * THE .jnilib HACK is real and load-bearing. macOS JNI libraries were historically named .jnilib;
 * Java 8 onward only looks for .dylib. Old LWJGL jars still contain the old name, so entries are
 * renamed as they are extracted — otherwise the game starts and immediately fails to find its
 * graphics library.
 *
 * !! SECURITY: ZIP-SLIP GUARD ADDED — see PORTING.md !!
 * Upstream extracts each entry to `directory.absoluteFilePath(name)` with no check that the result
 * stays inside the target. A jar containing an entry named "../../something" writes outside the
 * natives folder. Native jars come from Mojang and mod repositories over the network, so this is
 * reachable. Entries that escape are refused here.
 *
 * NOTE ON extract/exclude: every native library entry carries an "extract": { "exclude": [...] } block,
 * and upstream parses it into Library::m_extractExcludes — where nothing ever reads it. Extraction
 * takes the whole archive. That behaviour is preserved by default; pass applyExcludes: true to honour
 * the field instead.
 */

using System.IO.Compression;
using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class NativesExtractor : LauncherTask
{
    private readonly IReadOnlyList<string> _nativeJars;
    private readonly IReadOnlyList<string> _excludes;
    private readonly string _targetDirectory;
    private readonly bool _applyJniLibHack;

    /// <param name="javaVersion">Decides whether the .jnilib rename applies.</param>
    /// <param name="excludes">Prefixes to skip. Empty preserves upstream's extract-everything.</param>
    public NativesExtractor(
        IReadOnlyList<string> nativeJars,
        string targetDirectory,
        JavaVersion javaVersion,
        IReadOnlyList<string>? excludes = null)
        : base("Extract natives")
    {
        _nativeJars = nativeJars;
        _targetDirectory = targetDirectory;
        _excludes = excludes ?? [];

        // Java 8 dropped .jnilib support.
        _applyJniLibHack = javaVersion.IsParseable && javaVersion.Major >= 8;
    }

    /// <summary>Builds an extractor for a resolved profile.</summary>
    public static NativesExtractor ForProfile(
        LaunchProfile profile,
        RuntimeContext runtimeContext,
        string targetDirectory,
        JavaVersion javaVersion,
        string localLibraryPath = "",
        bool applyExcludes = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        List<string> jars = [], natives = [];
        profile.GetLibraryFiles(runtimeContext, jars, natives, localLibraryPath);

        var excludes = new List<string>();

        if (applyExcludes)
        {
            foreach (var library in profile.NativeLibraries)
            {
                excludes.AddRange(library.ExtractExcludes);
            }
        }

        return new NativesExtractor(natives, targetDirectory, javaVersion, excludes);
    }

    public int ExtractedFiles { get; private set; }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_nativeJars.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!FileSystem.EnsureFolderPathExists(_targetDirectory))
        {
            throw new TaskFailedException($"Couldn't create the natives directory '{_targetDirectory}'");
        }

        var root = Path.GetFullPath(_targetDirectory);

        for (var i = 0; i < _nativeJars.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SetProgress(i, _nativeJars.Count);
            SetStatus($"Extracting {Path.GetFileName(_nativeJars[i])}");

            // Fail on the first bad jar rather than launching with a half-populated natives folder.
            Extract(_nativeJars[i], root);
        }

        SetProgress(_nativeJars.Count, _nativeJars.Count);

        return Task.CompletedTask;
    }

    private void Extract(string source, string root)
    {
        ZipArchive archive;

        try
        {
            archive = ZipFile.OpenRead(source);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new TaskFailedException($"Couldn't extract native jar '{source}' to destination '{root}': {e.Message}", e);
        }

        using (archive)
        {
            foreach (var entry in archive.Entries)
            {
                // Directory entries have an empty name and nothing to write.
                if (entry.Name.Length == 0)
                {
                    continue;
                }

                var name = entry.FullName;

                if (IsExcluded(name))
                {
                    continue;
                }

                if (_applyJniLibHack)
                {
                    name = ReplaceSuffix(name, ".jnilib", ".dylib");
                }

                var destination = Path.GetFullPath(Path.Combine(root, name));

                // Zip-slip guard: an entry must not resolve outside the natives directory.
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new TaskFailedException(
                        $"Native jar '{source}' contains an entry that escapes the target directory: '{entry.FullName}'");
                }

                try
                {
                    FileSystem.EnsureFilePathExists(destination);
                    entry.ExtractToFile(destination, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    throw new TaskFailedException(
                        $"Couldn't extract native jar '{source}' to destination '{root}': {e.Message}", e);
                }

                ExtractedFiles++;
            }
        }
    }

    private bool IsExcluded(string name)
        => _excludes.Any(exclude => name.StartsWith(exclude, StringComparison.Ordinal));

    /// <summary>Swaps a trailing suffix, leaving anything else untouched.</summary>
    internal static string ReplaceSuffix(string target, string suffix, string replacement)
        => target.EndsWith(suffix, StringComparison.Ordinal)
            ? target[..^suffix.Length] + replacement
            : target;

    /// <summary>Clears the scratch directory. Safe to call when it was never created.</summary>
    public static void Cleanup(string targetDirectory) => FileSystem.DeletePath(targetDirectory);
}
