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
 * THE FIRST UI TESTS IN THE PORT, and the reason the view model exists at all. Upstream's ordering
 * rules live in a QSortFilterProxyModel that cannot be constructed without a QApplication, so they are
 * only observable by looking at a window. Here they run in a test process with no display.
 *
 * The instances are real folders with real instance.cfg files, read through the ported InstanceList —
 * so this exercises the whole stack beneath it rather than a hand-built list of view models.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class InstanceListViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-vm-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public InstanceListViewModelTests()
    {
        _instances = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(_instances);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private void MakeInstance(string id, string name, long lastLaunch = 0)
    {
        var path = Path.Combine(_instances, id);
        Directory.CreateDirectory(path);

        File.WriteAllText(
            Path.Combine(path, "instance.cfg"),
            $"name={name}\nInstanceType=OneSix\nlastLaunchTime={lastLaunch}\n");
    }

    private InstanceList NewList()
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        return list;
    }

    private InstanceListViewModel Load(Action<InstanceList>? configure = null)
    {
        var list = NewList();

        configure?.Invoke(list);

        var viewModel = new InstanceListViewModel();
        viewModel.Load(list);

        return viewModel;
    }

    // ================================================================== grouping

    /*
     * The ungrouped heading is a group whose name is empty, which sorts first. Upstream's arrangement:
     * instances the user has not filed sit at the top rather than under a "Misc" heading invented for
     * them.
     */
    [Fact]
    public void UngroupedInstancesComeFirstUnderAnEmptyHeading()
    {
        MakeInstance("a", "Alpha");
        MakeInstance("b", "Beta");
        MakeInstance("c", "Gamma");

        var viewModel = Load(list =>
        {
            list.SetInstanceGroup("b", "Modded");
            list.SetInstanceGroup("c", "Modded");
        });

        Assert.Equal([string.Empty, "Modded"], viewModel.Groups.Select(g => g.Name));
        Assert.True(viewModel.Groups[0].IsUngrouped);
        Assert.Equal(["Alpha"], viewModel.Groups[0].Instances.Select(i => i.Name));
        Assert.Equal(["Beta", "Gamma"], viewModel.Groups[1].Instances.Select(i => i.Name));
    }

    [Fact]
    public void GroupsAreOrderedByName()
    {
        MakeInstance("a", "One");
        MakeInstance("b", "Two");
        MakeInstance("c", "Three");

        var viewModel = Load(list =>
        {
            list.SetInstanceGroup("a", "Zebra");
            list.SetInstanceGroup("b", "Apple");
            list.SetInstanceGroup("c", "Mango");
        });

        Assert.Equal(["Apple", "Mango", "Zebra"], viewModel.Groups.Select(g => g.Name));
    }

    [Fact]
    public void AnEmptyFolderProducesNoGroups()
    {
        var viewModel = Load();

        Assert.Empty(viewModel.Groups);

        // Empty because there is nothing, not because the search hid it.
        Assert.False(viewModel.IsFilteredToNothing);
    }

    // ================================================================== sorting

    /*
     * NATURAL ORDER: "Pack 2" before "Pack 10". This is what the whole StringUtils port exists for,
     * and it is the single most visible ordering rule in the launcher -- a user with ten numbered
     * packs sees it every time they open the window.
     */
    [Fact]
    public void InstancesSortNaturallyByName()
    {
        MakeInstance("a", "Pack 10");
        MakeInstance("b", "Pack 2");
        MakeInstance("c", "Pack 1");

        var viewModel = Load();

        Assert.Equal(["Pack 1", "Pack 2", "Pack 10"], viewModel.Groups[0].Instances.Select(i => i.Name));
    }

    [Fact]
    public void NameSortingIgnoresCase()
    {
        MakeInstance("a", "beta");
        MakeInstance("b", "Alpha");

        Assert.Equal(["Alpha", "beta"], Load().Groups[0].Instances.Select(i => i.Name));
    }

    /// <summary>Descending: the interesting end of "when did I last play this" is the recent one.</summary>
    [Fact]
    public void LastLaunchSortsMostRecentFirst()
    {
        MakeInstance("a", "Old", lastLaunch: 1_000);
        MakeInstance("b", "Newest", lastLaunch: 3_000);
        MakeInstance("c", "Middle", lastLaunch: 2_000);

        var viewModel = Load();
        viewModel.SortMode = InstanceSortMode.LastLaunch;

        Assert.Equal(["Newest", "Middle", "Old"], viewModel.Groups[0].Instances.Select(i => i.Name));
    }

    /// <summary>Never-played instances share a timestamp, so the name breaks the tie.</summary>
    [Fact]
    public void NeverPlayedInstancesFallBackToNameOrder()
    {
        MakeInstance("a", "Zulu");
        MakeInstance("b", "Alpha");

        var viewModel = Load();
        viewModel.SortMode = InstanceSortMode.LastLaunch;

        Assert.Equal(["Alpha", "Zulu"], viewModel.Groups[0].Instances.Select(i => i.Name));
    }

    [Fact]
    public void ChangingTheSortModeReordersImmediately()
    {
        MakeInstance("a", "Alpha", lastLaunch: 1_000);
        MakeInstance("b", "Zulu", lastLaunch: 9_000);

        var viewModel = Load();

        Assert.Equal("Alpha", viewModel.Groups[0].Instances[0].Name);

        viewModel.SortMode = InstanceSortMode.LastLaunch;

        Assert.Equal("Zulu", viewModel.Groups[0].Instances[0].Name);
    }

    // ================================================================== filtering

    [Fact]
    public void SearchingNarrowsToMatchingNames()
    {
        MakeInstance("a", "Skyblock");
        MakeInstance("b", "Sky Factory");
        MakeInstance("c", "Vanilla");

        var viewModel = Load();
        viewModel.SearchText = "sky";

        Assert.Equal(2, viewModel.Groups[0].Instances.Count);
        Assert.DoesNotContain(viewModel.Groups[0].Instances, i => i.Name == "Vanilla");
    }

    /*
     * ORDERING IS LOCALE-AWARE, NOT ORDINAL, and the difference is visible: a space is a
     * variable-weight character under CLDR collation, so "Sky Factory" sorts as though it were
     * "SkyFactory" and lands AFTER "Skyblock" -- where an ordinal comparison would put it first,
     * because U+0020 is below 'b'.
     *
     * That is faithful to Qt's localeAwareCompare, which is what upstream sorts with. I expected the
     * ordinal answer when writing this and the code was right; pinned here so the next person does not
     * "fix" it back.
     */
    [Fact]
    public void OrderingIgnoresSpacesTheWayLocaleAwareComparisonDoes()
    {
        MakeInstance("a", "Skyblock");
        MakeInstance("b", "Sky Factory");

        Assert.Equal(["Skyblock", "Sky Factory"], Load().Groups[0].Instances.Select(i => i.Name));
    }

    /// <summary>A group left with no matches disappears rather than showing an empty heading.</summary>
    [Fact]
    public void AGroupWithNoMatchesIsNotShown()
    {
        MakeInstance("a", "Skyblock");
        MakeInstance("b", "Vanilla");

        var viewModel = Load(list => list.SetInstanceGroup("b", "Boring"));
        viewModel.SearchText = "sky";

        Assert.Equal([string.Empty], viewModel.Groups.Select(g => g.Name));
    }

    /*
     * The NAME only. A user typing into a search box is naming what they want to play, not writing a
     * query -- matching the group as well would surface every instance in a group whose name happens
     * to contain the text.
     */
    [Fact]
    public void SearchingDoesNotMatchTheGroupName()
    {
        MakeInstance("a", "Vanilla");

        var viewModel = Load(list => list.SetInstanceGroup("a", "Skyblock Packs"));
        viewModel.SearchText = "sky";

        Assert.Empty(viewModel.Groups);
    }

    /// <summary>"Nothing here" and "nothing matched" are different things to tell a user.</summary>
    [Fact]
    public void AListFilteredToNothingSaysSo()
    {
        MakeInstance("a", "Vanilla");

        var viewModel = Load();
        viewModel.SearchText = "nothing matches this";

        Assert.Empty(viewModel.Groups);
        Assert.True(viewModel.IsFilteredToNothing);
    }

    [Fact]
    public void ClearingTheSearchRestoresEverything()
    {
        MakeInstance("a", "Skyblock");
        MakeInstance("b", "Vanilla");

        var viewModel = Load();
        viewModel.SearchText = "sky";
        viewModel.SearchText = string.Empty;

        Assert.Equal(2, viewModel.Groups[0].Instances.Count);
    }

    // ================================================================== selection and state

    [Fact]
    public void SelectingOneInstanceDeselectsTheRest()
    {
        MakeInstance("a", "Alpha");
        MakeInstance("b", "Beta");

        var viewModel = Load();

        viewModel.Select("a");
        Assert.Equal("a", viewModel.Selected!.Id);

        viewModel.Select("b");
        Assert.Equal("b", viewModel.Selected!.Id);
        Assert.False(viewModel.Find("a")!.IsSelected);
    }

    [Fact]
    public void SelectingNothingClearsTheSelection()
    {
        MakeInstance("a", "Alpha");

        var viewModel = Load();

        viewModel.Select("a");
        viewModel.Select(null);

        Assert.Null(viewModel.Selected);
    }

    /// <summary>A collapsed group stays collapsed when the search or sort changes.</summary>
    [Fact]
    public void CollapsedGroupsSurviveARebuild()
    {
        MakeInstance("a", "Alpha");
        MakeInstance("b", "Beta");

        var viewModel = Load(list => list.SetInstanceGroup("a", "Modded"));

        viewModel.Groups.Single(g => g.Name == "Modded").IsCollapsed = true;
        viewModel.SortMode = InstanceSortMode.LastLaunch;

        Assert.True(viewModel.Groups.Single(g => g.Name == "Modded").IsCollapsed);
        Assert.False(viewModel.Groups.Single(g => g.IsUngrouped).IsCollapsed);
    }

    /// <summary>An instance the launcher cannot start is still listed, and marked.</summary>
    [Fact]
    public void UnsupportedInstancesAreListedAndFlagged()
    {
        MakeInstance("good", "Good");

        var broken = Path.Combine(_instances, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "instance.cfg"), "name=Broken\nInstanceType=Nonsense\n");

        var viewModel = Load();

        Assert.Equal(2, viewModel.Groups[0].Instances.Count);
        Assert.False(viewModel.Groups[0].Instances.Single(i => i.Name == "Broken").IsSupported);
    }

    // ================================================================== the sort picker

    /*
     * The picker binds to these rather than to a hardcoded list in the XAML. That is how a control
     * ends up decorative: it looks right, selects nothing, and silently stops matching the model.
     */
    [Fact]
    public void TheSortPickerOffersEveryMode()
    {
        var viewModel = Load();

        Assert.Equal(
            Enum.GetValues<InstanceSortMode>(),
            viewModel.SortOptions.Select(o => o.Mode));

        Assert.All(viewModel.SortOptions, o => Assert.NotEqual(string.Empty, o.Label));
    }

    [Fact]
    public void ChoosingASortOptionAppliesIt()
    {
        MakeInstance("a", "Alpha", lastLaunch: 1_000);
        MakeInstance("b", "Zulu", lastLaunch: 9_000);

        var viewModel = Load();

        viewModel.SelectedSortOption = viewModel.SortOptions.Single(o => o.Mode == InstanceSortMode.LastLaunch);

        Assert.Equal("Zulu", viewModel.Groups[0].Instances[0].Name);
    }

    /// <summary>And the other way, so setting the mode in code moves the picker too.</summary>
    [Fact]
    public void SettingTheModeUpdatesThePicker()
    {
        var viewModel = Load();

        viewModel.SortMode = InstanceSortMode.LastLaunch;

        Assert.Equal(InstanceSortMode.LastLaunch, viewModel.SelectedSortOption.Mode);
    }
}
