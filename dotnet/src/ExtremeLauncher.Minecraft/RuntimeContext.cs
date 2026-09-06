// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * Ported from launcher/RuntimeContext.h.
 *
 * Describes the platform a given instance will actually launch on: OS plus JVM architecture. This is
 * what decides which native libraries get extracted and which library rules apply, so getting the
 * architecture names wrong here means "the game silently fails to start" rather than a clean error.
 *
 * Mojang's own naming is inconsistent -- "amd64" from the JVM is "x86_64" in their classifiers -- so
 * MappedJavaRealArchitecture translates, and ClassifierMatches carries the legacy fallback for when
 * Mojang assumed x86/x86_64 were the only architectures that existed.
 */

namespace ExtremeLauncher.Minecraft;

public sealed class RuntimeContext
{
    /// <summary>The JVM's reported bitness, "32" or "64".</summary>
    public string JavaArchitecture { get; set; } = string.Empty;

    /// <summary>The JVM's reported architecture, e.g. "amd64" or "aarch64".</summary>
    public string JavaRealArchitecture { get; set; } = string.Empty;

    /// <summary>"windows", "linux" or "osx".</summary>
    public string System { get; set; } = CurrentSystem();

    public static string CurrentSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "osx" : "linux";
    }

    /// <summary>Translates the JVM's architecture name into Mojang's spelling.</summary>
    public string MappedJavaRealArchitecture()
        => JavaRealArchitecture switch
        {
            "amd64" => "x86_64",
            "i386" or "i686" => "x86",
            "aarch64" => "arm64",
            "arm" or "armhf" => "arm32",
            _ => JavaRealArchitecture,
        };

    /// <summary>The precise classifier, "[os]-[arch]".</summary>
    public string GetClassifier() => $"{System}-{MappedJavaRealArchitecture()}";

    /// <summary>
    /// Whether this is one of the two architectures Mojang originally assumed were the only ones.
    /// </summary>
    public bool IsLegacyArch()
    {
        var mapped = MappedJavaRealArchitecture();
        return mapped is "x86_64" or "x86";
    }

    /// <summary>
    /// Matches a classifier, accepting a bare OS name on legacy architectures.
    /// </summary>
    /// <remarks>
    /// The fallback is what makes old version JSONs work: they say "linux", not "linux-x86_64",
    /// because at the time there was nothing else it could mean.
    /// </remarks>
    public bool ClassifierMatches(string target)
    {
        if (string.Equals(target, GetClassifier(), StringComparison.Ordinal))
        {
            return true;
        }

        return IsLegacyArch() && string.Equals(target, System, StringComparison.Ordinal);
    }
}
