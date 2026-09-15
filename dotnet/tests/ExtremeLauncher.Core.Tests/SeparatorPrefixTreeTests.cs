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
 * For SeparatorPrefixTree, the export dialog's excluded-path tree. The three questions it answers drive
 * the folder checkbox: Cover ("this or an ancestor is excluded" → unchecked), Exists ("a descendant is"
 * → partially checked), neither → checked. Plus insert/remove/round-trip.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class SeparatorPrefixTreeTests
{
    private static SeparatorPrefixTree Tree(params string[] paths) => new(paths);

    [Fact]
    public void CoverFindsThePathItselfWhenInserted()
    {
        var tree = Tree("mods/sodium.jar");

        Assert.Equal("mods/sodium.jar", tree.Cover("mods/sodium.jar"));
    }

    [Fact]
    public void CoverFindsAnExcludedAncestor()
    {
        var tree = Tree("config");

        // A whole excluded folder covers everything inside it.
        Assert.Equal("config", tree.Cover("config/foo/bar.cfg"));
    }

    [Fact]
    public void CoverIsNullForAnUncoveredPath()
    {
        var tree = Tree("mods/sodium.jar");

        Assert.Null(tree.Cover("mods/lithium.jar"));
        Assert.Null(tree.Cover("config"));
    }

    [Fact]
    public void ExistsIsTrueForAnAncestorOfAnExcludedPath()
    {
        var tree = Tree("mods/sodium.jar");

        // "mods" has an excluded descendant but is not itself excluded → partially checked.
        Assert.True(tree.Exists("mods"));
        Assert.Null(tree.Cover("mods"));
    }

    [Fact]
    public void ExistsIsFalseForAnUnrelatedPath()
        => Assert.False(Tree("mods/sodium.jar").Exists("config"));

    [Fact]
    public void ContainsDistinguishesInsertedFromStructuralNodes()
    {
        var tree = Tree("mods/sodium.jar");

        Assert.True(tree.Contains("mods"));                 // structural node exists
        Assert.True(tree.Contains("mods/sodium.jar"));      // the inserted leaf
        Assert.True(tree.Find("mods/sodium.jar")!.Contained);
        Assert.False(tree.Find("mods")!.Contained);         // structural, not inserted
    }

    [Fact]
    public void RemovingAnInsertedPathUncoversIt()
    {
        var tree = Tree("mods/sodium.jar", "mods/lithium.jar");

        Assert.True(tree.Remove("mods/sodium.jar"));

        Assert.Null(tree.Cover("mods/sodium.jar"));
        Assert.Equal("mods/lithium.jar", tree.Cover("mods/lithium.jar")); // the sibling stays
    }

    [Fact]
    public void RemovingAStructuralPrefixTakesEverythingUnderIt()
    {
        var tree = Tree("mods/a.jar", "mods/b.jar");

        Assert.True(tree.Remove("mods"));

        Assert.Null(tree.Cover("mods/a.jar"));
        Assert.Null(tree.Cover("mods/b.jar"));
        Assert.Empty(tree.ToList());
    }

    [Fact]
    public void RemovingAnAbsentPathFails()
        => Assert.False(Tree("mods/a.jar").Remove("config/x.cfg"));

    [Fact]
    public void ToListRoundTripsTheInsertedPaths()
    {
        var tree = Tree("mods/a.jar", "config", "config/deep/x.cfg");

        Assert.Equal(
            ["config", "config/deep/x.cfg", "mods/a.jar"],
            tree.ToList().OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void ANonSlashSeparatorWorks()
    {
        var tree = new SeparatorPrefixTree(["a.b.c"], separator: '.');

        Assert.Equal("a.b.c", tree.Cover("a.b.c"));
        Assert.True(tree.Exists("a.b"));
    }
}
