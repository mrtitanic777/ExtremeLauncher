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
 * Ported from launcher/minecraft/LaunchProfile.{h,cpp} and VersionFile::applyTo.
 *
 * The flattened result of applying a whole patch stack in order: main class, arguments, classpath,
 * natives, tweakers, traits. This is what the launch pipeline actually reads, so it is the last thing
 * standing between the resolver and a running game.
 *
 * MERGE RULES DIFFER PER FIELD, and the differences are the whole point:
 *   strings      -- last non-empty wins, so Forge can override Minecraft's main class
 *   jvm args     -- appended, never replaced
 *   traits       -- unioned
 *   tweakers     -- de-duplicated, and a repeat MOVES the entry later rather than keeping it early
 *   libraries    -- one entry per artifact, highest version wins
 *   maven files  -- appended with no de-duplication at all, deliberately
 *
 * !! ONE UPSTREAM BUG FIXED -- see PORTING.md !!
 * applyMods() returns instead of continuing after appending a new mod, so a patch contributing
 * several new mods only ever gets its first one applied. Fixed and tested.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

public sealed class LaunchProfile
{
    private readonly List<Library> _libraries = [];
    private readonly List<Library> _nativeLibraries = [];
    private readonly List<Library> _mavenFiles = [];
    private readonly List<Library> _jarMods = [];
    private readonly List<Library> _mods = [];
    private readonly List<Agent> _agents = [];
    private readonly List<string> _tweakers = [];
    private readonly List<string> _addnJvmArguments = [];
    private readonly List<int> _compatibleJavaMajors = [];
    private readonly HashSet<string> _traits = new(StringComparer.Ordinal);

    public string MinecraftVersion { get; private set; } = string.Empty;

    public string MinecraftVersionType { get; private set; } = string.Empty;

    public MojangAssetIndexInfo? MinecraftAssets { get; private set; }

    public string MinecraftArguments { get; private set; } = string.Empty;

    public string MainClass { get; private set; } = string.Empty;

    public string AppletClass { get; private set; } = string.Empty;

    public string CompatibleJavaName { get; private set; } = string.Empty;

    public Library? MainJar { get; private set; }

    public ProblemSeverity ProblemSeverity { get; private set; } = ProblemSeverity.None;

    public IReadOnlyList<Library> Libraries => _libraries;

    public IReadOnlyList<Library> NativeLibraries => _nativeLibraries;

    public IReadOnlyList<Library> MavenFiles => _mavenFiles;

    public IReadOnlyList<Library> JarMods => _jarMods;

    public IReadOnlyList<Library> Mods => _mods;

    public IReadOnlyList<Agent> Agents => _agents;

    public IReadOnlyList<string> Tweakers => _tweakers;

    public IReadOnlyList<string> AddnJvmArguments => _addnJvmArguments;

    public IReadOnlyList<int> CompatibleJavaMajors => _compatibleJavaMajors;

    public IReadOnlySet<string> Traits => _traits;

    public bool HasTrait(string trait) => _traits.Contains(trait);

    public void Clear()
    {
        MinecraftVersion = string.Empty;
        MinecraftVersionType = string.Empty;
        MinecraftAssets = null;
        MinecraftArguments = string.Empty;
        MainClass = string.Empty;
        AppletClass = string.Empty;
        CompatibleJavaName = string.Empty;
        MainJar = null;
        ProblemSeverity = ProblemSeverity.None;

        _addnJvmArguments.Clear();
        _tweakers.Clear();
        _libraries.Clear();
        _nativeLibraries.Clear();
        _mavenFiles.Clear();
        _agents.Clear();
        _traits.Clear();
        _jarMods.Clear();
        _mods.Clear();
        _compatibleJavaMajors.Clear();
    }

    // ================================================================== apply

    /// <summary>Applies one patch. Only real Minecraft may set the version, type and assets.</summary>
    public void Apply(VersionFile patch, RuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (IsMinecraftVersion(patch.Uid))
        {
            ApplyMinecraftVersion(patch.Version);
            ApplyMinecraftVersionType(patch.Type);

            // HACK, upstream's: asset indexes from anything but Minecraft itself are ignored, a
            // workaround for the 2017 S3 outage that left other packages pointing at dead URLs.
            ApplyMinecraftAssets(patch.MojangAssetIndex);
        }

        ApplyMainJar(patch.MainJar);
        ApplyMainClass(patch.MainClass);
        ApplyAppletClass(patch.AppletClass);
        ApplyMinecraftArguments(patch.MinecraftArguments);
        ApplyAddnJvmArguments(patch.AddnJvmArguments);
        ApplyTweakers(patch.AddTweakers);
        ApplyJarMods(patch.JarMods);
        ApplyMods(patch.Mods);
        ApplyTraits(patch.Traits);
        ApplyCompatibleJavaMajors(patch.CompatibleJavaMajors);
        ApplyCompatibleJavaName(patch.CompatibleJavaName);

        foreach (var library in patch.Libraries)
        {
            ApplyLibrary(library, runtimeContext);
        }

        foreach (var mavenFile in patch.MavenFiles)
        {
            ApplyMavenFile(mavenFile, runtimeContext);
        }

        foreach (var agent in patch.Agents)
        {
            ApplyAgent(agent, runtimeContext);
        }

        ApplyProblemSeverity(patch.GetProblemSeverity());
    }

    public static bool IsMinecraftVersion(string uid) => string.Equals(uid, "net.minecraft", StringComparison.Ordinal);

    public void ApplyMinecraftVersion(string id) => MinecraftVersion = ApplyString(id, MinecraftVersion);

    public void ApplyMinecraftVersionType(string type) => MinecraftVersionType = ApplyString(type, MinecraftVersionType);

    public void ApplyMainClass(string mainClass) => MainClass = ApplyString(mainClass, MainClass);

    public void ApplyAppletClass(string appletClass) => AppletClass = ApplyString(appletClass, AppletClass);

    public void ApplyMinecraftArguments(string arguments) => MinecraftArguments = ApplyString(arguments, MinecraftArguments);

    public void ApplyCompatibleJavaName(string javaName) => CompatibleJavaName = ApplyString(javaName, CompatibleJavaName);

    public void ApplyMinecraftAssets(MojangAssetIndexInfo? assets)
    {
        if (assets is not null)
        {
            MinecraftAssets = assets;
        }
    }

    public void ApplyMainJar(Library? jar)
    {
        if (jar is not null)
        {
            MainJar = jar;
        }
    }

    public void ApplyAddnJvmArguments(IEnumerable<string> arguments) => _addnJvmArguments.AddRange(arguments);

    public void ApplyCompatibleJavaMajors(IEnumerable<int> majors) => _compatibleJavaMajors.AddRange(majors);

    public void ApplyTraits(IEnumerable<string> traits) => _traits.UnionWith(traits);

    public void ApplyJarMods(IEnumerable<Library> jarMods) => _jarMods.AddRange(jarMods);

    /// <summary>
    /// Appends tweakers, moving any that were already present to the end.
    /// </summary>
    /// <remarks>
    /// Order is load order, so a patch re-declaring a tweaker is asking for it to run later. Dropping
    /// the re-declaration instead would change mod-loader initialisation order.
    /// </remarks>
    public void ApplyTweakers(IReadOnlyList<string> tweakers)
    {
        var remaining = _tweakers.Where(existing => !tweakers.Contains(existing, StringComparer.Ordinal)).ToList();

        _tweakers.Clear();
        _tweakers.AddRange(remaining);
        _tweakers.AddRange(tweakers);
    }

    /// <remarks>
    /// UPSTREAM BUG FIXED: applyMods() returns rather than continues after appending a new mod, so a
    /// patch contributing several previously-unseen mods only ever applies its first.
    /// </remarks>
    public void ApplyMods(IEnumerable<Library> mods)
    {
        foreach (var mod in mods)
        {
            ApplyVersionedInto(_mods, mod);
        }
    }

    /// <summary>Adds a library, keeping only the highest version of each artifact.</summary>
    public void ApplyLibrary(Library library, RuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(library);

        if (!library.IsActive(runtimeContext))
        {
            return;
        }

        ApplyVersionedInto(library.IsNative ? _nativeLibraries : _libraries, library);
    }

    /// <remarks>
    /// Unlike libraries, maven files are NOT de-duplicated and NOT version-resolved — upstream says so
    /// explicitly. They are payloads to place on disk, not classpath entries.
    /// </remarks>
    public void ApplyMavenFile(Library mavenFile, RuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(mavenFile);

        if (!mavenFile.IsActive(runtimeContext) || mavenFile.IsNative)
        {
            return;
        }

        _mavenFiles.Add(Library.LimitedCopy(mavenFile));
    }

    public void ApplyAgent(Agent agent, RuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (!agent.Library.IsActive(runtimeContext) || agent.Library.IsNative)
        {
            return;
        }

        _agents.Add(agent);
    }

    /// <summary>Severity only ever escalates.</summary>
    public void ApplyProblemSeverity(ProblemSeverity severity)
    {
        if (ProblemSeverity < severity)
        {
            ProblemSeverity = severity;
        }
    }

    // ================================================================== classpath

    /// <summary>
    /// Resolves the profile to the concrete jar paths the JVM needs.
    /// </summary>
    /// <param name="tempPath">
    /// Where the jar-modded Minecraft jar is assembled. When jar mods are present the original main
    /// jar is deliberately NOT added — the merged one from here stands in for it.
    /// </param>
    public void GetLibraryFiles(
        RuntimeContext runtimeContext,
        List<string> jars,
        List<string> nativeJars,
        string overridePath = "",
        string tempPath = "")
    {
        List<string> native32 = [], native64 = [];

        jars.Clear();
        nativeJars.Clear();

        foreach (var library in _libraries)
        {
            library.GetApplicableFiles(runtimeContext, jars, nativeJars, native32, native64, overridePath);
        }

        // Order matters: the main jar goes last so libraries can shadow its classes.
        if (MainJar is { } mainJar)
        {
            if (_jarMods.Count != 0)
            {
                jars.Add(FileSystem.PathCombine(FileSystem.CleanPath(Path.GetFullPath(tempPath)), "minecraft.jar"));
            }
            else
            {
                mainJar.GetApplicableFiles(runtimeContext, jars, nativeJars, native32, native64, overridePath);
            }
        }

        foreach (var library in _nativeLibraries)
        {
            library.GetApplicableFiles(runtimeContext, jars, nativeJars, native32, native64, overridePath);
        }

        // The "${arch}" natives resolve to whichever bitness the JVM actually is.
        if (runtimeContext.JavaArchitecture == "32")
        {
            nativeJars.AddRange(native32);
        }
        else if (runtimeContext.JavaArchitecture == "64")
        {
            nativeJars.AddRange(native64);
        }
    }

    // ================================================================== internals

    /// <summary>Last non-empty wins; an empty value never clears an existing one.</summary>
    private static string ApplyString(string from, string to) => from.Length == 0 ? to : from;

    /// <summary>Adds, or replaces an existing entry for the same artifact when this one is newer.</summary>
    private static void ApplyVersionedInto(List<Library> target, Library incoming)
    {
        var copy = Library.LimitedCopy(incoming);
        var index = FindByName(target, incoming.Name);

        if (index < 0)
        {
            target.Add(copy);
            return;
        }

        if (new Core.Version(incoming.Version) > new Core.Version(target[index].Version))
        {
            target[index] = copy;
        }
    }

    /// <remarks>
    /// Returns -1 when there is more than one match, not just when there are none — an ambiguous
    /// artifact is treated as "not found" so it gets appended rather than silently overwriting one of
    /// several candidates.
    /// </remarks>
    private static int FindByName(List<Library> haystack, GradleSpecifier needle)
    {
        var result = -1;

        for (var i = 0; i < haystack.Count; i++)
        {
            if (!haystack[i].Name.MatchName(needle))
            {
                continue;
            }

            if (result != -1)
            {
                return -1;
            }

            result = i;
        }

        return result;
    }
}
