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
 * Ported from launcher/minecraft/VersionFile.h.
 *
 * One "patch" in an instance's version stack. A vanilla Minecraft version is one of these; so is
 * Forge, or Fabric, or a user's custom jar mod. PackProfile later merges the stack into a single
 * LaunchProfile, which is why order and uid matter.
 *
 * Populated by MojangVersionFormat (Mojang's own shape) and OneSixVersionFormat (the launcher's
 * richer superset, which adds jar mods, agents, traits, JVM args and dependency declarations).
 *
 * NOT PORTED: `runtimes`, which needs Java::Metadata from wave 4's deferred half.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

public sealed class VersionFile : ProblemContainer
{
    /// <summary>Sort order within the version stack; lower is applied first.</summary>
    public int Order { get; set; }

    /// <summary>Human-readable name, e.g. "Minecraft".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Machine identifier, e.g. "net.minecraft".</summary>
    public string Uid { get; set; } = string.Empty;

    /// <summary>This patch's own version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The "id" field of a Mojang version JSON.</summary>
    public string MinecraftVersion { get; set; } = string.Empty;

    public string MainClass { get; set; } = string.Empty;

    public string AppletClass { get; set; } = string.Empty;

    /// <summary>The pre-1.13 flat argument string.</summary>
    public string MinecraftArguments { get; set; } = string.Empty;

    /// <summary>"release", "snapshot", "old_beta"…</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The asset index name, e.g. "1.9".</summary>
    public string Assets { get; set; } = string.Empty;

    public int MinimumLauncherVersion { get; set; } = -1;

    public DateTimeOffset? ReleaseTime { get; set; }

    public DateTimeOffset? UpdateTime { get; set; }

    public List<int> CompatibleJavaMajors { get; } = [];

    public string CompatibleJavaName { get; set; } = string.Empty;

    /// <summary>
    /// The downloadable Java runtimes this patch offers, one per platform.
    /// </summary>
    /// <remarks>
    /// Only the meta server's "net.minecraft.java" package carries these; every other patch leaves it
    /// empty. AutoInstallJava reads it to find the build matching the host's runtimeOS.
    /// </remarks>
    public List<Java.JavaMetadata> Runtimes { get; } = [];

    public List<Library> Libraries { get; } = [];

    /// <summary>"client"/"server" download entries.</summary>
    public Dictionary<string, MojangDownloadInfo> MojangDownloads { get; } = new(StringComparer.Ordinal);

    public MojangAssetIndexInfo? MojangAssetIndex { get; set; }

    // ---------------------------------------------------------------- OneSix additions

    /// <summary>LaunchWrapper tweaker classes this patch contributes.</summary>
    public List<string> AddTweakers { get; } = [];

    /// <summary>Behavioural flags consumed by the launch pipeline, e.g. "legacyLaunch".</summary>
    public HashSet<string> Traits { get; } = new(StringComparer.Ordinal);

    /// <summary>Extra JVM arguments this patch contributes.</summary>
    public List<string> AddnJvmArguments { get; } = [];

    /// <summary>Libraries that must be downloaded but not put on the classpath.</summary>
    public List<Library> MavenFiles { get; } = [];

    /// <summary>Mods merged into the main jar. A legacy mechanism, still supported.</summary>
    public List<Library> JarMods { get; } = [];

    public List<Library> Mods { get; } = [];

    public List<Agent> Agents { get; } = [];

    /// <summary>The Minecraft client jar itself, modelled as a library.</summary>
    public Library? MainJar { get; set; }

    public RequireSet Requires { get; } = [];

    public RequireSet Conflicts { get; } = [];

    /// <summary>A volatile patch may be replaced silently during resolution.</summary>
    public bool IsVolatile { get; set; }

    public override string ToString() => $"{Uid} {Version}";
}
