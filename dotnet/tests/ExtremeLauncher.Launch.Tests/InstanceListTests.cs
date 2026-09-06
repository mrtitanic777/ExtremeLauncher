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
 * Characterization tests for instance discovery and grouping. Upstream has no Qt test for
 * InstanceList.
 *
 * instgroups.json is a file every existing install already has, so its shape is a compatibility
 * surface. The tests below assert the literal JSON, not just a round trip.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Settings;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class InstanceListTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-list-" + Guid.NewGuid().ToString("N"));

    public InstanceListTests() => Directory.CreateDirectory(_temp);

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

    private string InstancesDir => Path.Combine(_temp, "instances");

    private InstanceList NewList()
        => new(InstancesDir, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

    /// <summary>Creates an instance directory the way the launcher would.</summary>
    private string MakeInstance(string id, string name = "", string instanceType = "OneSix")
    {
        var root = Path.Combine(InstancesDir, id);
        Directory.CreateDirectory(root);

        var config = new IniFile();

        if (name.Length != 0)
        {
            config.Set("name", name);
        }

        if (instanceType.Length != 0)
        {
            config.Set("InstanceType", instanceType);
        }

        config.SaveFile(Path.Combine(root, "instance.cfg"));

        return root;
    }

    private string GroupFile => Path.Combine(InstancesDir, "instgroups.json");

    // ================================================================== discovery

    [Fact]
    public void ADirectoryWithAnInstanceConfigIsAnInstance()
    {
        MakeInstance("1.20.1", "Vanilla");
        MakeInstance("modded", "My Modpack");

        var list = NewList();
        list.LoadList();

        Assert.Equal(2, list.Count);
        Assert.Equal("My Modpack", list.GetInstanceById("modded")!.Name);
    }

    [Fact]
    public void ADirectoryWithoutOneIsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(InstancesDir, "not-an-instance"));
        MakeInstance("real");

        var list = NewList();
        list.LoadList();

        Assert.Single(list.Instances);
    }

    [Fact]
    public void AHalfCreatedInstanceIsStillDiscovered()
    {
        // No mmc-pack.json: the instance is unusable, but hiding it would leave the user unable to see
        // or delete it.
        MakeInstance("broken");

        var list = NewList();
        list.LoadList();

        Assert.Single(list.Instances);
    }

    [Fact]
    public void TheFolderNameIsTheId()
    {
        MakeInstance("stable-id", "A Name The User Changed");

        var list = NewList();
        list.LoadList();

        var record = Assert.Single(list.Instances);

        // Renaming an instance in the UI must not rename its folder: the id has to stay stable or the
        // group file and every reference to it break.
        Assert.Equal("stable-id", record.Id);
        Assert.Equal("A Name The User Changed", record.Name);
    }

    [Fact]
    public void AMissingInstancesDirectoryIsEmptyRatherThanAnError()
    {
        var list = NewList();
        list.LoadList();

        Assert.Empty(list.Instances);
    }

    [Fact]
    public void AnInstanceWithNoTypeIsTreatedAsOneSix()
    {
        // Some launcher versions did not write InstanceType at all; refusing those would hide
        // instances that work perfectly well.
        MakeInstance("untyped", "Old Instance", instanceType: string.Empty);

        var list = NewList();
        list.LoadList();

        Assert.True(Assert.Single(list.Instances).IsSupported);
    }

    [Fact]
    public void AnInstanceOfAnUnknownTypeIsListedButNotSupported()
    {
        MakeInstance("weird", "From Another Launcher", instanceType: "SomethingElse");

        var list = NewList();
        list.LoadList();

        var record = Assert.Single(list.Instances);

        // Listed so the user can see and remove it; flagged so nothing tries to launch it.
        Assert.False(record.IsSupported);
    }

    // ================================================================== groups

    [Fact]
    public void AnInstanceStartsUngrouped()
    {
        MakeInstance("a");

        var list = NewList();
        list.LoadList();

        Assert.Equal(string.Empty, list.GetInstanceGroup("a"));
        Assert.Empty(list.GetGroups());
    }

    [Fact]
    public void GroupingSurvivesAReload()
    {
        MakeInstance("a");
        MakeInstance("b");

        var list = NewList();
        list.LoadList();

        list.SetInstanceGroup("a", "Modded");
        list.SetInstanceGroup("b", "Modded");

        var reopened = NewList();
        reopened.LoadList();

        Assert.Equal("Modded", reopened.GetInstanceGroup("a"));
        Assert.Equal(["Modded"], reopened.GetGroups());
    }

    [Fact]
    public void AGroupIsNothingButTheInstancesNamingIt()
    {
        MakeInstance("a");

        var list = NewList();
        list.LoadList();

        list.SetInstanceGroup("a", "Temporary");
        Assert.Equal(["Temporary"], list.GetGroups());

        // Derived rather than stored, so a group whose last instance leaves stops existing.
        list.SetInstanceGroup("a", string.Empty);
        Assert.Empty(list.GetGroups());
    }

    [Fact]
    public void RenamingAGroupMovesEveryInstanceInIt()
    {
        MakeInstance("a");
        MakeInstance("b");

        var list = NewList();
        list.LoadList();

        list.SetInstanceGroup("a", "Old Name");
        list.SetInstanceGroup("b", "Old Name");
        list.SetGroupCollapsed("Old Name", true);

        list.RenameGroup("Old Name", "New Name");

        Assert.Equal("New Name", list.GetInstanceGroup("a"));
        Assert.Equal("New Name", list.GetInstanceGroup("b"));

        // The collapsed state follows the rename, or the group would silently spring open.
        Assert.True(list.IsGroupCollapsed("New Name"));
        Assert.False(list.IsGroupCollapsed("Old Name"));
    }

    [Fact]
    public void DeletingAGroupLeavesItsInstancesAlone()
    {
        MakeInstance("a");

        var list = NewList();
        list.LoadList();

        list.SetInstanceGroup("a", "Doomed");
        list.DeleteGroup("Doomed");

        // The group goes; the instance does not.
        Assert.Equal(string.Empty, list.GetInstanceGroup("a"));
        Assert.Single(list.Instances);
    }

    // ================================================================== the group file

    [Fact]
    public void TheGroupFileIsKeyedByGroupNotByInstance()
    {
        MakeInstance("a");
        MakeInstance("b");

        var list = NewList();
        list.LoadList();

        list.SetInstanceGroup("a", "Modded");
        list.SetInstanceGroup("b", "Modded");

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(GroupFile))!;
        var groups = (JsonObject)root["groups"]!;

        var instances = (JsonArray)((JsonObject)groups["Modded"]!)["instances"]!;

        Assert.Equal(2, instances.Count);
    }

    [Fact]
    public void TheFormatVersionIsWrittenAsAString()
    {
        MakeInstance("a");

        var list = NewList();
        list.LoadList();
        list.SetInstanceGroup("a", "Modded");

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(GroupFile))!;

        // INHERITED: a launcher that wrote a number here would be refused by every existing version.
        Assert.Equal("1", (string?)root["formatVersion"]);
    }

    [Fact]
    public void ACollapsedUngroupedSectionGetsItsOwnKey()
    {
        MakeInstance("a");

        var list = NewList();
        list.LoadList();
        list.SetGroupCollapsed(string.Empty, true);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(GroupFile))!;

        Assert.NotNull(root["ungrouped"]);

        var reopened = NewList();
        reopened.LoadList();

        Assert.True(reopened.IsGroupCollapsed(string.Empty));
    }

    [Fact]
    public void AnInstanceNoLongerOnDiskLosesItsGroupEntry()
    {
        MakeInstance("a");
        MakeInstance("gone");

        var list = NewList();
        list.LoadList();

        list.SetInstanceGroup("a", "Modded");
        list.SetInstanceGroup("gone", "Modded");

        // Deleted outside the launcher.
        Directory.Delete(Path.Combine(InstancesDir, "gone"), recursive: true);

        var reopened = NewList();
        reopened.LoadList();
        reopened.SaveGroupList();

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(GroupFile))!;
        var instances = (JsonArray)((JsonObject)((JsonObject)root["groups"]!)["Modded"]!)["instances"]!;

        // An instance removed by hand should not keep its group forever.
        Assert.Single(instances);
    }

    [Fact]
    public void SavingIsRefusedBeforeDiscoveryHasRun()
    {
        MakeInstance("a");

        var list = NewList();
        list.LoadList();
        list.SetInstanceGroup("a", "Modded");

        // A fresh list has not probed yet. Writing now would persist an EMPTY picture and erase every
        // user's grouping.
        var unprobed = NewList();
        unprobed.SaveGroupList();

        var reopened = NewList();
        reopened.LoadList();

        Assert.Equal("Modded", reopened.GetInstanceGroup("a"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"formatVersion\": \"999\", \"groups\": {} }")]
    [InlineData("{ \"formatVersion\": \"1\" }")]
    [InlineData("{ \"formatVersion\": \"1\", \"groups\": \"not an object\" }")]
    public void AnUnusableGroupFileLeavesEverythingUngrouped(string contents)
    {
        MakeInstance("a");
        Directory.CreateDirectory(InstancesDir);
        File.WriteAllText(GroupFile, contents);

        var list = NewList();
        list.LoadList();

        // A user seeing an ungrouped list can regroup; an error they cannot act on helps nobody.
        Assert.Single(list.Instances);
        Assert.Equal(string.Empty, list.GetInstanceGroup("a"));
    }

    [Fact]
    public void AMalformedGroupIsSkippedAndTheRestStillLoad()
    {
        MakeInstance("a");
        MakeInstance("b");
        Directory.CreateDirectory(InstancesDir);

        File.WriteAllText(GroupFile, """
            {
                "formatVersion": "1",
                "groups": {
                    "Broken": { "hidden": false },
                    "Good": { "hidden": false, "instances": [ "b" ] }
                }
            }
            """);

        var list = NewList();
        list.LoadList();

        Assert.Equal(string.Empty, list.GetInstanceGroup("a"));
        Assert.Equal("Good", list.GetInstanceGroup("b"));
    }

    [Fact]
    public void AnEmptyGroupNameInTheFileIsIgnored()
    {
        MakeInstance("a");
        Directory.CreateDirectory(InstancesDir);

        File.WriteAllText(GroupFile, """
            { "formatVersion": "1", "groups": { "": { "hidden": false, "instances": [ "a" ] } } }
            """);

        var list = NewList();
        list.LoadList();

        // The ungrouped pseudo-group has its own key; an empty name in "groups" is redundant.
        Assert.Equal(string.Empty, list.GetInstanceGroup("a"));
    }
}
