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
 * Ported from launcher/minecraft/Component.{h,cpp}.
 *
 * One entry in an instance's version stack: "Minecraft 1.20.1", "Fabric Loader 0.14.21", or a user's
 * own customised patch. A component is backed EITHER by a metadata version fetched from the meta
 * server, OR by a local JSON override the user has edited. The override wins, and that is the whole
 * customise/revert mechanism -- customising copies the metadata version to a local file, reverting
 * deletes it.
 *
 * DEPENDENCIES INVERTED. Upstream carries a PackProfile* back-reference (used only to reach the
 * runtime context) and calls APPLICATION->metadataIndex() directly. Both are passed in here instead,
 * which removes the parent pointer and the global, and is what makes this testable in isolation.
 *
 * It lives in ExtremeLauncher.Meta rather than Minecraft because it holds a MetaVersion; Meta already
 * references Minecraft.
 *
 * NOT PORTED: KNOWN_MODLOADERS and knownConflictingComponents(), which need ModPlatform::ModLoaderType
 * from wave 8.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.Meta;

/// <summary>
/// A pending change to a component, decided by dependency resolution and applied afterwards.
/// </summary>
/// <remarks>
/// Upstream is a <c>std::variant</c> visited with an overload set; here it is a closed record
/// hierarchy matched with a switch. Same shape, and the compiler still checks the cases.
///
/// The actions are QUEUED rather than applied on the spot because applying one can produce more —
/// changing an important component's version invalidates whatever depended on it — so
/// ComponentUpdateTask loops until a pass adds nothing new.
/// </remarks>
public abstract record UpdateAction
{
    private UpdateAction()
    {
    }

    /// <summary>Nothing to do.</summary>
    public sealed record None : UpdateAction
    {
        public static readonly None Instance = new();
    }

    /// <summary>Pin the component to an exact version.</summary>
    public sealed record ChangeVersion(string TargetVersion) : UpdateAction;

    /// <summary>Take whatever this component's list recommends for the given parent, or the newest compatible.</summary>
    public sealed record LatestRecommendedCompatible(string ParentUid, string ParentName, string Version) : UpdateAction;

    /// <summary>Drop the component from the profile.</summary>
    public sealed record Remove : UpdateAction
    {
        public static readonly Remove Instance = new();
    }

    /// <summary>An important component moved; everything linked to it has to be reconsidered.</summary>
    public sealed record ImportantChanged(string OldVersion) : UpdateAction;
}

public sealed class Component : IProblemProvider
{
    private readonly List<PatchProblem> _componentProblems = [];

    public Component(string uid) => Uid = uid;

    public Component(string uid, VersionFile file)
    {
        Uid = uid;
        LocalFile = file;
    }

    /// <summary>Raised when cached display data actually changes.</summary>
    public event EventHandler? DataChanged;

    public string Uid { get; }

    /// <summary>
    /// The requested version. With a local override present, this is also what reverting returns to.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Added automatically to satisfy a dependency, and removable again automatically.</summary>
    public bool IsDependencyOnly { get; set; }

    /// <summary>The instance's main component, or otherwise not removable.</summary>
    public bool IsImportant { get; set; }

    public bool IsDisabled { get; set; }

    /// <summary>DEPRECATED: explicit ordering, only used when loading pre-component configs.</summary>
    public int Order { get; set; }

    public bool OrderOverride { get; set; }

    /// <summary>The metadata version this component tracks, once loaded.</summary>
    public MetaVersion? MetaVersion { get; set; }

    /// <summary>A local JSON override. Its presence is what makes a component "custom".</summary>
    public VersionFile? LocalFile { get; set; }

    public VersionList? VersionList { get; set; }

    public bool IsLoaded { get; set; }

    // ---------------------------------------------------------------- cached display data

    public string CachedName { get; private set; } = string.Empty;

    public string CachedVersion { get; private set; } = string.Empty;

    public RequireSet CachedRequires { get; private set; } = [];

    public RequireSet CachedConflicts { get; private set; } = [];

    /// <summary>A volatile component may be removed automatically once nothing needs it.</summary>
    public bool CachedVolatile { get; private set; }

    /// <summary>Restores cached data read straight from mmc-pack.json, without a version file.</summary>
    /// <remarks>
    /// The cache exists so an instance's component list can be shown before any metadata has been
    /// fetched. <see cref="UpdateCachedData"/> overwrites it once the real file arrives.
    /// </remarks>
    public void SetCachedData(string name, string version, RequireSet requires, RequireSet conflicts, bool isVolatile)
    {
        CachedName = name;
        CachedVersion = version;
        CachedRequires = requires;
        CachedConflicts = conflicts;
        CachedVolatile = isVolatile;
    }

    // ---------------------------------------------------------------- state

    /// <summary>
    /// The patch contents: the local override if there is one, otherwise the metadata version's.
    /// </summary>
    /// <remarks>
    /// QUIRK, preserved: the check is on <c>MetaVersion</c>, not on <c>LocalFile</c>. So once a
    /// metadata version is attached it wins even if a local file exists — the two are never both set
    /// in practice, because customising clears the metadata link.
    /// </remarks>
    public VersionFile? GetVersionFile() => MetaVersion is not null ? MetaVersion.Data : LocalFile;

    /// <summary>Backed by a local override rather than metadata.</summary>
    public bool IsCustom => LocalFile is not null;

    /// <summary>Can be turned into a local override.</summary>
    public bool IsCustomizable => MetaVersion is not null && GetVersionFile() is not null;

    /// <summary>An override can be discarded only if the metadata index still knows this uid.</summary>
    public bool IsRevertible(Index metadataIndex)
    {
        ArgumentNullException.ThrowIfNull(metadataIndex);
        return IsCustom && metadataIndex.HasUid(Uid);
    }

    public bool IsRemovable => !IsImportant;

    /// <summary>
    /// Always true.
    /// </summary>
    /// <remarks>
    /// Upstream's comment: "HACK, FIXME: this was too dumb and wouldn't follow dependency constraints
    /// anyway. For now hardcoded to 'true'." Preserved rather than silently improved, since making it
    /// honest would change which reorderings the UI permits.
    /// </remarks>
    public bool IsMoveable => true;

    public bool CanBeDisabled => IsRemovable && !IsDependencyOnly;

    /// <summary>A component that cannot be disabled is always enabled, whatever the flag says.</summary>
    public bool IsEnabled => !CanBeDisabled || !IsDisabled;

    public bool IsVersionChangeable => VersionList is { Versions.Count: > 0 };

    public string Name => CachedName.Length != 0 ? CachedName : Uid;

    public DateTimeOffset? ReleaseDateTime => MetaVersion?.Time;

    /// <summary>Where a local override for this component is stored, relative to the patches folder.</summary>
    public string Filename => $"{Uid}.json";

    /// <summary>
    /// Turns a metadata-backed component into an editable local override.
    /// </summary>
    /// <remarks>
    /// Ported from Component::customize. Writes the metadata version's data out as a patch file in the
    /// instance's patches folder, points <see cref="LocalFile"/> at it, and drops the metadata link.
    ///
    /// CLEARING THE METADATA LINK IS THE WHOLE POINT. Before customising, the meta server wins on
    /// every refresh; after, the local file does — which is exactly what "customise" means. Leaving
    /// <see cref="MetaVersion"/> set would let the next resolution silently overwrite the copy the
    /// user is now free to edit, and the QUIRK preserved on <see cref="GetVersionFile"/> — meta
    /// beating local whenever both are set — is why the clear is not optional.
    /// </remarks>
    /// <returns>False when the component is already custom or the file could not be written.</returns>
    public bool Customize(string patchesDirectory)
    {
        if (IsCustom)
        {
            return false;
        }

        var file = GetVersionFile();

        // Mirrors isCustomizable(): there has to be a metadata version to copy from.
        if (MetaVersion is null || file is null)
        {
            return false;
        }

        var path = PackProfile.PatchFilePathForUid(patchesDirectory, Uid);

        try
        {
            FileSystem.EnsureFilePathExists(path);
            File.WriteAllText(path, OneSixVersionFormat.VersionFileToJson(file).ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        LocalFile = file;
        MetaVersion = null;

        return true;
    }

    /// <summary>
    /// Discards a local override, returning the component to what the metadata server provides.
    /// </summary>
    /// <remarks>
    /// Ported from Component::revert. Deletes the patch file and clears <see cref="LocalFile"/>; the
    /// resolution that runs before every launch re-attaches the metadata version.
    ///
    /// A DELIBERATE DIVERGENCE: upstream also tries to reload the meta version from an ambient
    /// metadata index right here. The port has no such singleton to reach for, and does not need one
    /// — leaving the component unloaded means the next resolution repopulates it from the cache or
    /// the server, which is where every other component's metadata comes from anyway.
    /// </remarks>
    /// <returns>False only when the patch file existed and could not be deleted.</returns>
    public bool Revert(string patchesDirectory)
    {
        if (!IsCustom)
        {
            // Already not custom, as upstream: nothing to undo.
            return true;
        }

        var path = PackProfile.PatchFilePathForUid(patchesDirectory, Uid);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        LocalFile = null;

        // Forces the next resolution to fetch metadata for this uid again, rather than trusting a
        // stale in-memory copy of the file just deleted.
        IsLoaded = false;

        return true;
    }

    // ---------------------------------------------------------------- behaviour

    /// <summary>Applies this component's patch to the profile. Disabled components contribute nothing.</summary>
    public void ApplyTo(LaunchProfile profile, RuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!IsEnabled)
        {
            return;
        }

        var file = GetVersionFile();

        if (file is not null)
        {
            profile.Apply(file, runtimeContext);
            return;
        }

        // Nothing to apply, but the component's own problems still have to surface.
        profile.ApplyProblemSeverity(GetProblemSeverity());
    }

    /// <summary>Refreshes the cached display data from the current version file.</summary>
    /// <returns><see langword="true"/> if anything changed.</returns>
    public bool UpdateCachedData()
    {
        var file = GetVersionFile();

        if (file is null)
        {
            // The metadata went away; drop the stale requirements rather than keeping them.
            CachedRequires = [];
            CachedConflicts = [];
            DataChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        var changed = false;

        if (!string.Equals(CachedName, file.Name, StringComparison.Ordinal))
        {
            CachedName = file.Name;
            changed = true;
        }

        if (!string.Equals(CachedVersion, file.Version, StringComparison.Ordinal))
        {
            CachedVersion = file.Version;
            changed = true;
        }

        if (CachedVolatile != file.IsVolatile)
        {
            CachedVolatile = file.IsVolatile;
            changed = true;
        }

        if (!DeepCompare(CachedRequires, file.Requires))
        {
            CachedRequires = file.Requires;
            changed = true;
        }

        if (!DeepCompare(CachedConflicts, file.Conflicts))
        {
            CachedConflicts = file.Conflicts;
            changed = true;
        }

        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>
    /// Compares two requirement sets by their full contents.
    /// </summary>
    /// <remarks>
    /// Set equality alone is not enough: <see cref="Require"/> is keyed by uid, so two sets can be
    /// "equal" while pinning different versions. This is exactly what <c>Require.DeepEquals</c> is for.
    /// </remarks>
    internal static bool DeepCompare(RequireSet a, RequireSet b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var left in a)
        {
            if (!b.TryGetValue(left, out var right) || !left.DeepEquals(right))
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- problems

    public IReadOnlyList<PatchProblem> GetProblems()
    {
        var file = GetVersionFile();

        return file is null ? _componentProblems : [.. _componentProblems, .. file.GetProblems()];
    }

    public ProblemSeverity GetProblemSeverity()
    {
        var file = GetVersionFile();
        var fromFile = file?.GetProblemSeverity() ?? ProblemSeverity.None;

        return ComponentProblemSeverity > fromFile ? ComponentProblemSeverity : fromFile;
    }

    private ProblemSeverity ComponentProblemSeverity { get; set; } = ProblemSeverity.None;

    public void AddComponentProblem(ProblemSeverity severity, string description)
    {
        if (severity > ComponentProblemSeverity)
        {
            ComponentProblemSeverity = severity;
        }

        _componentProblems.Add(new PatchProblem(severity, description));
    }

    /// <summary>The change queued for this component, if any.</summary>
    public UpdateAction UpdateAction { get; private set; } = UpdateAction.None.Instance;

    public void SetUpdateAction(UpdateAction action) => UpdateAction = action;

    public void ClearUpdateAction() => UpdateAction = UpdateAction.None.Instance;

    public void ResetComponentProblems()
    {
        _componentProblems.Clear();
        ComponentProblemSeverity = ProblemSeverity.None;
    }

    public override string ToString() => $"{Uid} {CachedVersion}";
}
