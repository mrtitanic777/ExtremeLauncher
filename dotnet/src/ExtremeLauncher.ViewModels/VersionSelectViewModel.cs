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
 * Ported in behaviour from launcher/ui/dialogs/VersionSelectDialog.cpp and InstallLoaderDialog.cpp.
 *
 * THE HALF OF THE VERSION PAGE THAT WAS MISSING. `CanChangeVersion` has been on ComponentViewModel
 * since wave 11 with no command behind it: the page could remove a component and reorder the list,
 * and there was no way to change what version anything was, or to add a loader to an instance that
 * did not have one. Upgrading a modpack's Minecraft version, or putting Fabric on a vanilla instance,
 * both had to be done by hand-editing mmc-pack.json.
 *
 * ONE VIEW MODEL FOR BOTH, because they are the same question asked twice: pick a version of a
 * component. Upstream has two dialogs -- VersionSelectDialog for "change this component" and
 * InstallLoaderDialog for "add one of these components" -- and the second is the first with a list of
 * uids to choose from first. Modelling that as a mode rather than a second class means the filtering,
 * the loading states and the empty cases are written and tested once.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;

namespace ExtremeLauncher.ViewModels;

public sealed partial class VersionSelectViewModel : ObservableObject
{
    private readonly IVersionListSource? _versions;

    /// <summary>The Minecraft version to filter loaders against, or empty not to filter.</summary>
    private readonly string _minecraftVersion;

    private Task? _loading;

    /// <param name="uid">The component whose versions to list.</param>
    /// <param name="title">What the dialog calls itself, e.g. "Change Minecraft version".</param>
    /// <param name="minecraftVersion">
    /// Filters loader versions to those declaring this as a requirement. Empty lists everything, which
    /// is right for Minecraft itself -- it requires nothing.
    /// </param>
    public VersionSelectViewModel(
        string uid,
        string title,
        IVersionListSource? versions = null,
        string minecraftVersion = "")
    {
        Uid = uid;
        Title = title;

        _versions = versions;
        _minecraftVersion = minecraftVersion;
    }

    public string Uid { get; }

    public string Title { get; }

    public ObservableCollection<VersionViewModel> Versions { get; } = [];

    [ObservableProperty]
    private VersionViewModel? _selected;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>
    /// Whether to show snapshots and old versions as well as releases.
    /// </summary>
    /// <remarks>
    /// Off by default, as upstream has it. The full Minecraft list is over a thousand entries and all
    /// but a hundred of them are snapshots; showing everything by default buries the version almost
    /// everybody wants.
    /// </remarks>
    [ObservableProperty]
    private bool _showAllVersions;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public bool CanAccept => Selected is not null && !IsLoading;

    /// <summary>The version the user chose, or empty if they cancelled.</summary>
    public string ChosenVersion { get; private set; } = string.Empty;

    private IReadOnlyList<MetaVersion> _all = [];

    /// <summary>
    /// Fetches the list, once.
    /// </summary>
    /// <remarks>
    /// THE IN-FLIGHT TASK IS SHARED, not re-started. The dialog awaits this on open and a filter change
    /// can ask again while it is still running; two concurrent rebuilds of the same collection is a
    /// crash that only shows up against a real server, which this port has already had once.
    /// </remarks>
    public Task LoadAsync(CancellationToken cancellationToken = default)
        => _loading ??= LoadCoreAsync(cancellationToken);

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_versions is null)
        {
            Status = "No metadata source is configured.";

            return;
        }

        IsLoading = true;
        Status = "Loading versions…";

        try
        {
            _all = await _versions.LoadAsync(Uid, cancellationToken).ConfigureAwait(true);

            Rebuild();

            Status = _all.Count == 0 ? "No versions were found." : string.Empty;
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            // Shown, not swallowed: an empty list with no explanation reads as "this component has no
            // versions", which is a different and much more alarming thing than "the server is down".
            Status = $"Could not load versions: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Rebuild()
    {
        var previous = Selected?.Version;

        Versions.Clear();

        foreach (var version in _all)
        {
            if (!Matches(version))
            {
                continue;
            }

            Versions.Add(new VersionViewModel
            {
                Version = version.VersionString,
                Type = version.Type,
                Released = version.Time,
                IsRecommended = version.IsRecommended,
            });
        }

        // Kept across a filter change where it survives, so ticking "show all" does not lose the row
        // somebody had already highlighted.
        Selected = Versions.FirstOrDefault(v => string.Equals(v.Version, previous, StringComparison.Ordinal))
            ?? Versions.FirstOrDefault(v => v.IsRecommended)
            ?? Versions.FirstOrDefault();
    }

    /// <summary>
    /// The types the default view hides.
    /// </summary>
    /// <remarks>
    /// EXCLUDING PRE-RELEASES, not requiring "release", and the difference is not academic. Against the
    /// live metadata server:
    ///
    ///     net.minecraft               snapshot=744  release=102  old_snapshot=75  old_alpha=35 ...
    ///     net.minecraftforge          ''=5028
    ///     net.neoforged               ''=1729
    ///     net.fabricmc.fabric-loader  release=251
    ///
    /// Forge and NeoForge publish an EMPTY type on every build. A filter that keeps only "release"
    /// therefore hides all 5,028 Forge versions, and "Add loader -> Forge" opens on an empty list that
    /// the user has to tick "show snapshots" to populate -- for a component that has no snapshots.
    ///
    /// My unit tests all passed: they were built from fixtures I wrote, and I had written type:
    /// "release" on every one of them. Only the probe against the real server showed it.
    /// </remarks>
    private static readonly string[] PrereleaseTypes =
        ["snapshot", "old_snapshot", "old_alpha", "old_beta", "experiment", "pending"];

    private static bool IsPrerelease(string type)
        => PrereleaseTypes.Contains(type, StringComparer.OrdinalIgnoreCase);

    private bool Matches(MetaVersion version)
    {
        if (!ShowAllVersions && IsPrerelease(version.Type))
        {
            return false;
        }

        if (SearchText is { Length: > 0 } search
            && version.VersionString.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        /*
         * LOADER VERSIONS ARE FILTERED BY WHAT THEY REQUIRE. Forge and NeoForge publish a build per
         * Minecraft version, so an unfiltered list offers hundreds of builds that cannot work with the
         * instance in front of you -- and picking one produces an instance that fails to resolve with
         * an error naming a version the user never chose.
         *
         * Fabric and Quilt are version-independent and declare no such requirement, so this filter
         * removes nothing for them, which is correct rather than accidental.
         */
        if (_minecraftVersion.Length != 0)
        {
            /*
             * Requirement is a STRUCT, so "not found" is a default with an empty uid rather than null.
             * A component that pins no Minecraft version -- Fabric and Quilt, which are
             * version-independent and say so by omission -- passes, which is correct rather than
             * accidental.
             */
            var pinned = version.Requires.FirstOrDefault(
                r => string.Equals(r.Uid, "net.minecraft", StringComparison.Ordinal));

            if (pinned.EqualsVersion is { Length: > 0 } required
                && !string.Equals(required, _minecraftVersion, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(CanAccept))]
    public void Accept()
    {
        if (Selected is { } chosen)
        {
            ChosenVersion = chosen.Version;
        }
    }

    partial void OnSelectedChanged(VersionViewModel? value)
    {
        OnPropertyChanged(nameof(CanAccept));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAccept));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowAllVersionsChanged(bool value) => Rebuild();

    partial void OnSearchTextChanged(string value) => Rebuild();
}
