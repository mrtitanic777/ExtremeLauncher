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
 * Ported from launcher/minecraft/launch/AutoInstallJava.{h,cpp} and
 * launcher/minecraft/launch/ReconstructAssets.{h,cpp}.
 *
 * THIS STEP NEVER FAILS A LAUNCH. Every path that cannot produce a Java runtime logs a warning and
 * succeeds, leaving whatever Java the user already has configured. That is deliberate and worth
 * keeping: a meta server outage, an unpublished platform, or out-of-date metadata should not stop
 * someone playing with the JVM they already have. The only genuine failure is a download that started
 * and broke, and even that falls through to the next candidate major version.
 *
 * The retry loop upstream is a chain of signal callbacks that re-enter tryNextMajorJava on every
 * failure, with an index member counting position. Here it is a foreach.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
// Aliased: ExtremeLauncher.Meta.Index collides with System.Index, which is in scope implicitly.
using MetaIndex = ExtremeLauncher.Meta.Index;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>The result of trying to provide a compatible Java runtime.</summary>
/// <param name="JavaPath">The interpreter to launch with, or empty to keep the configured one.</param>
/// <remarks>
/// A struct with a string in it has a <see langword="default"/> whose string is NULL, whatever the
/// constructor does — <c>default(JavaResolution).JavaPath</c> is null, not empty. Both members below
/// are written to tolerate that, because the step exposes this property before it has run.
/// </remarks>
public readonly record struct JavaResolution(string JavaPath)
{
    /// <summary>The interpreter path, or an empty string when none was resolved.</summary>
    public string Path => JavaPath ?? string.Empty;

    public bool Found => Path.Length != 0;
}

public sealed class AutoInstallJava : LaunchStep
{
    /// <summary>The meta package that carries downloadable runtimes.</summary>
    private const string JavaUid = "net.minecraft.java";

    private readonly LaunchProfile _profile;
    private readonly MetaIndex _index;
    private readonly string _javaDirectory;
    private readonly Func<string, string, CancellationToken, Task<bool>>? _loadVersion;
    private readonly Func<JavaMetadata, string, LauncherTask>? _createDownload;
    private readonly string _supportedArchitecture;
    private readonly IReadOnlyList<JavaInstall> _installedJavas;
    private readonly bool _automaticDownload;

    public AutoInstallJava(
        LaunchProfile profile,
        MetaIndex index,
        string javaDirectory,
        Func<string, string, CancellationToken, Task<bool>>? loadVersion = null,
        Func<JavaMetadata, string, LauncherTask>? createDownload = null,
        IReadOnlyList<JavaInstall>? installedJavas = null,
        bool automaticDownload = true,
        string? supportedArchitecture = null)
        : base("Install Java")
    {
        _profile = profile;
        _index = index;
        _javaDirectory = javaDirectory;
        _loadVersion = loadVersion;
        _createDownload = createDownload;
        _installedJavas = installedJavas ?? [];
        _automaticDownload = automaticDownload;
        _supportedArchitecture = supportedArchitecture ?? SysInfo.SupportedJavaArchitecture();
    }

    /// <summary>The interpreter to launch with. Empty when the configured one should be kept.</summary>
    public JavaResolution Resolution { get; private set; } = new(string.Empty);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Without automatic downloading, the most that can be done is pick from what is installed.
        if (!_automaticDownload)
        {
            PickInstalled();
            return;
        }

        if (_supportedArchitecture.Length == 0)
        {
            // FreeBSD and OpenBSD have no published runtimes. Not an error: the user's own Java works.
            LogLine(
                $"Your system ({SysInfo.CurrentSystem()}-{SysInfo.CurrentArchitecture()}) is not compatible with "
                + "automatic Java installation. Using the default Java path.",
                MessageLevel.Warning);

            return;
        }

        var wantedName = _profile.CompatibleJavaName;

        if (wantedName.Length == 0)
        {
            LogLine(
                "Your meta information is out of date or doesn't have the information necessary to determine what "
                + "installation of Java should be used. Using the default Java path.",
                MessageLevel.Warning);

            return;
        }

        // Already installed by a previous launch.
        if (Directory.Exists(FileSystem.PathCombine(_javaDirectory, wantedName)))
        {
            SetJavaPathFromPartial(wantedName);
            return;
        }

        await DownloadAsync(wantedName, cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// The profile lists compatible majors in preference order, so the FIRST match wins rather than
    /// the newest. A 32-bit install is accepted but called out, because it caps the heap at 2 GiB and
    /// the resulting out-of-memory crash is otherwise mystifying.
    /// </remarks>
    private void PickInstalled()
    {
        foreach (var major in _profile.CompatibleJavaMajors)
        {
            var match = _installedJavas.FirstOrDefault(java => java.Version.Major == major);

            if (match is null)
            {
                continue;
            }

            if (match.Arch != "64")
            {
                LogLine("The automatic Java mechanism detected a 32-bit installation of Java.", MessageLevel.Launcher);
            }

            SetJavaPath(match.Path);
            return;
        }

        LogLine("No compatible Java version was found. Using the default one.", MessageLevel.Warning);
    }

    /// <remarks>
    /// Each compatible major is tried in turn, and a FAILURE MOVES ON rather than stopping: the meta
    /// server may not publish a build of that major for this platform, or the download may break.
    /// Running out of candidates is still a success, with the configured Java left alone.
    /// </remarks>
    private async Task DownloadAsync(string wantedName, CancellationToken cancellationToken)
    {
        foreach (var major in _profile.CompatibleJavaMajors)
        {
            var version = $"java{major.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var metaVersion = _index.GetOrCreate(JavaUid, version);

            if (!metaVersion.IsLoaded && _loadVersion is not null)
            {
                if (!await _loadVersion(JavaUid, version, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                metaVersion = _index.GetOrCreate(JavaUid, version);
            }

            if (metaVersion.Data is not { } data)
            {
                continue;
            }

            // The runtimes list spans every platform; only one entry matches this machine AND the name
            // the profile asked for.
            var runtime = data.Runtimes.FirstOrDefault(
                java => string.Equals(java.RuntimeOS, _supportedArchitecture, StringComparison.Ordinal)
                        && string.Equals(java.Name, wantedName, StringComparison.Ordinal));

            if (runtime is null)
            {
                continue;
            }

            if (await InstallAsync(runtime, cancellationToken).ConfigureAwait(false))
            {
                SetJavaPathFromPartial(runtime.Name);
                return;
            }
        }

        LogLine(
            $"No versions of Java were found for your operating system: {SysInfo.CurrentSystem()}-{SysInfo.CurrentArchitecture()}",
            MessageLevel.Warning);

        LogLine("No compatible version of Java was found. Using the default one.", MessageLevel.Warning);
    }

    private async Task<bool> InstallAsync(JavaMetadata runtime, CancellationToken cancellationToken)
    {
        var finalPath = FileSystem.PathCombine(_javaDirectory, runtime.Name);

        if (_createDownload is null)
        {
            return false;
        }

        LauncherTask install;

        try
        {
            install = _createDownload(runtime, finalPath);
        }
        catch (LauncherException)
        {
            // An unknown download type. Upstream fails the whole step here; moving on is kinder and
            // reaches the same place, since the loop ends in a warning either way.
            return false;
        }

        install.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);
        install.StatusChanged += (_, status) => SetStatus(status);

        if (await install.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // A HALF-UNPACKED RUNTIME IS DELETED. Left behind, the "already installed" check above would
        // find the directory on the next launch and hand the game a path with no interpreter in it.
        FileSystem.DeletePath(finalPath);
        return false;
    }

    /// <summary>Points at the interpreter inside a runtime directory, if it is really there.</summary>
    /// <remarks>
    /// Checking for the binary is enough — a directory that exists but has no <c>bin/java</c> in it is
    /// a failed install, and treating it as usable produces a launch failure with no explanation.
    /// </remarks>
    private void SetJavaPathFromPartial(string javaName)
    {
        var finalPath = FileSystem.PathCombine(_javaDirectory, javaName, "bin", JavaUtils.JavaExecutable);

        if (File.Exists(finalPath))
        {
            SetJavaPath(finalPath);
            return;
        }

        LogLine(
            "No compatible Java version was found (the binary file does not exist). Using the default one.",
            MessageLevel.Warning);
    }

    private void SetJavaPath(string path)
    {
        Resolution = new JavaResolution(path);
        LogLine($"Compatible Java found at: {path}.", MessageLevel.Launcher);
    }
}

/// <summary>
/// Lays out the pre-1.7.3 asset tree, for versions that read assets as ordinary files.
/// </summary>
/// <remarks>
/// Old versions expect <c>resources/</c> full of real files with real names; everything since reads
/// the content-addressed store directly. The index says which it is, so this is a no-op for any modern
/// version.
///
/// NEVER FAILS, inherited: upstream logs a warning and carries on, because a missing sound file is a
/// worse reason to refuse to launch than it is a problem.
/// </remarks>
public sealed class ReconstructAssets : LaunchStep
{
    private readonly LaunchProfile _profile;
    private readonly string _assetsDirectory;
    private readonly string _resourcesDirectory;

    public ReconstructAssets(LaunchProfile profile, string assetsDirectory, string resourcesDirectory)
        : base("Reconstruct assets")
    {
        _profile = profile;
        _assetsDirectory = assetsDirectory;
        _resourcesDirectory = resourcesDirectory;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_profile.MinecraftAssets is not { Id.Length: > 0 } assets)
        {
            return Task.CompletedTask;
        }

        var index = AssetsIndex.Load(_assetsDirectory, assets.Id);

        if (index is null)
        {
            LogLine($"Failed to read the assets index for {assets.Id}", MessageLevel.Error);
            return Task.CompletedTask;
        }

        // Modern indexes say neither, and there is nothing to lay out.
        if (!index.MapToResources && !index.IsVirtual)
        {
            return Task.CompletedTask;
        }

        if (index.ReconstructVirtualTree(_assetsDirectory, _resourcesDirectory) is null)
        {
            LogLine($"Failed to reconstruct the virtual assets folder for {assets.Id}", MessageLevel.Error);
        }

        return Task.CompletedTask;
    }
}
