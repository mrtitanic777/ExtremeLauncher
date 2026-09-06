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
 * Index cases ported from tests/Index_test.cpp; the rest are characterization tests for the JSON
 * format and merge semantics, which have no upstream test.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class IndexTests
{
    private static Index ThreeLists()
        => new([new VersionList("list1"), new VersionList("list2"), new VersionList("list3")]);

    [Fact]
    public void HasUidAndGet()
    {
        var index = ThreeLists();

        Assert.True(index.HasUid("list1"));
        Assert.False(index.HasUid("asdf"));

        Assert.NotNull(index.Get("list2"));
        Assert.Equal("list2", index.Get("list2").Uid);

        // Upstream pins this: an unknown uid still yields a list rather than null.
        Assert.NotNull(index.Get("adsf"));
    }

    [Fact]
    public void GetOnAnUnknownUidRegistersIt()
    {
        var index = ThreeLists();

        Assert.Equal(3, index.Lists.Count);

        index.Get("brand-new");

        Assert.Equal(4, index.Lists.Count);
        Assert.True(index.HasUid("brand-new"));
    }

    [Fact]
    public void Merge()
    {
        var index = ThreeLists();
        Assert.Equal(3, index.Lists.Count);

        // Merging the same uids changes nothing.
        index.Merge(ThreeLists());
        Assert.Equal(3, index.Lists.Count);

        // Two new uids among three.
        index.Merge(new Index([new VersionList("list4"), new VersionList("list2"), new VersionList("list5")]));
        Assert.Equal(5, index.Lists.Count);

        index.Merge(new Index([new VersionList("list6")]));
        Assert.Equal(6, index.Lists.Count);
    }

    [Fact]
    public void MergingIntoAnEmptyIndexTakesTheOtherSideWholesale()
    {
        var index = new Index();

        index.Merge(ThreeLists());

        Assert.Equal(3, index.Lists.Count);
        Assert.True(index.HasUid("list2"));
    }

    [Fact]
    public void MergingAnIndexEntryDoesNotWipeLoadedVersions()
    {
        var index = new Index();

        var loaded = new VersionList("net.minecraft") { Name = "Minecraft" };
        loaded.SetVersions([new MetaVersion("net.minecraft", "1.20.1") { Type = "release", RawTime = 100 }]);
        index.Merge(new Index([loaded]));

        // A later index refresh knows the name and hash but no versions. It must not clear them.
        index.Merge(new Index([new VersionList("net.minecraft") { Name = "Minecraft", Sha256 = "abc" }]));

        Assert.Single(index.Get("net.minecraft").Versions);
        Assert.Equal("abc", index.Get("net.minecraft").Sha256);
    }
}

public sealed class VersionListTests
{
    private static MetaVersion V(string version, string type, long time)
        => new("net.minecraft", version) { Type = type, RawTime = time };

    [Fact]
    public void SetVersionsSortsNewestFirst()
    {
        var list = new VersionList("net.minecraft");
        list.SetVersions([V("old", "release", 100), V("newest", "release", 300), V("middle", "release", 200)]);

        Assert.Equal(["newest", "middle", "old"], list.Versions.Select(v => v.VersionString));
    }

    [Fact]
    public void RecommendedIsTheNewestRelease()
    {
        var list = new VersionList("net.minecraft");
        list.SetVersions([V("snap", "snapshot", 400), V("rel", "release", 300), V("older-rel", "release", 100)]);

        // QUIRK: it is the first entry of type "release" after the newest-first sort, not whatever
        // carries the `recommended` flag.
        Assert.Equal("rel", list.Recommended?.VersionString);
    }

    [Fact]
    public void LookupByVersionString()
    {
        var list = new VersionList("net.minecraft");
        list.SetVersions([V("1.20.1", "release", 100)]);

        Assert.True(list.HasVersion("1.20.1"));
        Assert.Null(list.GetVersion("9.9.9"));
    }

    [Fact]
    public void MergeFromIndexLeavesVersionsAlone()
    {
        var list = new VersionList("net.minecraft") { Name = "Old Name" };
        list.SetVersions([V("1.20.1", "release", 100)]);

        list.MergeFromIndex(new VersionList("net.minecraft") { Name = "Minecraft", Sha256 = "deadbeef" });

        Assert.Equal("Minecraft", list.Name);
        Assert.Equal("deadbeef", list.Sha256);
        Assert.Single(list.Versions);
    }

    [Fact]
    public void MergeAddsAndFoldsVersions()
    {
        var list = new VersionList("net.minecraft");
        list.SetVersions([V("1.20.1", "release", 100)]);

        var incoming = new VersionList("net.minecraft");
        incoming.SetVersions([V("1.20.1", "release", 150), V("1.20.2", "release", 200)]);

        list.Merge(incoming);

        Assert.Equal(2, list.Versions.Count);

        // The existing entry was folded into, not replaced.
        Assert.Equal(150, list.GetVersion("1.20.1")!.RawTime);
    }

    [Fact]
    public void ReleaseBeatsSnapshotWhenPickingRecommended()
    {
        Assert.Equal("rel", VersionList.GetBetterVersion(V("snap", "snapshot", 999), V("rel", "release", 1))?.VersionString);

        // Within one type, newer wins.
        Assert.Equal("new", VersionList.GetBetterVersion(V("old", "release", 1), V("new", "release", 2))?.VersionString);
    }
}

public sealed class MetaJsonFormatTests
{
    private static System.Text.Json.Nodes.JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(json));

    [Fact]
    public void ParsesAnIndexDocument()
    {
        var index = new Index();

        MetaJsonFormat.ParseIndex(Parse("""
            {
              "formatVersion": 1,
              "packages": [
                { "uid": "net.minecraft", "name": "Minecraft", "sha256": "aaa" },
                { "uid": "net.fabricmc.fabric-loader", "name": "Fabric Loader", "sha256": "bbb" }
              ]
            }
            """), index);

        Assert.Equal(2, index.Lists.Count);
        Assert.Equal("Minecraft", index.Get("net.minecraft").Name);
        Assert.Equal("bbb", index.Get("net.fabricmc.fabric-loader").Sha256);
    }

    [Fact]
    public void ParsesAVersionListDocument()
    {
        var list = new VersionList("net.minecraft");

        MetaJsonFormat.ParseVersionList(Parse("""
            {
              "formatVersion": 1,
              "uid": "net.minecraft",
              "name": "Minecraft",
              "versions": [
                { "version": "1.20.2", "type": "release", "releaseTime": "2023-09-21T12:00:00+00:00" },
                { "version": "23w31a", "type": "snapshot", "releaseTime": "2023-08-02T12:00:00+00:00" }
              ]
            }
            """), list);

        Assert.Equal("Minecraft", list.Name);
        Assert.Equal(2, list.Versions.Count);

        // Newest first.
        Assert.Equal("1.20.2", list.Versions[0].VersionString);
        Assert.Equal("1.20.2", list.Recommended?.VersionString);

        // Entries from a list are entitled to speak about recommendations.
        Assert.True(list.Versions[0].ProvidesRecommendations);
    }

    [Fact]
    public void ParsesRequiresAndConflicts()
    {
        var list = new VersionList("net.fabricmc.fabric-loader");

        MetaJsonFormat.ParseVersionList(Parse("""
            {
              "formatVersion": 1,
              "uid": "net.fabricmc.fabric-loader",
              "versions": [
                {
                  "version": "0.14.21",
                  "type": "release",
                  "releaseTime": "2023-06-01T12:00:00+00:00",
                  "requires": [ { "uid": "net.minecraft", "equals": "1.20.1" } ],
                  "conflicts": [ { "uid": "net.minecraftforge" } ]
                }
              ]
            }
            """), list);

        var version = list.GetVersion("0.14.21");

        Assert.NotNull(version);
        Assert.Single(version.Requires);
        Assert.Equal("net.minecraft", version.Requires.First().Uid);
        Assert.Equal("1.20.1", version.Requires.First().EqualsVersion);
        Assert.Single(version.Conflicts);
    }

    [Fact]
    public void ReleaseTimeBecomesUnixSeconds()
    {
        var list = new VersionList("x");

        MetaJsonFormat.ParseVersionList(Parse("""
            {
              "formatVersion": 1,
              "uid": "x",
              "versions": [ { "version": "1", "type": "release", "releaseTime": "2023-09-21T12:00:00+00:00" } ]
            }
            """), list);

        Assert.Equal(
            new DateTimeOffset(2023, 9, 21, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            list.GetVersion("1")!.RawTime);
    }

    [Theory]
    [InlineData("""{ "formatVersion": 0 }""", MetadataVersion.InitialRelease)]
    [InlineData("""{ "formatVersion": 1 }""", MetadataVersion.InitialRelease)]
    [InlineData("""{ "formatVersion": 2 }""", MetadataVersion.Invalid)]
    [InlineData("""{ "formatVersion": "one" }""", MetadataVersion.Invalid)]
    [InlineData("""{ }""", MetadataVersion.Invalid)]
    public void FormatVersionIsValidatedStrictly(string json, MetadataVersion expected)
        => Assert.Equal(expected, MetaJsonFormat.ParseFormatVersion(Parse(json)));

    [Fact]
    public void AMissingFormatVersionIsToleratedOnlyWhenOptional()
    {
        Assert.Equal(MetadataVersion.InitialRelease, MetaJsonFormat.ParseFormatVersion(Parse("{ }"), required: false));
        Assert.Equal(MetadataVersion.Invalid, MetaJsonFormat.ParseFormatVersion(Parse("{ }")));
    }

    [Fact]
    public void AnUnknownFormatVersionIsRejectedRatherThanGuessedAt()
    {
        // Metadata decides what gets downloaded and run, so a best-effort parse would be unsafe.
        Assert.Throws<ParseException>(
            () => MetaJsonFormat.ParseIndex(Parse("""{ "formatVersion": 99, "packages": [] }"""), new Index()));
    }

    [Fact]
    public void RequiresRoundTripThroughSerialization()
    {
        var requires = new RequireSet
        {
            new Require("net.minecraft", "1.20.1"),
            new Require("org.example", suggests: "2.0"),
        };

        var obj = new System.Text.Json.Nodes.JsonObject();
        MetaJsonFormat.SerializeRequires(obj, requires, "requires");

        var parsed = MetaJsonFormat.ParseRequires(obj, "requires");

        Assert.Equal(2, parsed.Count);
        Assert.Contains(parsed, r => r.Uid == "net.minecraft" && r.EqualsVersion == "1.20.1");
        Assert.Contains(parsed, r => r.Uid == "org.example" && r.Suggests == "2.0");
    }

    [Fact]
    public void RequirementsAreKeyedByUidAlone()
    {
        var set = new RequireSet
        {
            new Require("net.minecraft", "1.20.1"),
            new Require("net.minecraft", "1.19.4"),
        };

        // QUIRK preserved: the second is dropped, because equality ignores the version constraint.
        Assert.Single(set);
        Assert.Equal("1.20.1", set.First().EqualsVersion);

        // DeepEquals is the comparison that does look at it.
        Assert.False(new Require("a", "1").DeepEquals(new Require("a", "2")));
        Assert.True(new Require("a", "1").DeepEquals(new Require("a", "1")));
    }
}
