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
 * Ported from the sorting and grouping rules in launcher/ui/instanceview/InstanceProxyModel.cpp.
 *
 * THE LAUNCHER'S MAIN SCREEN, and the first piece of wave 10.
 *
 * WHY THERE IS A VIEW MODEL AT ALL. Upstream expresses this as a QSortFilterProxyModel: the ordering
 * rules live inside a Qt class that cannot be constructed without a QApplication, so they are only
 * observable by looking at a window. Everything decidable about this screen -- which instances are
 * shown, in what order, under which headings -- is moved into a class with no toolkit dependency, so
 * it can be tested in a process with no display. That is not a stylistic preference: it is the only
 * way any of this gets tested on the machine this port has been written on.
 *
 * The view that follows is expected to be thin enough to be checked by looking at it.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

/// <summary>How instances are ordered within their group.</summary>
public enum InstanceSortMode
{
    /// <summary>By name, naturally: "Pack 2" before "Pack 10".</summary>
    Name,

    /// <summary>Most recently played first.</summary>
    LastLaunch,
}

/// <summary>One instance as the list shows it.</summary>
public sealed partial class InstanceItemViewModel : ObservableObject
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Group { get; init; } = string.Empty;

    public string IconKey { get; init; } = "default";

    /// <summary>
    /// The icon this instance resolves to, or null in a build with no icon list.
    /// </summary>
    /// <remarks>
    /// An IconEntry rather than a bitmap, because the view models must stay free of Avalonia types.
    /// The tile turns it into an image through a converter.
    /// </remarks>
    public IconEntry? Icon { get; init; }

    /// <summary>When it was last played. Zero for never.</summary>
    public DateTimeOffset LastLaunch { get; init; }

    /// <summary>Whether the launcher understands this instance well enough to start it.</summary>
    public bool IsSupported { get; init; } = true;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A heading with the instances under it.</summary>
public sealed partial class InstanceGroupViewModel : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>The ungrouped heading, which has no name.</summary>
    public bool IsUngrouped => Name.Length == 0;

    public ObservableCollection<InstanceItemViewModel> Instances { get; } = [];

    [ObservableProperty]
    private bool _isCollapsed;
}

public sealed partial class InstanceListViewModel : ObservableObject
{
    private readonly List<InstanceItemViewModel> _all = [];

    private IconList? _icons;

    /// <summary>
    /// Where instance icons are resolved from.
    /// </summary>
    /// <remarks>
    /// Settable rather than a constructor argument because the list is built before the data
    /// directory is known in some paths, and a tile with no icon is a cosmetic gap rather than a
    /// failure -- so it must not be a hard dependency.
    /// </remarks>
    public IconList? Icons
    {
        get => _icons;
        set => _icons = value;
    }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private InstanceSortMode _sortMode = InstanceSortMode.Name;

    /// <summary>One entry of the sort-order picker.</summary>
    /// <param name="Label">What the picker shows.</param>
    public sealed record SortOption(string Label, InstanceSortMode Mode);

    /// <summary>
    /// The orderings offered, for a picker to bind to.
    /// </summary>
    /// <remarks>
    /// Exposed as data rather than written into the view. A hardcoded list of items in the XAML is how
    /// a picker ends up decorative: it looks right, selects nothing, and the list never reorders.
    /// </remarks>
    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("Sort by name", InstanceSortMode.Name),
        new("Sort by last played", InstanceSortMode.LastLaunch),
    ];

    /// <summary>The picker's selection, which is the sort mode by another name.</summary>
    public SortOption SelectedSortOption
    {
        get => SortOptions.First(o => o.Mode == SortMode);
        set => SortMode = value.Mode;
    }

    /// <summary>The groups, in display order, each holding its instances in display order.</summary>
    public ObservableCollection<InstanceGroupViewModel> Groups { get; } = [];

    /// <summary>Whether the list is empty because of the search rather than because it is empty.</summary>
    public bool IsFilteredToNothing => _all.Count != 0 && Groups.Count == 0;

    /// <summary>Whether there are no instances at all — a fresh install, not a search that missed.
    /// Drives the welcome placeholder, so a new user does not stare at an empty void.</summary>
    public bool IsEmpty => _all.Count == 0;

    /// <summary>The list this was loaded from, so it can be re-read after something changes it.</summary>
    public InstanceList? Source { get; private set; }

    /// <summary>Reads the instances and their groups from the list on disk.</summary>
    public void Load(InstanceList instances)
    {
        ArgumentNullException.ThrowIfNull(instances);

        Source = instances;

        // Remembered across the reload: an instance deleted from under the selection should clear it,
        // but a reload for any other reason should not move it.
        var selected = Selected?.Id;

        _all.Clear();

        foreach (var instance in instances.Instances)
        {
            _all.Add(new InstanceItemViewModel
            {
                Id = instance.Id,
                Name = instance.Name,
                Group = instances.GetInstanceGroup(instance.Id),
                IsSupported = instance.IsSupported,
                LastLaunch = DateTimeOffset.FromUnixTimeMilliseconds(instance.Settings.LastLaunchTime),

                /*
                 * Read from instance.cfg, and RESOLVED here rather than in the tile. Every instance
                 * has carried an iconKey since the first wave and nothing has ever read one back, so
                 * every instance in this launcher has looked identical.
                 */
                IconKey = instance.Settings.IconKey,
                Icon = _icons?.Resolve(instance.Settings.IconKey),
            });
        }

        if (selected is not null)
        {
            Select(selected);
        }

        Rebuild(instances);
    }

    /// <summary>Re-reads whatever this was loaded from.</summary>
    public void Reload()
    {
        if (Source is { } source)
        {
            Load(source);
        }
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnSortModeChanged(InstanceSortMode value)
    {
        Rebuild();

        // Kept in step whichever end changed it, so setting the mode in code moves the picker too.
        OnPropertyChanged(nameof(SelectedSortOption));
    }

    /// <summary>
    /// Rebuilds the grouped, sorted, filtered view.
    /// </summary>
    /// <remarks>
    /// A full rebuild rather than an incremental update, and that is a considered choice: a launcher
    /// has tens of instances, not thousands, so the cost is nothing — while an incremental path would
    /// have to get insertion order right under every combination of filter, sort and group change,
    /// which is where list-model bugs live.
    /// </remarks>
    private void Rebuild(InstanceList? instances = null)
    {
        var collapsed = instances is null
            ? Groups.Where(g => g.IsCollapsed).Select(g => g.Name).ToHashSet(StringComparer.Ordinal)
            : Groups.Select(g => g.Name).Where(instances.IsGroupCollapsed).ToHashSet(StringComparer.Ordinal);

        Groups.Clear();

        var matching = _all.Where(Matches);

        /*
         * GROUPS SORT BY NAME, and the ungrouped heading is a group whose name is empty -- which puts
         * it first under an ordinal comparison. That is upstream's arrangement: instances the user has
         * not filed sit at the top rather than under a "Misc" heading invented for them.
         */
        foreach (var group in matching.GroupBy(i => i.Group).OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var viewModel = new InstanceGroupViewModel
            {
                Name = group.Key,
                IsCollapsed = collapsed.Contains(group.Key),
            };

            foreach (var instance in Sort(group))
            {
                viewModel.Instances.Add(instance);
            }

            Groups.Add(viewModel);
        }

        OnPropertyChanged(nameof(IsFilteredToNothing));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <remarks>
    /// NATURAL ORDER for names, so "Pack 2" comes before "Pack 10" — the comparison the whole
    /// StringUtils port exists for. Last-launch order is DESCENDING, because the interesting end of
    /// "when did I last play this" is the recent one.
    /// </remarks>
    private IEnumerable<InstanceItemViewModel> Sort(IEnumerable<InstanceItemViewModel> instances)
        => SortMode == InstanceSortMode.LastLaunch
            ? instances.OrderByDescending(i => i.LastLaunch).ThenBy(i => i.Name, NaturalComparer.Instance)
            : instances.OrderBy(i => i.Name, NaturalComparer.Instance);

    /// <remarks>
    /// Case-insensitive substring, on the NAME only. A user typing into a search box is naming what
    /// they want to play, not writing a query — matching the group as well would surface every
    /// instance in a group whose name happens to contain the text.
    /// </remarks>
    private bool Matches(InstanceItemViewModel instance)
        => SearchText.Length == 0
            || instance.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The instance with this id, or null.</summary>
    public InstanceItemViewModel? Find(string id) => _all.Find(i => i.Id == id);

    /// <summary>Selects one instance and deselects the rest.</summary>
    public void Select(string? id)
    {
        foreach (var instance in _all)
        {
            instance.IsSelected = instance.Id == id;
        }
    }

    /// <summary>The selected instance, or null.</summary>
    public InstanceItemViewModel? Selected => _all.Find(i => i.IsSelected);
}

/// <summary>Orders strings the way a person reads them, via the ported natural comparison.</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y) => StringUtils.NaturalCompare(x ?? string.Empty, y ?? string.Empty, CaseSensitivity.CaseInsensitive);
}
