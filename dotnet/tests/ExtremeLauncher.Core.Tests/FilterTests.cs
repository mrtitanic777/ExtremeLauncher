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
 * FIVE FILTERS THAT DIFFER ONLY IN WHAT "NOTHING" MEANS, which is the only interesting thing about
 * them and the only thing worth testing. Picking the wrong one shows a user an empty version list
 * with no explanation, or a filter that silently never filters -- neither of which errors.
 *
 * My first pass got two of these backwards: it made ExactFilter accept an empty value (that is
 * ExactIfPresentFilter's job) and gave ContainsFilter a deliberate empty-pattern guard it does not
 * have. Hence a test per filter per case rather than one representative each.
 */

using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class FilterTests
{
    // ================================================================== exact

    [Fact]
    public void ExactFilterIsStrict()
    {
        var filter = new ExactFilter("release");

        Assert.True(filter.Accepts("release"));
        Assert.False(filter.Accepts("snapshot"));

        // No empty-value exemption: that belongs to ExactIfPresentFilter.
        Assert.False(filter.Accepts(string.Empty));
    }

    /// <summary>Here a missing field is not evidence of a mismatch.</summary>
    [Fact]
    public void ExactIfPresentFilterLetsAnEmptyValueThrough()
    {
        var filter = new ExactIfPresentFilter("release");

        Assert.True(filter.Accepts("release"));
        Assert.True(filter.Accepts(string.Empty));
        Assert.False(filter.Accepts("snapshot"));
    }

    /// <summary>An empty PATTERN is not special in either — only the value is, and only in one.</summary>
    [Fact]
    public void AnEmptyPatternMatchesOnlyAnEmptyValue()
    {
        Assert.True(new ExactFilter(string.Empty).Accepts(string.Empty));
        Assert.False(new ExactFilter(string.Empty).Accepts("release"));
    }

    // ================================================================== list

    [Fact]
    public void ExactListFilterAcceptsAnyMember()
    {
        var filter = new ExactListFilter(["release", "snapshot"]);

        Assert.True(filter.Accepts("release"));
        Assert.True(filter.Accepts("snapshot"));
        Assert.False(filter.Accepts("old_beta"));
    }

    /// <summary>An empty LIST means no constraint — the pickers rely on this.</summary>
    [Fact]
    public void AnEmptyListAcceptsEverything()
    {
        Assert.True(new ExactListFilter().Accepts("anything"));
        Assert.True(new ExactListFilter([]).Accepts("anything"));
    }

    /// <summary>An empty value is not special here, unlike ExactIfPresentFilter.</summary>
    [Fact]
    public void AnEmptyValueIsJustAnotherMissForAListFilter()
        => Assert.False(new ExactListFilter(["release"]).Accepts(string.Empty));

    // ================================================================== contains

    [Fact]
    public void ContainsFilterMatchesASubstring()
    {
        var filter = new ContainsFilter("1.20");

        Assert.True(filter.Accepts("1.20.1"));
        Assert.True(filter.Accepts("pre-1.20-rc"));
        Assert.False(filter.Accepts("1.19.4"));
    }

    /*
     * An empty pattern accepts everything, but as a CONSEQUENCE of what a substring test does rather
     * than by design -- upstream writes a bare contains() with no guard. Pinned so the behaviour is
     * not mistaken for a deliberate "unset means no constraint".
     */
    [Fact]
    public void AnEmptyContainsPatternMatchesAnythingByAccident()
    {
        Assert.True(new ContainsFilter(string.Empty).Accepts("anything"));
        Assert.True(new ContainsFilter(string.Empty).Accepts(string.Empty));
    }

    // ================================================================== regexp

    [Fact]
    public void RegexpFilterMatchesAndInverts()
    {
        Assert.True(new RegexpFilter("^1[.]20").Accepts("1.20.1"));
        Assert.False(new RegexpFilter("^1[.]20").Accepts("1.19.4"));

        Assert.False(new RegexpFilter("^1[.]20", invert: true).Accepts("1.20.1"));
        Assert.True(new RegexpFilter("^1[.]20", invert: true).Accepts("1.19.4"));
    }

    /// <summary>Unanchored, like QRegularExpression::match — a filter wanting anchors must say so.</summary>
    [Fact]
    public void RegexpFilterMatchesAnywhereUnlessAnchored()
    {
        Assert.True(new RegexpFilter("20").Accepts("1.20.1"));
        Assert.False(new RegexpFilter("^20").Accepts("1.20.1"));
    }
}
