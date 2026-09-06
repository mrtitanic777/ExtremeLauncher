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
 * Ported from launcher/java/JavaChecker.{h,cpp}.
 *
 * ASKS A JVM WHAT IT IS, rather than guessing from its path. JavaUtils.FindJavaPaths deliberately does
 * not validate the candidates it turns up -- a directory called "jre-1.8.0_51" may hold a 64-bit Java
 * 17, or nothing at all -- so something has to actually run each one. This is that something.
 *
 * It works by launching the candidate with a tiny bundled jar that prints three system properties.
 * See Resources/README.md for why a jar rather than parsing -XshowSettings output.
 */

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ExtremeLauncher.Core;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Java;

/// <summary>How much was learned about a candidate JVM.</summary>
public enum JavaCheckValidity
{
    /// <summary>It could not be run at all.</summary>
    Errored,

    /// <summary>It ran, but did not say what it was.</summary>
    ReturnedInvalidData,

    Valid,
}

/// <summary>What a candidate JVM turned out to be.</summary>
public sealed class JavaCheckResult
{
    public string Path { get; init; } = string.Empty;

    /// <summary>Correlates a result with the request that produced it, when many run at once.</summary>
    public int Id { get; init; }

    public JavaCheckValidity Validity { get; set; } = JavaCheckValidity.Errored;

    public bool Is64Bit { get; set; }

    /// <summary>"64" or "32" -- the vocabulary a version JSON's rules use.</summary>
    public string MojangPlatform { get; set; } = string.Empty;

    /// <summary>What the JVM itself calls its architecture: "amd64", "aarch64", "x86".</summary>
    public string RealPlatform { get; set; } = string.Empty;

    public string JavaVersion { get; set; } = string.Empty;

    public string JavaVendor { get; set; } = string.Empty;

    public string OutLog { get; set; } = string.Empty;

    public string ErrorLog { get; set; } = string.Empty;
}

public sealed class JavaChecker : LauncherTask
{
    /// <summary>
    /// A JVM that has not answered in this long is not going to.
    /// </summary>
    /// <remarks>
    /// Upstream's comment on the kill is "NO MERCY. NO ABUSE." -- and it is right to be firm. A
    /// candidate found by scanning the filesystem may be a broken install, a stub that blocks on a
    /// network mount, or not a JVM at all, and the scan runs one of these per candidate at startup.
    /// </remarks>
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The binary to PROBE with, which is not always the one the game is launched with.
    /// </summary>
    /// <remarks>
    /// DIVERGES FROM UPSTREAM, deliberately. Every Windows candidate is a javaw.exe, because that is
    /// what the game should be started with -- javaw has no console, so a played game does not sit
    /// behind a black window. But javaw reports its own startup failures in a MODAL MESSAGE BOX rather
    /// than on stderr, and probing is precisely the business of running a JVM that might fail.
    ///
    /// Found by running the launcher on a machine carrying Java 5, 6, 7, 8, 21 and 25: probing for a
    /// pack that wanted 17 walked the whole list, and each ancient JRE that could not load the checker
    /// class put up "Could not find the main class. Program will exit." and sat there until the 15
    /// second kill timer fired. Six dialogs and a minute and a half to decide there was no Java 17.
    ///
    /// java.exe sits beside javaw.exe in every JRE and JDK layout and differs only in having a
    /// console, so the checker reads the same output with the failures arriving as text. The path
    /// REPORTED stays the one that was asked about -- this changes how it is inspected, not what gets
    /// launched.
    /// </remarks>
    internal static string ProbeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var console = string.Concat(path.AsSpan(0, path.Length - "javaw.exe".Length), "java.exe");

        // Only if it is really there: a layout without java.exe is stranger than one without javaw,
        // but guessing a path that does not exist would turn a slow probe into a failed one.
        return File.Exists(console) ? console : path;
    }

    /// <summary>
    /// Environment variables that would change what the JVM reports about itself.
    /// </summary>
    /// <remarks>
    /// Upstream calls these "dangerous java crap". _JAVA_OPTIONS and JAVA_TOOL_OPTIONS in particular
    /// are injected into every JVM on the machine, so a user with one set would have their real
    /// architecture or version reported through a filter they had forgotten they applied.
    /// </remarks>
    private static readonly string[] IgnoredEnvironment =
    [
        "JAVA_ARGS", "CLASSPATH", "CONFIGPATH", "JAVA_HOME",
        "JRE_HOME", "_JAVA_OPTIONS", "JAVA_OPTIONS", "JAVA_TOOL_OPTIONS",
    ];

    /// <summary>Variables that would make the child load libraries meant for the launcher itself.</summary>
    private static readonly string[] StrippedEnvironment =
    [
        "LD_LIBRARY_PATH", "LD_PRELOAD", "QT_PLUGIN_PATH", "QT_FONTPATH",
    ];

    private readonly string _path;
    private readonly string _args;
    private readonly int _minMemory;
    private readonly int _maxMemory;
    private readonly int _permGen;
    private readonly int _id;
    private readonly string? _checkerJarOverride;

    public JavaChecker(
        string path,
        string args = "",
        int minMemory = 0,
        int maxMemory = 0,
        int permGen = 64,
        int id = 0,
        string? checkerJarPath = null)
        : base($"Check Java at {path}")
    {
        _path = path;
        _args = args;
        _minMemory = minMemory;
        _maxMemory = maxMemory;
        _permGen = permGen;
        _id = id;
        _checkerJarOverride = checkerJarPath;
    }

    /// <summary>What the candidate turned out to be. Available once the task has run.</summary>
    public JavaCheckResult Result { get; private set; } = new();

    /// <summary>
    /// The arguments the checker would run with, for a given jar path.
    /// </summary>
    /// <remarks>
    /// The memory flags are passed deliberately: a JVM that cannot honour them fails HERE rather than
    /// at launch. A 32-bit Java asked for -Xmx4096m refuses to start, and finding that out during a
    /// check is far better than during a game start.
    /// </remarks>
    public IReadOnlyList<string> BuildArguments(string checkerJar)
    {
        var args = new List<string>();

        if (_args.Length != 0)
        {
            args.AddRange(Commandline.SplitArgs(_args));
        }

        if (_minMemory != 0)
        {
            args.Add($"-Xms{_minMemory.ToString(CultureInfo.InvariantCulture)}m");
        }

        if (_maxMemory != 0)
        {
            args.Add($"-Xmx{_maxMemory.ToString(CultureInfo.InvariantCulture)}m");
        }

        // PermGen was removed in Java 8, and 64 is the default, so it is only worth passing when the
        // user has chosen something else.
        if (_permGen != 64 && _permGen != 0)
        {
            args.Add($"-XX:PermSize={_permGen.ToString(CultureInfo.InvariantCulture)}m");
        }

        args.Add("-jar");
        args.Add(checkerJar);

        return args;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var checkerJar = _checkerJarOverride ?? ExtractCheckerJar();

        if (checkerJar.Length == 0)
        {
            // Nothing to run. Upstream returns here without emitting anything at all, leaving its
            // caller waiting forever; an errored result is the same outcome without the hang.
            Result = new JavaCheckResult
            {
                Path = _path,
                Id = _id,
                ErrorLog = "Java checker library could not be found. Please check your installation.",
            };

            return;
        }

        var info = new ProcessStartInfo(ProbeExecutable(_path))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildArguments(checkerJar))
        {
            info.ArgumentList.Add(argument);
        }

        ApplyCleanEnvironment(info);

        string stdout, stderr;
        int exitCode;

        try
        {
            using var process = Process.Start(info);

            if (process is null)
            {
                Result = new JavaCheckResult { Path = _path, Id = _id };
                return;
            }

            // Read both pipes concurrently with the wait: a JVM that fills one buffer while nothing
            // drains it would block forever, and this runs against candidates that may misbehave.
            var outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(KillTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);

                Result = new JavaCheckResult
                {
                    Path = _path,
                    Id = _id,
                    ErrorLog = "Java checker has been killed by timeout.",
                };

                return;
            }

            stdout = await outTask.ConfigureAwait(false);
            stderr = await errTask.ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Failed to start: not a JVM, not executable, or no longer there.
            Result = new JavaCheckResult { Path = _path, Id = _id, ErrorLog = e.Message };
            return;
        }

        Result = Parse(stdout, stderr, exitCode);
    }

    /// <summary>
    /// Turns the checker's output into a result.
    /// </summary>
    /// <remarks>Separated from the process handling so the parsing can be tested without a JVM.</remarks>
    public JavaCheckResult Parse(string stdout, string stderr, int exitCode)
    {
        var result = new JavaCheckResult
        {
            Path = _path,
            Id = _id,
            OutLog = stdout,
            ErrorLog = stderr,
        };

        // The checker exits 1 when a property came back null, which means the JVM ran but is not one
        // that can be reasoned about.
        if (exitCode != 0)
        {
            result.Validity = JavaCheckValidity.Errored;
            return result;
        }

        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in stdout.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();

            // WORKAROUND for GH-4125: on Bedrock Linux the JVM wrapper prints its own diagnostics into
            // stdout, and they contain '=' often enough to be mistaken for properties.
            if (line.Contains("/bedrock/strata", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('=', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                continue;
            }

            results[parts[0]] = parts[1];
        }

        if (!results.TryGetValue("os.arch", out var osArch)
            || !results.TryGetValue("java.version", out var javaVersion)
            || !results.TryGetValue("java.vendor", out var javaVendor))
        {
            result.Validity = JavaCheckValidity.ReturnedInvalidData;
            return result;
        }

        // The JVM's own name for its architecture, mapped to the two-value vocabulary the version
        // rules use. Anything not on this list is treated as 32-bit, which is the safe direction: it
        // caps the heap rather than letting a launch fail at startup.
        var is64 = osArch is "x86_64" or "amd64" or "aarch64" or "arm64";

        result.Validity = JavaCheckValidity.Valid;
        result.Is64Bit = is64;
        result.MojangPlatform = is64 ? "64" : "32";
        result.RealPlatform = osArch;
        result.JavaVersion = javaVersion;
        result.JavaVendor = javaVendor;

        return result;
    }

    /// <summary>
    /// Writes the bundled checker jar somewhere a JVM can be pointed at it.
    /// </summary>
    /// <returns>The path, or an empty string if the resource is missing.</returns>
    /// <remarks>
    /// Cached under a fixed name, so repeated checks -- and the startup scan runs one per candidate --
    /// do not each write their own copy.
    /// </remarks>
    public static string ExtractCheckerJar()
    {
        var target = FileSystem.PathCombine(Path.GetTempPath(), "ExtremeLauncher", "JavaCheck.jar");

        try
        {
            using var source = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("ExtremeLauncher.Java.JavaCheck.jar");

            if (source is null)
            {
                return string.Empty;
            }

            // Rewritten only when the size differs. The jar changes approximately never, and rewriting
            // it on every check would race with the checks running beside it.
            if (!File.Exists(target) || new FileInfo(target).Length != source.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(output);
            }

            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Builds the child environment, dropping anything that would distort the answer.</summary>
    private static void ApplyCleanEnvironment(ProcessStartInfo info)
    {
        foreach (var name in IgnoredEnvironment)
        {
            info.Environment.Remove(name);
        }

        // Upstream strips these only on Linux and the BSDs; the QT_* pair is meaningless elsewhere and
        // the LD_* pair does not exist there, so removing them unconditionally is the same thing.
        foreach (var name in StrippedEnvironment)
        {
            info.Environment.Remove(name);
        }
    }

    public override string ToString() => $"JavaChecker({_path})";
}
