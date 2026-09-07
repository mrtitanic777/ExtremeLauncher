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
 * Ported from tests/CatPack_test.cpp against the SAME testdata/CatPacks/index.json, so both launchers
 * answer to one source of truth. The interesting behaviour is all at the range edges: the single-day
 * variant, the overlap where the earlier variant in file order wins (28 Dec is in both the Christmas
 * and the New Year ranges), and the two ranges that wrap across the new year.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class CatPackTests
{
    private static readonly string ManifestPath =
        Path.Combine(AppContext.BaseDirectory, "testdata", "CatPacks", "index.json");

    private static string Expected(string image)
        => FileSystem.PathCombine(Path.GetDirectoryName(ManifestPath)!, image);

    [Theory]
    [InlineData(2023, 4, 12, "oneDay.png")]     // the single-day variant, exactly on its day
    [InlineData(2023, 4, 11, "maxwell.png")]    // the day before it: default
    [InlineData(2023, 4, 13, "maxwell.png")]    // the day after it: default
    [InlineData(2023, 12, 21, "christmas.png")] // inside the Christmas range
    [InlineData(2023, 12, 28, "christmas.png")] // overlap day: Christmas is listed first, so it wins
    [InlineData(2023, 12, 29, "newyear.png")]   // now only the New Year range covers it
    [InlineData(2023, 12, 30, "newyear2.png")]  // newyear2 (30 Dec–1 Jan) wins, listed before newyear
    [InlineData(2023, 12, 31, "newyear2.png")]  // still in the wrapping newyear2 range
    [InlineData(2024, 1, 1, "newyear2.png")]    // the far end of the wrap, in the next year
    [InlineData(2024, 1, 2, "newyear.png")]     // past newyear2; still inside the wrapping newyear range
    [InlineData(2024, 1, 3, "newyear.png")]     // the last day of the newyear range
    [InlineData(2024, 1, 4, "maxwell.png")]     // past every range: default
    public void ThePackPicksTheRightCatForTheDay(int year, int month, int day, string image)
    {
        var pack = new JsonCatPack(ManifestPath);

        Assert.Equal(Expected(image), pack.Path(new DateOnly(year, month, day)));
    }

    [Fact]
    public void TheManifestNameAndIdAreReadBack()
    {
        var pack = new JsonCatPack(ManifestPath);

        Assert.Equal("My Cute Cat", pack.Name);

        // Upstream keys a JSON cat pack by the directory it lives in.
        Assert.Equal("CatPacks", pack.Id);
    }
}
