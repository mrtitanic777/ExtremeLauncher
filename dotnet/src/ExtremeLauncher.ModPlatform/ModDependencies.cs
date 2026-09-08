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
 * Ported from GetModDependenciesTask::getDependenciesForVersion -- the pure heart of mod dependency
 * resolution. When a mod version is about to be installed, this decides which of its dependencies
 * still need fetching: only the REQUIRED ones, with the Fabric/Quilt override applied (see
 * ModIndex.ApplyLoaderOverride), and only those not already accounted for -- not already in the result,
 * not among the mods being installed, and not already present in the instance. The task around it does
 * the network fetch and recursion; this is the filtering, which is the part that can be tested.
 *
 * A DIVERGENCE FROM UPSTREAM, DELIBERATE. Upstream keeps three separate "already have" lists (the
 * selected packs, the installed mods, the dependencies loaded so far) and checks each with slightly
 * different field comparisons -- and the loaded-dependencies check, on the Modrinth version-only path,
 * compares a stored version against the dependency's (empty) addon id, which can never match. Nothing
 * tests this, so the three are unified here into one KnownDependency set with one consistent rule.
 */

namespace ExtremeLauncher.ModPlatform;

/// <summary>
/// A dependency the caller already accounts for — a mod being installed, or one already in the
/// instance. Matched against a required dependency by provider and either addon id or, for a
/// Modrinth version-only reference, version.
/// </summary>
public readonly record struct KnownDependency(ResourceProvider Provider, string AddonId, string Version);

/// <summary>Deciding which of a version's dependencies still need to be fetched.</summary>
public static class ModDependencies
{
    /// <summary>
    /// The required dependencies of <paramref name="version"/> that are not yet accounted for. Each is
    /// run through the loader override first; optional, incompatible and embedded dependencies are
    /// ignored, as are duplicates within this version and anything already in
    /// <paramref name="alreadyPresent"/>.
    /// </summary>
    /// <param name="provider">The provider the version came from — overrides and matches are per-provider.</param>
    /// <param name="loaders">The instance's loaders, which decide the Fabric/Quilt override.</param>
    public static List<Dependency> NewRequiredDependencies(
        IndexedVersion version,
        ResourceProvider provider,
        ModLoaderTypes loaders,
        IEnumerable<KnownDependency> alreadyPresent)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(alreadyPresent);

        var known = alreadyPresent as ICollection<KnownDependency> ?? [.. alreadyPresent];
        var result = new List<Dependency>();

        foreach (var raw in version.Dependencies)
        {
            if (raw.Type != DependencyType.Required)
            {
                continue;
            }

            var dependency = ModIndex.ApplyLoaderOverride(raw, provider, loaders);

            // Modrinth may name a dependency by a specific file version rather than a project id; when
            // it does, matching is by version instead of addon id.
            var byVersion = provider == ResourceProvider.Modrinth && dependency.AddonId.Length == 0;

            if (result.Exists(d => Matches(d, dependency, byVersion))
                || known.Any(k => k.Provider == provider && Matches(k, dependency, byVersion)))
            {
                continue;
            }

            result.Add(dependency);
        }

        return result;
    }

    private static bool Matches(Dependency existing, Dependency wanted, bool byVersion)
        => byVersion ? existing.Version == wanted.Version : existing.AddonId == wanted.AddonId;

    private static bool Matches(KnownDependency existing, Dependency wanted, bool byVersion)
        => byVersion ? existing.Version == wanted.Version : existing.AddonId == wanted.AddonId;
}
