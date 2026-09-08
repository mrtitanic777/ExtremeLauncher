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
 * Ported from the component-building halves of ModrinthInstanceCreationTask.cpp and
 * FlameInstanceCreationTask.cpp.
 *
 * WHERE A PACK BECOMES AN INSTANCE. Both importers end at the same place -- a list of (uid, version)
 * components for mmc-pack.json -- but they arrive very differently, and that difference is the whole
 * of this file.
 *
 * MODRINTH NAMES ITS LOADERS. Its dependency block is already a map of loader to version, so the
 * conversion is a lookup table and nothing else can go wrong.
 *
 * CURSEFORGE PACKS LOADER AND VERSION INTO ONE STRING. "forge-47.2.0" has to be split, mapped to a
 * metadata uid, and then cleaned up for the special cases CurseForge has accumulated. Every one of
 * those steps can silently produce a loader no metadata server has heard of, which fails much later
 * as "component not found" rather than here as "this pack is odd".
 *
 * BOTH ARE PURE FUNCTIONS of the parsed manifest. Upstream builds a MinecraftInstance and a PackProfile
 * to hold the answer, which means the mapping cannot be exercised without a staging directory on disk.
 */

using System.Text.RegularExpressions;

using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>One entry of the component list an imported instance starts from.</summary>
/// <param name="Uid">The metadata uid, e.g. <c>net.fabricmc.fabric-loader</c>.</param>
/// <param name="Version">The version, or <see cref="PackComponents.RecommendedVersion"/>.</param>
/// <param name="Important">
/// Whether the component is one the user chose rather than one pulled in as a dependency. Only
/// Minecraft itself is important here, matching upstream's third argument to setComponentVersion.
/// </param>
public readonly record struct PackComponent(string Uid, string Version, bool Important = false);

public static partial class PackComponents
{
    public const string MinecraftUid = "net.minecraft";
    public const string FabricUid = "net.fabricmc.fabric-loader";
    public const string QuiltUid = "org.quiltmc.quilt-loader";
    public const string ForgeUid = "net.minecraftforge";
    public const string NeoForgeUid = "net.neoforged";

    /// <summary>
    /// The version CurseForge writes when it means "whatever the loader currently recommends".
    /// </summary>
    /// <remarks>
    /// Not a version at all, and it has to survive to the caller: resolving it needs the metadata
    /// index, which this file deliberately does not reach for. The caller looks up the recommended
    /// build and, for Forge and NeoForge only, filters it by the Minecraft version -- Fabric and
    /// Quilt releases are not tied to one.
    /// </remarks>
    public const string RecommendedVersion = "recommended";

    /// <summary>The components a Modrinth pack asks for.</summary>
    /// <remarks>
    /// A pack may name several loaders; each becomes its own component, exactly as upstream's
    /// independent if-statements do. Whether that combination is installable is the component
    /// resolver's problem, not the importer's.
    /// </remarks>
    public static List<PackComponent> FromModrinth(ModrinthPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var dependencies = manifest.Dependencies;

        // Minecraft first and marked important: it is the one component the user actually chose.
        var components = new List<PackComponent>
        {
            new(MinecraftUid, NormaliseMinecraftVersion(dependencies.MinecraftVersion), Important: true),
        };

        foreach (var (uid, version) in (ReadOnlySpan<(string, string)>)[
            (FabricUid, dependencies.FabricVersion),
            (QuiltUid, dependencies.QuiltVersion),
            (ForgeUid, dependencies.ForgeVersion),
            (NeoForgeUid, dependencies.NeoForgeVersion),
        ])
        {
            if (version.Length != 0)
            {
                components.Add(new PackComponent(uid, version));
            }
        }

        return components;
    }

    /// <summary>The components a CurseForge pack asks for.</summary>
    /// <returns>
    /// Minecraft, plus at most one loader. A loader version of <see cref="RecommendedVersion"/> is
    /// passed through for the caller to resolve.
    /// </returns>
    public static List<PackComponent> FromFlame(FlamePackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var components = new List<PackComponent>
        {
            new(MinecraftUid, NormaliseMinecraftVersion(manifest.Minecraft.Version), Important: true),
        };

        // An unrecognised loader is skipped rather than fatal: upstream warns and carries on, and a
        // pack naming a loader this launcher does not support is still playable without it.
        if (FlamePack.GetPrimaryModloader(manifest) is { } loader
            && ResolveLoader(loader.Id) is { } resolved)
        {
            components.Add(resolved);
        }

        return components;
    }

    /// <summary>The components an ATLauncher pack version asks for.</summary>
    /// <returns>
    /// Minecraft, plus the loader the version's <c>loader</c> block names. The loader version is passed
    /// through as the pack states it, for the caller to resolve against the metadata index — the same
    /// approach as <see cref="FromFlame"/>.
    /// </returns>
    /// <exception cref="LauncherException">
    /// The loader type is set but not one this launcher knows, which upstream treats as a fatal install
    /// error ("Unknown loader type"). An empty loader type is fine — a vanilla pack has no loader.
    /// </exception>
    public static List<PackComponent> FromAtl(AtlPackVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var components = new List<PackComponent>
        {
            new(MinecraftUid, NormaliseMinecraftVersion(version.Minecraft), Important: true),
        };

        var type = version.Loader.Type;

        var uid = type switch
        {
            "" => null,
            "forge" => ForgeUid,
            "neoforge" => NeoForgeUid,
            "fabric" => FabricUid,
            _ => throw new LauncherException($"Unknown loader type: {type}"),
        };

        if (uid is not null)
        {
            components.Add(new PackComponent(uid, version.Loader.Version));
        }

        return components;
    }

    /// <summary>
    /// Maps a CurseForge loader id such as "forge-47.2.0" to a component.
    /// </summary>
    /// <returns>Null when the loader is not one this launcher knows.</returns>
    public static PackComponent? ResolveLoader(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var (name, version) = FlamePack.SplitModloaderId(id);

        var uid = name.ToLowerInvariant() switch
        {
            "neoforge" => NeoForgeUid,
            "forge" => ForgeUid,
            "fabric" => FabricUid,
            "quilt" => QuiltUid,
            _ => null,
        };

        if (uid is null)
        {
            return null;
        }

        if (uid == NeoForgeUid)
        {
            version = StripNeoForgeMinecraftPrefix(version);
        }

        return new PackComponent(uid, version);
    }

    /// <summary>
    /// Removes the Minecraft version NeoForge briefly prefixed its own with.
    /// </summary>
    /// <remarks>
    /// For 1.20.1 only, CurseForge writes <c>neoforge-1.20.1-47.1.0</c> where every other version is
    /// <c>neoforge-20.4.190</c>. Upstream hardcodes the string "1.20.1-" and calls it "a mess for
    /// curseforge", which it is. Left as a special case rather than generalised: NeoForge's scheme
    /// changed exactly once, and a rule clever enough to cover both would also mangle a legitimate
    /// version that happened to start with digits and a dot.
    /// </remarks>
    public static string StripNeoForgeMinecraftPrefix(string version)
        => version.StartsWith("1.20.1-", StringComparison.Ordinal) ? version["1.20.1-".Length..] : version;

    /// <summary>
    /// Removes trailing dots from a Minecraft version.
    /// </summary>
    /// <remarks>
    /// Upstream's comment calls these "mysterious trailing dots" and logs a warning when it finds
    /// them. They come from hand-edited manifests, and "1.20.1." matches no version on any metadata
    /// server, so the import fails at component resolution with nothing to suggest the cause.
    /// </remarks>
    public static string NormaliseMinecraftVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return TrailingDots().Replace(version, string.Empty);
    }

    [GeneratedRegex(@"\.+$")]
    private static partial Regex TrailingDots();
}
