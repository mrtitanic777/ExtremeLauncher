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
 * What version a newly added dependency gets.
 *
 * FOUND BY LAUNCHING A REAL FABRIC INSTANCE. The resolution pass correctly worked out that Fabric
 * needs net.fabricmc.intermediary and added it with an EMPTY version, because Fabric's requirement
 * names no version at all:
 *
 *     "requires": [ { "uid": "net.fabricmc.intermediary" } ]
 *
 * The launch then asked the metadata server for `net.fabricmc.intermediary/.json` -- note the missing
 * version -- and took a 404. No Fabric or Quilt instance could start.
 *
 * Upstream answers this with a small hardcoded table under a banner reading "HACK HACK HACK HACK
 * FIXME". These tests pin that table, because it is the behaviour, hack or not.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class DependencyVersionTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-depv-" + Guid.NewGuid().ToString("N"));

    public DependencyVersionTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    /// <summary>A cached meta version, optionally declaring what it requires.</summary>
    private static MetaVersion Version(string uid, string version, params Require[] requires)
    {
        var meta = new MetaVersion(uid, version)
        {
            Type = "release",
            RawTime = 1_700_000_000,
            Status = MetaEntity.LoadStatus.Remote,
        };

        meta.Data = new VersionFile { Uid = uid, Version = version, Name = uid };

        if (requires.Length != 0)
        {
            meta.SetRequires([.. requires], []);

            // The version FILE carries them too: that is what the update task reads back after a
            // component reloads, and the meta entity's copy alone is not enough.
            foreach (var require in requires)
            {
                meta.Data.Requires.Add(require);
            }
        }

        return meta;
    }

    private static Index IndexWith(params MetaVersion[] versions)
        => new(versions
            .GroupBy(v => v.Uid, StringComparer.Ordinal)
            .Select(group =>
            {
                var list = new VersionList(group.Key);

                list.SetVersions(group);

                return list;
            }));

    private static PackProfile ProfileWith(params Component[] components)
    {
        var profile = new PackProfile(Context());

        foreach (var component in components)
        {
            profile.AppendComponent(component);
        }

        return profile;
    }

    private ComponentUpdateTask Resolve(PackProfile profile, Index index)
        => new(profile, index, _temp, ComponentUpdateMode.Resolution, NetMode.Online, (_, _, _) => Task.FromResult(true));

    [Fact]
    public async Task ADependencyWithNoVersionAtAllStillGetsOne()
    {
        /*
         * THE BUG, in miniature. Fabric requires intermediary and says nothing about which one; a
         * component with an empty version produces a request for "<uid>/.json" and a 404.
         */
        var index = IndexWith(
            Version("net.minecraft", "1.20.1"),
            Version("net.fabricmc.fabric-loader", "0.15.7", new Require("net.fabricmc.intermediary")),
            Version("net.fabricmc.intermediary", "1.20.1"));

        var profile = ProfileWith(
            new Component("net.minecraft") { Version = "1.20.1", IsImportant = true },
            new Component("net.fabricmc.fabric-loader") { Version = "0.15.7" });

        Assert.True(await Resolve(profile, index).RunAsync());

        var intermediary = profile.GetComponent("net.fabricmc.intermediary");

        Assert.NotNull(intermediary);
        Assert.NotEqual(string.Empty, intermediary.Version);

        // Mappings are published one per Minecraft version and named after it, so the instance's
        // Minecraft version IS the answer.
        Assert.Equal("1.20.1", intermediary.Version);
        Assert.True(intermediary.IsDependencyOnly);
    }

    [Fact]
    public async Task QuiltsMappingsGetTheSameTreatment()
    {
        // org.quiltmc.hashed is the same shape of thing under a different name, and upstream's table
        // lists them together.
        var index = IndexWith(
            Version("net.minecraft", "1.20.1"),
            Version("org.quiltmc.quilt-loader", "0.23.1", new Require("org.quiltmc.hashed")),
            Version("org.quiltmc.hashed", "1.20.1"));

        var profile = ProfileWith(
            new Component("net.minecraft") { Version = "1.20.1", IsImportant = true },
            new Component("org.quiltmc.quilt-loader") { Version = "0.23.1" });

        Assert.True(await Resolve(profile, index).RunAsync());

        Assert.Equal("1.20.1", profile.GetComponent("org.quiltmc.hashed")?.Version);
    }

    [Fact]
    public async Task ModernQuiltRequiresFabricsIntermediaryAndStillGetsTheVersion()
    {
        /*
         * WHAT MODERN QUILT ACTUALLY DOES. quilt-loader stopped shipping org.quiltmc.hashed and now
         * requires net.fabricmc.intermediary directly -- confirmed against the live meta server,
         * where org.quiltmc.hashed 404s and quilt-loader 0.26.3's requires lists
         * net.fabricmc.intermediary with no version. The same empty-version bug the Fabric case had
         * would strand it, and the same table entry saves it: intermediary gets the Minecraft version.
         *
         * The old-hashed test above covers the legacy path; this covers the one a Quilt instance
         * created today takes.
         */
        var index = IndexWith(
            Version("net.minecraft", "1.20.1"),
            Version("org.quiltmc.quilt-loader", "0.26.3", new Require("net.fabricmc.intermediary")),
            Version("net.fabricmc.intermediary", "1.20.1"));

        var profile = ProfileWith(
            new Component("net.minecraft") { Version = "1.20.1", IsImportant = true },
            new Component("org.quiltmc.quilt-loader") { Version = "0.26.3" });

        Assert.True(await Resolve(profile, index).RunAsync());

        var intermediary = profile.GetComponent("net.fabricmc.intermediary");

        Assert.NotNull(intermediary);
        Assert.Equal("1.20.1", intermediary.Version);
        Assert.True(intermediary.IsDependencyOnly);

        // And it is a real member of the resolved list, not just a lookup: quilt-loader, the
        // intermediary it pulled in, and Minecraft are all present.
        Assert.Contains(profile.Components, c => c.Uid == "org.quiltmc.quilt-loader");
    }

    [Fact]
    public async Task AnExactRequirementIsHonouredOverEverything()
    {
        // The ordinary case, and it must not be disturbed by the fallback below it.
        var index = IndexWith(
            Version("net.minecraft", "1.20.1"),
            Version("net.fabricmc.fabric-loader", "0.15.7", new Require("net.fabricmc.intermediary", "1.19.2")),
            Version("net.fabricmc.intermediary", "1.19.2"));

        var profile = ProfileWith(
            new Component("net.minecraft") { Version = "1.20.1", IsImportant = true },
            new Component("net.fabricmc.fabric-loader") { Version = "0.15.7" });

        Assert.True(await Resolve(profile, index).RunAsync());

        Assert.Equal("1.19.2", profile.GetComponent("net.fabricmc.intermediary")?.Version);
    }

    [Fact]
    public async Task ASuggestedVersionIsUsedWhenThereIsNoExactOne()
    {
        /*
         * What Minecraft does for LWJGL: `{ "uid": "org.lwjgl3", "suggests": "3.3.1" }`. This is the
         * path that made vanilla work in the previous wave, and it must keep winning over the
         * hardcoded floor below.
         */
        var index = IndexWith(
            Version("net.minecraft", "1.20.1", new Require("org.lwjgl3", string.Empty, "3.3.1")),
            Version("org.lwjgl3", "3.3.1"));

        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1", IsImportant = true });

        Assert.True(await Resolve(profile, index).RunAsync());

        Assert.Equal("3.3.1", profile.GetComponent("org.lwjgl3")?.Version);
    }

    [Fact]
    public async Task AnUnknownDependencyWithNoVersionIsLeftEmptyRatherThanGuessed()
    {
        /*
         * Upstream's table covers four uids and nothing else, and inventing a version for a fifth
         * would be this port disagreeing with upstream about what an instance contains. It stays
         * empty, and the launch reports it -- which is at least honest.
         */
        var index = IndexWith(
            Version("net.minecraft", "1.20.1", new Require("com.example.mystery")),
            Version("com.example.mystery", "1.0"));

        var profile = ProfileWith(new Component("net.minecraft") { Version = "1.20.1", IsImportant = true });

        await Resolve(profile, index).RunAsync();

        Assert.Equal(string.Empty, profile.GetComponent("com.example.mystery")?.Version ?? string.Empty);
    }

    [Fact]
    public async Task NoComponentEverEndsUpWithAnEmptyVersionForFabric()
    {
        /*
         * The guard that would have caught the original bug outright, stated as the thing that
         * actually matters: after resolving a Fabric instance, EVERY component has a version. One
         * without a version is a request for "<uid>/.json" waiting to happen.
         */
        var index = IndexWith(
            Version("net.minecraft", "1.20.1", new Require("org.lwjgl3", string.Empty, "3.3.1")),
            Version("org.lwjgl3", "3.3.1"),
            Version("net.fabricmc.fabric-loader", "0.15.7", new Require("net.fabricmc.intermediary")),
            Version("net.fabricmc.intermediary", "1.20.1"));

        var profile = ProfileWith(
            new Component("net.minecraft") { Version = "1.20.1", IsImportant = true },
            new Component("net.fabricmc.fabric-loader") { Version = "0.15.7" });

        Assert.True(await Resolve(profile, index).RunAsync());

        var versionless = profile.Components
            .Where(c => c.Version.Length == 0)
            .Select(c => c.Uid)
            .ToArray();

        Assert.True(versionless.Length == 0, "components with no version: " + string.Join(", ", versionless));
    }
}
