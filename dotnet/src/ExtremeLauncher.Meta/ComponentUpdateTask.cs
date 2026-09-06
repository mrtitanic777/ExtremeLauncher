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
 * Ported from launcher/minecraft/ComponentUpdateTask.{h,cpp} — the loading half. The resolution
 * arithmetic already lives in DependencyResolver.cs; this is what surrounds it.
 *
 * WHAT THIS DOES: turns a saved list of "net.minecraft 1.20.1, net.fabricmc.fabric-loader 0.15.0" into
 * a set of loaded patches ready to be flattened into a LaunchProfile. For each component it finds the
 * patch body — a local override on disk, an already-cached meta document, or a fetch — then resolves
 * what the components require of each other and applies the changes that fall out.
 *
 * A LOCAL PATCH FILE ALWAYS WINS over the meta server. That is the whole point of the patches folder:
 * a user who has hand-edited net.minecraft.json gets their version, not Mojang's.
 *
 * THE SIGNAL MACHINERY IS GONE. Upstream tracks remote loads through a RemoteLoadStatus list indexed
 * by task, with remoteLoadSucceeded / remoteLoadFailed / checkIfAllFinished counting completions and a
 * remoteTasksInProgress counter — roughly a hundred lines whose only job is "wait for all of these".
 * Here that is one Task.WhenAll. The bug class it removes is real: upstream's remoteLoadSucceeded
 * guards against being called twice for the same index and warns rather than failing when it is.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Meta;

/// <summary>Why the profile is being resolved.</summary>
public enum ComponentUpdateMode
{
    /// <summary>About to start the game: take what is on disk and do not reach for newer versions.</summary>
    Launch,

    /// <summary>The user is editing the version list: dependency changes are expected and wanted.</summary>
    Resolution,
}

public sealed class ComponentUpdateTask : LauncherTask
{
    /// <summary>How far one component got. Ordered: a worse result absorbs a better one.</summary>
    private enum LoadResult
    {
        LoadedLocal = 0,
        RequiresRemote = 1,
        Failed = 2,
    }

    private readonly PackProfile _profile;
    private readonly Index _index;
    private readonly string _patchesDirectory;
    private readonly ComponentUpdateMode _mode;
    private readonly NetMode _netMode;
    private readonly Func<string, string, CancellationToken, Task<bool>>? _loadVersion;

    /// <param name="loadVersion">
    /// Fetches one version document from the meta server. Null means nothing can be fetched, which is
    /// what offline mode amounts to.
    /// </param>
    public ComponentUpdateTask(
        PackProfile profile,
        Index index,
        string patchesDirectory,
        ComponentUpdateMode mode = ComponentUpdateMode.Launch,
        NetMode netMode = NetMode.Online,
        Func<string, string, CancellationToken, Task<bool>>? loadVersion = null)
        : base("Update components")
    {
        _profile = profile;
        _index = index;
        _patchesDirectory = patchesDirectory;
        _mode = mode;
        _netMode = netMode;
        _loadVersion = loadVersion;
    }

    public override bool CanAbort => true;

    /// <summary>What dependency resolution decided. Null until the task has run.</summary>
    public ResolutionResult? Resolution { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus("Loading components");

        var result = await LoadComponentsAsync(cancellationToken).ConfigureAwait(false);

        if (result == LoadResult.Failed)
        {
            throw new TaskFailedException("Some component metadata load tasks failed.");
        }

        PerformUpdateActions();

        // In Launch mode, or offline, resolution only REPORTS problems — it must not start changing
        // versions under someone who is trying to play. Upstream passes the same condition as
        // "checkOnly".
        var checkOnly = _mode == ComponentUpdateMode.Launch || _netMode == NetMode.Offline;

        Resolution = DependencyResolver.Resolve(_profile);

        if (!checkOnly)
        {
            ApplyResolution(Resolution);
            PerformUpdateActions();
        }

        FinalizeComponents();

        foreach (var problem in Resolution.Problems)
        {
            _profile.GetComponent(problem.Uid)?.AddComponentProblem(ProblemSeverity.Error, problem.Description);
        }
    }

    // ================================================================== loading

    private async Task<LoadResult> LoadComponentsAsync(CancellationToken cancellationToken)
    {
        var result = LoadResult.LoadedLocal;
        var pending = new List<Task<bool>>();

        foreach (var component in _profile.Components)
        {
            component.ResetComponentProblems();

            var single = LoadComponent(component, pending, cancellationToken);

            if (single == LoadResult.LoadedLocal)
            {
                component.UpdateCachedData();
            }

            // A worse result absorbs a better one, so one failure taints the whole pass.
            result = (LoadResult)Math.Max((int)result, (int)single);
        }

        if (pending.Count == 0)
        {
            return result;
        }

        // One await where upstream keeps a status list, two callbacks and a counter.
        var outcomes = await Task.WhenAll(pending).ConfigureAwait(false);

        if (outcomes.Any(ok => !ok))
        {
            return LoadResult.Failed;
        }

        // Everything that was fetched now has a body; cache what the patches say about themselves.
        foreach (var component in _profile.Components)
        {
            if (!component.IsLoaded && component.GetVersionFile() is not null)
            {
                component.IsLoaded = true;
            }

            component.UpdateCachedData();
        }

        return result == LoadResult.RequiresRemote ? LoadResult.LoadedLocal : result;
    }

    private LoadResult LoadComponent(Component component, List<Task<bool>> pending, CancellationToken cancellationToken)
    {
        if (component.IsLoaded)
        {
            return LoadResult.LoadedLocal;
        }

        var customPatchFilename = PackProfile.PatchFilePathForUid(_patchesDirectory, component.Uid);

        if (File.Exists(customPatchFilename))
        {
            return LoadLocalPatch(component, customPatchFilename);
        }

        /*
         * GetOrCreate, not Get. On a first run the index has not been fetched, so nothing is known and
         * a pure lookup returns null for every component — an earlier draft of this treated that as
         * "no metadata is available" and failed the whole cold-cache path, which is the one every new
         * install takes. Upstream's getVersion creates a placeholder for exactly this reason: the
         * document's URL is derivable from the uid and version alone, so it can be fetched before
         * anything above it is known.
         */
        var metaVersion = _index.GetOrCreate(component.Uid, component.Version);

        component.MetaVersion = metaVersion;

        if (metaVersion.IsLoaded)
        {
            component.IsLoaded = true;
            return LoadResult.LoadedLocal;
        }

        if (_netMode == NetMode.Online && _loadVersion is not null)
        {
            pending.Add(_loadVersion(component.Uid, component.Version, cancellationToken));
            return LoadResult.RequiresRemote;
        }

        // Nothing on disk and no way to fetch it. Named rather than left as a bare failure, because
        // "offline with a cold cache" and "this component does not exist" look identical otherwise.
        component.AddComponentProblem(
            ProblemSeverity.Error,
            $"No metadata is available for {component.Uid} {component.Version}, and it cannot be fetched.");

        // Offline, and the document is not cached. Upstream re-checks isLoaded here after starting a
        // task that cannot run; the answer is the same either way.
        return LoadResult.Failed;
    }

    /// <remarks>
    /// UID REPAIR, inherited and load-bearing. A patch file whose "uid" disagrees with the component
    /// it was loaded for is REWRITTEN on disk to match. Files get copied between instances and renamed
    /// by hand, and a mismatched uid makes the patch invisible to dependency resolution — so the
    /// mismatch is corrected rather than reported.
    ///
    /// Upstream's own comment on the save is "FIXME: @QUALITY do not ignore return value". Kept
    /// ignored: a patches folder that cannot be written to should not stop a launch that has a
    /// perfectly good patch already in memory.
    /// </remarks>
    private static LoadResult LoadLocalPatch(Component component, string filename)
    {
        VersionFile file;

        try
        {
            file = OneSixVersionFormat.VersionFileFromJson(
                Json.RequireObject(Json.RequireDocument(File.ReadAllText(filename), filename), filename),
                filename,
                requireOrder: false);
        }
        catch (JsonException e)
        {
            component.AddComponentProblem(ProblemSeverity.Error, $"Could not read {filename}: {e.Message}");
            return LoadResult.Failed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            component.AddComponentProblem(ProblemSeverity.Error, $"Could not read {filename}: {e.Message}");
            return LoadResult.Failed;
        }

        if (!string.Equals(file.Uid, component.Uid, StringComparison.Ordinal))
        {
            file.Uid = component.Uid;

            try
            {
                File.WriteAllText(filename, OneSixVersionFormat.VersionFileToJson(file).ToJsonString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Deliberately not fatal; see the remarks above.
            }
        }

        component.LocalFile = file;
        component.IsLoaded = true;

        return LoadResult.LoadedLocal;
    }

    // ================================================================== applying what was resolved

    private void ApplyResolution(ResolutionResult resolution)
    {
        foreach (var uid in resolution.ToRemove)
        {
            _profile.GetComponent(uid)?.SetUpdateAction(UpdateAction.Remove.Instance);
        }

        foreach (var requirement in resolution.ToChange)
        {
            var version = requirement.EqualsVersion.Length != 0 ? requirement.EqualsVersion : requirement.Suggests;

            if (version.Length != 0)
            {
                _profile.GetComponent(requirement.Uid)?.SetUpdateAction(new UpdateAction.ChangeVersion(version));
            }
        }

        foreach (var requirement in resolution.ToAdd)
        {
            if (_profile.GetComponent(requirement.Uid) is not null)
            {
                continue;
            }

            var version = VersionForNewDependency(requirement);

            var component = new Component(requirement.Uid) { Version = version, IsDependencyOnly = true };
            _profile.AppendComponent(component);
        }
    }

    /// <summary>
    /// Decides what version a newly added dependency should be.
    /// </summary>
    /// <remarks>
    /// A requirement can name an exact version, suggest one, or say NOTHING AT ALL -- and the last is
    /// not rare. Fabric declares `requires: [{ "uid": "net.fabricmc.intermediary" }]` with no version
    /// constraint whatsoever, because which intermediary you need is decided by your Minecraft
    /// version rather than by Fabric.
    ///
    /// Upstream's answer is a small hardcoded table, and it does not pretend otherwise -- the code is
    /// wrapped in a banner of hashes and reads:
    ///
    ///     HACK HACK HACK HACK FIXME: this is a placeholder for deciding what version to use.
    ///     For now, it is hardcoded.
    ///
    /// Ported as it stands, because the alternative is inventing a constraint solver that upstream
    /// does not have and then disagreeing with it about which version an instance should get. The
    /// omission was not free: without this an added intermediary got an EMPTY version, and the launch
    /// asked the metadata server for `net.fabricmc.intermediary/.json` and took a 404. No Fabric or
    /// Quilt instance could start.
    /// </remarks>
    private string VersionForNewDependency(Requirement requirement)
    {
        if (requirement.EqualsVersion.Length != 0)
        {
            return requirement.EqualsVersion;
        }

        if (requirement.Suggests.Length != 0)
        {
            return requirement.Suggests;
        }

        return requirement.Uid switch
        {
            // Upstream's literals. They are floors rather than choices: a real lwjgl requirement
            // carries a `suggests` and never reaches here.
            "org.lwjgl" => "2.9.1",
            "org.lwjgl3" => "3.1.2",

            /*
             * Fabric's and Quilt's mappings are published one-per-Minecraft-version and named after
             * it, so the instance's Minecraft version IS the answer.
             */
            "net.fabricmc.intermediary" or "org.quiltmc.hashed"
                => _profile.GetComponent("net.minecraft")?.Version ?? string.Empty,

            _ => string.Empty,
        };
    }

    /// <summary>
    /// Applies every queued update action, repeating until a pass queues nothing new.
    /// </summary>
    /// <remarks>
    /// THE LOOP IS NOT DECORATION. Applying an ImportantChanged queues actions on everything linked to
    /// that component, and those can queue more in turn: changing Minecraft's version can force a new
    /// Forge, which can force a new Forge-dependent library. One pass would leave the profile
    /// half-updated, which is worse than not updating it.
    /// </remarks>
    private void PerformUpdateActions()
    {
        bool addedActions;

        do
        {
            addedActions = false;
            var toRemove = new List<string>();

            foreach (var component in _profile.Components.ToList())
            {
                switch (component.UpdateAction)
                {
                    case UpdateAction.None:
                        break;

                    case UpdateAction.ChangeVersion changeVersion:
                        component.Version = changeVersion.TargetVersion;
                        ReloadComponent(component);
                        break;

                    case UpdateAction.LatestRecommendedCompatible compatible:
                        ApplyLatestRecommendedCompatible(component, compatible);
                        break;

                    case UpdateAction.Remove:
                        toRemove.Add(component.Uid);
                        break;

                    case UpdateAction.ImportantChanged important:
                        addedActions |= ApplyImportantChanged(component, important);
                        break;
                }

                component.ClearUpdateAction();
            }

            foreach (var uid in toRemove)
            {
                _profile.Remove(uid);
            }
        }
        while (addedActions);
    }

    private void ApplyLatestRecommendedCompatible(Component component, UpdateAction.LatestRecommendedCompatible action)
    {
        var versionList = _index.HasUid(component.Uid) ? _index.Get(component.Uid) : null;

        if (versionList is null)
        {
            component.AddComponentProblem(
                ProblemSeverity.Error,
                $"No version list in metadata index for {component.Uid}");

            return;
        }

        // Recommended first, then merely compatible. A list with neither means this component cannot
        // coexist with the parent's new version at all, which is a problem worth naming.
        var chosen = versionList.GetRecommendedForParent(action.ParentUid, action.Version)
                     ?? versionList.GetLatestForParent(action.ParentUid, action.Version);

        if (chosen is null)
        {
            component.AddComponentProblem(
                ProblemSeverity.Error,
                $"No compatible version of {component.Name} found for {action.ParentName} {action.Version}");

            return;
        }

        component.Version = chosen.VersionString;
        ReloadComponent(component);
    }

    /// <returns>Whether anything new was queued.</returns>
    private bool ApplyImportantChanged(Component component, UpdateAction.ImportantChanged action)
    {
        var addedActions = false;

        // Whatever the OLD version required and the new one does not is no longer wanted here.
        if (_index.Get(component.Uid, action.OldVersion) is { Data: not null } oldVersion)
        {
            foreach (var oldRequirement in oldVersion.Requires)
            {
                if (component.CachedRequires.Contains(oldRequirement))
                {
                    continue;
                }

                if (_profile.GetComponent(oldRequirement.Uid) is { } stale)
                {
                    stale.SetUpdateAction(UpdateAction.Remove.Instance);
                    addedActions = true;
                }
            }
        }

        foreach (var linked in DependencyResolver.CollectTreeLinked(_profile, component.Uid))
        {
            // A hand-edited patch is the user's own decision and is never rewritten by this.
            if (linked.IsCustom)
            {
                continue;
            }

            // Require is a value type, so "not found" is a default-valued struct rather than null.
            var newVersion = string.Empty;

            foreach (var requirement in component.CachedRequires)
            {
                if (!string.Equals(requirement.Uid, linked.Uid, StringComparison.Ordinal))
                {
                    continue;
                }

                newVersion = requirement.EqualsVersion.Length != 0 ? requirement.EqualsVersion : requirement.Suggests;
                break;
            }

            linked.SetUpdateAction(newVersion.Length != 0
                ? new UpdateAction.ChangeVersion(newVersion)
                : new UpdateAction.LatestRecommendedCompatible(component.Uid, component.Name, component.Version));

            addedActions = true;
        }

        return addedActions;
    }

    /// <summary>Drops a component's cached body so the next load picks up its new version.</summary>
    private void ReloadComponent(Component component)
    {
        component.IsLoaded = false;
        component.MetaVersion = null;

        var pending = new List<Task<bool>>();
        _ = LoadComponent(component, pending, CancellationToken.None);

        if (component.IsLoaded)
        {
            component.UpdateCachedData();
        }

        // A version change that needs a fresh fetch cannot be satisfied synchronously here. Upstream
        // blocks on waitLoadMeta(); this leaves the component unloaded and lets FinalizeComponents
        // report it, because blocking a task on a nested event loop is exactly what wave 2 removed.
        _profile.InvalidateLaunchProfile();
    }

    // ================================================================== reporting

    /// <summary>
    /// Turns unmet requirements and known conflicts into problems on the components that own them.
    /// </summary>
    /// <remarks>
    /// Severity is the point. A missing requirement or a wrong exact version is an ERROR; merely not
    /// being on the SUGGESTED version is a warning, because it usually works. A dependency that has
    /// problems of its own propagates them upward at its own severity, so the component list shows
    /// which entry to look at rather than only the one that ultimately broke.
    /// </remarks>
    private void FinalizeComponents()
    {
        foreach (var component in _profile.Components)
        {
            foreach (var requirement in component.CachedRequires)
            {
                var required = _profile.GetComponent(requirement.Uid);

                if (required is null)
                {
                    var wanted = requirement.EqualsVersion.Length != 0 ? requirement.EqualsVersion : requirement.Suggests;

                    component.AddComponentProblem(
                        ProblemSeverity.Error,
                        $"{component.Name} is missing requirement {requirement.Uid} {wanted}");

                    continue;
                }

                if (required.GetProblems().Count != 0)
                {
                    component.AddComponentProblem(
                        required.GetProblemSeverity(),
                        $"{required.Name}, a dependency of this component, has reported issues");
                }

                if (requirement.EqualsVersion.Length != 0
                    && !string.Equals(requirement.EqualsVersion, required.Version, StringComparison.Ordinal))
                {
                    component.AddComponentProblem(
                        ProblemSeverity.Error,
                        $"{required.Name}, a dependency of this component, is not the required version {requirement.EqualsVersion}");
                }
                else if (requirement.Suggests.Length != 0
                         && !string.Equals(requirement.Suggests, required.Version, StringComparison.Ordinal))
                {
                    component.AddComponentProblem(
                        ProblemSeverity.Warning,
                        $"{required.Name}, a dependency of this component, is not the suggested version {requirement.Suggests}");
                }
            }
        }
    }
}
