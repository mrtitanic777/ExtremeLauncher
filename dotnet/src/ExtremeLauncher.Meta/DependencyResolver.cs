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
 * Ported from the resolution half of launcher/minecraft/ComponentUpdateTask.cpp -- composeRequirement,
 * gatherRequirementsFromComponents, getTrivialRemovals and getTrivialComponentChanges.
 *
 * Works out what a component stack is missing, what it has too much of, and what cannot be satisfied
 * at all. Fabric Loader requires Minecraft 1.20.1 and Intermediary 1.20.1; this is what notices they
 * are absent, adds them, and later removes them again when Fabric goes away.
 *
 * DECOUPLED FROM LOADING, deliberately. Upstream's own comment on this code reads "FIXME, TODO:
 * decouple dependency resolution from loading -- this works directly with the PackProfile internals.
 * It shouldn't!". Here the algorithm is pure: components in, decisions out, no network and no
 * mutation. The task that acts on those decisions is separate.
 *
 * NOT PORTED: the remote-loading half (loadComponent, remoteLoadSucceeded, checkIfAllFinished,
 * performUpdateActions). It drives BaseEntity's fetch-and-cache flow, which is wave 5's deferred
 * piece; without it there is nothing to load from.
 */

using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.Meta;

/// <summary>A requirement, plus where in the stack the first component that wanted it sits.</summary>
/// <remarks>
/// The index decides where a newly added component is inserted, so a dependency lands before whatever
/// needed it rather than at the end of the stack.
/// </remarks>
public readonly struct Requirement : IEquatable<Requirement>, IComparable<Requirement>
{
    public Requirement(string uid, string equalsVersion = "", string suggests = "", int indexOfFirstDependee = 0)
    {
        Uid = uid;
        EqualsVersion = equalsVersion;
        Suggests = suggests;
        IndexOfFirstDependee = indexOfFirstDependee;
    }

    public string Uid { get; }

    /// <summary>An exact version pin, or empty for "any version".</summary>
    public string EqualsVersion { get; }

    /// <summary>A preferred version when nothing pins one.</summary>
    public string Suggests { get; }

    public int IndexOfFirstDependee { get; }

    // Keyed by uid, exactly like Require -- one requirement per package.
    public bool Equals(Requirement other) => string.Equals(Uid, other.Uid, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Requirement other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Uid ?? string.Empty);

    public int CompareTo(Requirement other) => string.CompareOrdinal(Uid, other.Uid);

    public override string ToString()
        => EqualsVersion.Length != 0 ? $"Req: {Uid} == {EqualsVersion}" : $"Req: {Uid}";
}

/// <summary>Why a stack could not be resolved.</summary>
public readonly record struct ResolutionProblem(string Uid, string Description);

public sealed class ResolutionResult
{
    /// <summary>Requirements with no component present; each should be installed.</summary>
    public List<Requirement> ToAdd { get; } = [];

    /// <summary>Requirements met by a component sitting at the wrong version, which may be changed.</summary>
    public List<Requirement> ToChange { get; } = [];

    /// <summary>Components nothing needs any more, which may be removed.</summary>
    public List<string> ToRemove { get; } = [];

    /// <summary>Conflicts that cannot be resolved automatically.</summary>
    public List<ResolutionProblem> Problems { get; } = [];

    public bool Succeeded => Problems.Count == 0;
}

public static class DependencyResolver
{
    /// <summary>
    /// Merges two requirements for the same package.
    /// </summary>
    /// <remarks>
    /// Two explicit pins that disagree are an unresolvable conflict. Two suggestions that disagree are
    /// not — the higher wins, compared with the launcher's FlexVer comparator so "1.10" beats "1.9".
    /// </remarks>
    /// <returns><see langword="false"/> when the pins conflict.</returns>
    public static bool TryCompose(Requirement a, Requirement b, out Requirement result)
    {
        if (!string.Equals(a.Uid, b.Uid, StringComparison.Ordinal))
        {
            throw new ArgumentException("Cannot compose requirements for different packages.", nameof(b));
        }

        var index = Math.Min(a.IndexOfFirstDependee, b.IndexOfFirstDependee);

        string equalsVersion;

        if (a.EqualsVersion.Length == 0)
        {
            equalsVersion = b.EqualsVersion;
        }
        else if (b.EqualsVersion.Length == 0)
        {
            equalsVersion = a.EqualsVersion;
        }
        else if (string.Equals(a.EqualsVersion, b.EqualsVersion, StringComparison.Ordinal))
        {
            equalsVersion = a.EqualsVersion;
        }
        else
        {
            result = new Requirement(a.Uid, string.Empty, string.Empty, index);
            return false;
        }

        string suggests;

        if (a.Suggests.Length == 0)
        {
            suggests = b.Suggests;
        }
        else if (b.Suggests.Length == 0)
        {
            suggests = a.Suggests;
        }
        else
        {
            suggests = new Core.Version(a.Suggests) < new Core.Version(b.Suggests) ? b.Suggests : a.Suggests;
        }

        result = new Requirement(a.Uid, equalsVersion, suggests, index);
        return true;
    }

    /// <summary>
    /// Collects every component's requirements into one set, composing duplicates.
    /// </summary>
    /// <returns><see langword="false"/> if any two components pin the same package to different versions.</returns>
    public static bool TryGatherRequirements(
        IReadOnlyList<Component> components,
        out SortedSet<Requirement> requirements,
        out List<ResolutionProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(components);

        requirements = [];
        problems = [];

        var succeeded = true;

        for (var index = 0; index < components.Count; index++)
        {
            foreach (var required in components[index].CachedRequires)
            {
                var incoming = new Requirement(required.Uid, required.EqualsVersion, required.Suggests, index);

                if (!requirements.TryGetValue(incoming, out var existing))
                {
                    requirements.Add(incoming);
                    continue;
                }

                if (TryCompose(incoming, existing, out var composed))
                {
                    // Replace rather than update: the set is keyed by uid, so the old entry has to go.
                    requirements.Remove(existing);
                    requirements.Add(composed);
                    continue;
                }

                problems.Add(new ResolutionProblem(
                    required.Uid,
                    $"Conflicting requirements for {required.Uid}: "
                    + $"'{incoming.EqualsVersion}' versus '{existing.EqualsVersion}'"));

                succeeded = false;
            }
        }

        return succeeded;
    }

    /// <summary>
    /// Finds components that can be dropped: installed only to satisfy a dependency, marked volatile,
    /// and no longer required by anything.
    /// </summary>
    /// <remarks>
    /// BOTH flags are needed. A dependency-only component that is not volatile stays — the user may
    /// have come to rely on it — and a volatile component the user installed themselves is theirs to
    /// remove. This is also why the <c>cachedVolatile</c> persistence bug mattered: with the flag
    /// lost on every reload, this method could never fire.
    /// </remarks>
    public static List<string> GetTrivialRemovals(
        IReadOnlyList<Component> components,
        SortedSet<Requirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(requirements);

        var result = new List<string>();

        foreach (var component in components)
        {
            if (!component.IsDependencyOnly || !component.CachedVolatile)
            {
                continue;
            }

            if (!requirements.Contains(new Requirement(component.Uid)))
            {
                result.Add(component.Uid);
            }
        }

        return result;
    }

    /// <summary>
    /// Decides, for each requirement, whether it is already met, needs a component added, needs one
    /// changed, or cannot be satisfied.
    /// </summary>
    /// <remarks>
    /// A version pin that disagrees with an installed component is only fixable when that component
    /// was itself installed as a dependency and is not customised. Anything the user chose or edited
    /// is left alone and reported as a conflict, rather than being silently changed underneath them.
    /// </remarks>
    public static bool TryGetTrivialChanges(
        PackProfile profile,
        SortedSet<Requirement> requirements,
        List<Requirement> toAdd,
        List<Requirement> toChange,
        List<ResolutionProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(requirements);

        var succeeded = true;

        foreach (var requirement in requirements)
        {
            var component = profile.GetComponent(requirement.Uid);

            if (component is null)
            {
                toAdd.Add(requirement);
                continue;
            }

            // No pin, and something is installed: satisfied.
            if (requirement.EqualsVersion.Length == 0)
            {
                continue;
            }

            if (string.Equals(component.Version, requirement.EqualsVersion, StringComparison.Ordinal))
            {
                continue;
            }

            if (component.IsDependencyOnly && !component.IsCustom)
            {
                toChange.Add(requirement);
                continue;
            }

            problems.Add(new ResolutionProblem(
                requirement.Uid,
                $"{requirement} is required, but {requirement.Uid} is at '{component.Version}' "
                + "and cannot be changed automatically."));

            succeeded = false;
        }

        return succeeded;
    }

    /// <summary>Runs the whole analysis over a stack, without changing it.</summary>
    public static ResolutionResult Resolve(PackProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var result = new ResolutionResult();

        TryGatherRequirements(profile.Components, out var requirements, out var gatherProblems);
        result.Problems.AddRange(gatherProblems);

        TryGetTrivialChanges(profile, requirements, result.ToAdd, result.ToChange, result.Problems);
        result.ToRemove.AddRange(GetTrivialRemovals(profile.Components, requirements));

        return result;
    }

    /// <summary>Components that depend on <paramref name="uid"/>, or that it depends on.</summary>
    /// <remarks>Used to decide what else has to be reloaded when one component changes.</remarks>
    public static List<Component> CollectTreeLinked(PackProfile profile, string uid)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var linked = new List<Component>();

        // Anything that depends on it.
        foreach (var component in profile.Components)
        {
            if (component.CachedRequires.Any(r => string.Equals(r.Uid, uid, StringComparison.Ordinal)))
            {
                linked.Add(component);
            }
        }

        // ...and anything it depends on, if it is installed.
        if (profile.GetComponent(uid) is { } self)
        {
            foreach (var required in self.CachedRequires)
            {
                if (profile.GetComponent(required.Uid) is { } dependency)
                {
                    linked.Add(dependency);
                }
            }
        }

        return linked;
    }
}
