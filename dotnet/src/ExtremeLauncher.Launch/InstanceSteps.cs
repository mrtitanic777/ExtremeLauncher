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
 * Ported from launcher/minecraft/launch/{ModMinecraftJar,ClaimAccount,PrintInstanceInfo}.cpp.
 *
 * The launch steps that need something from the instance beyond its paths: the modded jar, the account
 * lock held for the duration of the game, and the diagnostic banner at the top of every log.
 */

using System.Diagnostics;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>
/// Builds the modded minecraft.jar for an instance that has jar mods.
/// </summary>
/// <remarks>
/// Jar modding is how mods worked before 1.6: classes are overwritten inside the game's own jar rather
/// than loaded alongside it. The result is scratch — rebuilt every launch and deleted afterwards —
/// because it is derived entirely from the mod list and the version's main jar.
/// </remarks>
public sealed class ModMinecraftJar : LaunchStep
{
    private readonly string _binRoot;
    private readonly string _sourceJarPath;
    private readonly IReadOnlyList<JarMod> _jarMods;

    public ModMinecraftJar(string binRoot, string sourceJarPath, IReadOnlyList<JarMod> jarMods)
        : base("Mod the Minecraft jar")
    {
        _binRoot = binRoot;
        _sourceJarPath = sourceJarPath;
        _jarMods = jarMods;
    }

    /// <summary>Where the assembled jar goes. The launch classpath points at this.</summary>
    public string FinalJarPath => FileSystem.PathCombine(_binRoot, "minecraft.jar");

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Nothing to do, and nothing to clean up either: the unmodded jar is used directly.
        if (_jarMods.Count == 0)
        {
            return Task.CompletedTask;
        }

        /*
         * UPSTREAM BUG (#8), FIXED HERE. Upstream calls emitFailed() for both of the checks below and
         * then FALLS THROUGH — there is no return after either. So a failure to create the bin folder
         * reports the failure and then tries to write a jar into the folder that could not be created,
         * emitting a second, contradictory result. Neither is reproducible in a step that either
         * returns or throws, and reproducing the double-emit would mean deliberately continuing into
         * work that cannot succeed.
         */
        if (!FileSystem.EnsureFolderPathExists(_binRoot))
        {
            LogLine("Couldn't create the bin folder for Minecraft.jar", MessageLevel.Error);
            throw new TaskFailedException("Couldn't create the bin folder for Minecraft.jar");
        }

        if (!RemoveJar())
        {
            LogLine($"Couldn't remove stale jar file: {FinalJarPath}", MessageLevel.Error);
            throw new TaskFailedException($"Couldn't remove stale jar file: {FinalJarPath}");
        }

        if (!MMCZip.CreateModdedJar(_sourceJarPath, FinalJarPath, _jarMods))
        {
            throw new TaskFailedException("Failed to create the custom Minecraft jar file.");
        }

        return Task.CompletedTask;
    }

    /// <summary>The modded jar does not outlive the launch that built it.</summary>
    public override Task FinalizeAsync()
    {
        RemoveJar();
        return Task.CompletedTask;
    }

    private bool RemoveJar() => !File.Exists(FinalJarPath) || FileSystem.DeletePath(FinalJarPath);
}

/// <summary>
/// Marks an account as in use for as long as the game is running on it.
/// </summary>
/// <remarks>
/// The lock is what stops a token refresh happening under a running game — refreshing invalidates the
/// session the game is holding and drops the player from whatever server they are on. It is taken only
/// for a real online session: an offline or demo launch has nothing to invalidate.
///
/// Cannot be aborted, inherited. Releasing the lock partway through would defeat the point.
/// </remarks>
public sealed class ClaimAccount : LaunchStep
{
    private readonly MinecraftAccount? _account;

    private bool _held;

    public ClaimAccount(AuthSession session, MinecraftAccount? account) : base("Claim account")
    {
        ArgumentNullException.ThrowIfNull(session);

        _account = session is { Status: SessionStatus.PlayableOnline, Demo: false } ? account : null;
    }

    public override bool CanAbort => false;

    /// <summary>Whether the lock is currently held.</summary>
    public bool IsHeld => _held;

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_account is not null)
        {
            _account.IncrementUses();
            _held = true;
        }

        return Task.CompletedTask;
    }

    public override Task FinalizeAsync()
    {
        if (_held)
        {
            _account!.DecrementUses();
            _held = false;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes the diagnostic banner at the top of the log.
/// </summary>
/// <remarks>
/// Its only job is to make a pasted log answerable without a round trip. Everything it prints is
/// best-effort: a probe that is not installed, or that hangs, must not stop a launch.
/// </remarks>
public sealed class PrintInstanceInfo : LaunchStep
{
    /// <summary>A probe that will not hold up a launch for more than this.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<string> _instanceDescription;

    public PrintInstanceInfo(IReadOnlyList<string> instanceDescription) : base("Print instance info")
        => _instanceDescription = instanceDescription;

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var log = new List<string>();

        // Upstream probes on Linux and FreeBSD only. Windows and macOS get the instance description
        // alone, which is the part that answers most questions anyway.
        if (OperatingSystem.IsLinux())
        {
            ProbeProcCpuinfo(log);
            RunLspci(log);
            RunGlxinfo(log);
        }
        else if (OperatingSystem.IsFreeBSD())
        {
            RunSysctlHwModel(log);
            RunPciconf(log);
            RunGlxinfo(log);
        }

        if (log.Count != 0)
        {
            Log(log, MessageLevel.Launcher);
        }

        if (_instanceDescription.Count != 0)
        {
            Log(_instanceDescription, MessageLevel.Launcher);
        }

        return Task.CompletedTask;
    }

    private static void ProbeProcCpuinfo(List<string> log)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal))
                {
                    // Upstream takes everything from column 13, which is past "model name\t: ".
                    log.Add(line.Length > 13 ? line[13..] : line);
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No /proc, or no permission. Not worth a word in the log.
        }
    }

    private static void RunLspci(List<string> log)
    {
        var output = RunProbe("lspci", "-k");

        if (output is null)
        {
            return;
        }

        var lines = output.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // The VGA marker sits at a fixed offset in lspci's output, and the two lines after it are
            // the driver in use and the kernel modules — which is what actually diagnoses a GPU issue.
            if (line.Length < 9 || !line.AsSpan(8).StartsWith("VGA", StringComparison.Ordinal))
            {
                continue;
            }

            log.Add(line.Length > 35 ? line[35..] : line);

            for (var offset = 1; offset < 3 && i + offset < lines.Length; offset++)
            {
                var detail = lines[i + offset];
                log.Add(detail.Length > 1 ? detail[1..] : detail);
            }

            break;
        }
    }

    private static void RunGlxinfo(List<string> log)
    {
        var output = RunProbe("glxinfo");

        if (output is null)
        {
            return;
        }

        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("OpenGL version string:", StringComparison.Ordinal))
            {
                log.Add(line);
                break;
            }
        }
    }

    private static void RunSysctlHwModel(List<string> log)
    {
        if (RunProbe("sysctl", "hw.model")?.Split('\n').FirstOrDefault() is { Length: > 0 } model)
        {
            log.Add(model);
        }
    }

    private static void RunPciconf(List<string> log)
    {
        var output = RunProbe("pciconf", "-lv", "-a", "vgapci0");

        if (output is null)
        {
            return;
        }

        var parts = new List<string>();

        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith("    vendor", StringComparison.Ordinal)
                && !line.StartsWith("    device", StringComparison.Ordinal))
            {
                continue;
            }

            // The value is the bit between the single quotes.
            var start = line.IndexOf('\'');
            var end = line.LastIndexOf('\'');

            if (start >= 0 && end > start)
            {
                parts.Add(line[(start + 1)..end]);
            }
        }

        if (parts.Count != 0)
        {
            log.Add(string.Join(' ', parts));
        }
    }

    /// <summary>
    /// Runs a diagnostic command and returns its output.
    /// </summary>
    /// <returns><see langword="null"/> when the tool is absent, failed or took too long.</returns>
    /// <remarks>
    /// Upstream uses popen and waits indefinitely. A wedged glxinfo — which happens on a broken GL
    /// driver, exactly the case this probe exists to diagnose — would hang the launch forever, so
    /// there is a timeout here.
    /// </remarks>
    private static string? RunProbe(string program, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return output.IsCompletedSuccessfully ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // The tool is not installed. Common, and not worth a word in the log.
            return null;
        }
    }
}
