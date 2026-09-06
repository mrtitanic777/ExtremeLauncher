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
 * Characterization tests for the StringUtils port. There is no upstream Qt test for this file, so
 * these pin the behaviour we ported rather than inheriting a contract. Cases whose result depends on
 * ICU collation ordering (e.g. 'a' vs 'A' under case-sensitive compare) are deliberately avoided.
 */

using System.Text.RegularExpressions;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class StringUtilsTests
{
    [Fact]
    public void NaturalCompareOrdersEmbeddedNumbersNumerically()
    {
        // The whole point: lexical sort would put "pack10" before "pack2".
        Assert.True(StringUtils.NaturalCompare("pack2", "pack10", CaseSensitivity.CaseSensitive) < 0);
        Assert.True(StringUtils.NaturalCompare("pack10", "pack2", CaseSensitivity.CaseSensitive) > 0);
        Assert.Equal(0, StringUtils.NaturalCompare("pack2", "pack2", CaseSensitivity.CaseSensitive));
    }

    [Fact]
    public void NaturalCompareHandlesMultiDigitRuns()
    {
        Assert.True(StringUtils.NaturalCompare("v1.9.0", "v1.10.0", CaseSensitivity.CaseSensitive) < 0);
        Assert.True(StringUtils.NaturalCompare("instance99", "instance100", CaseSensitivity.CaseSensitive) < 0);
    }

    [Fact]
    public void NaturalCompareIsCaseInsensitiveWhenAsked()
    {
        Assert.Equal(0, StringUtils.NaturalCompare("PACK2", "pack2", CaseSensitivity.CaseInsensitive));
        Assert.True(StringUtils.NaturalCompare("PACK2", "pack10", CaseSensitivity.CaseInsensitive) < 0);
    }

    [Fact]
    public void NaturalCompareFallsBackToOrdinalWhenNumericallyEqual()
    {
        // "02" and "2" compare equal during the digit scan, so the trailing string.Compare decides.
        // '0' sorts below '2', hence negative.
        Assert.True(StringUtils.NaturalCompare("pack02", "pack2", CaseSensitivity.CaseSensitive) < 0);
    }

    [Fact]
    public void NaturalCompareSkipsWhitespaceDuringScanButNotInTheFallback()
    {
        // Spaces are skipped while scanning, so the scan finds no difference; the ordinal fallback
        // then separates them. Pinned because it is surprising, not because it is desirable.
        Assert.True(StringUtils.NaturalCompare("a b", "ab", CaseSensitivity.CaseSensitive) < 0);
    }

    [Theory]
    [InlineData(0d, false, "0.00 KiB")]
    [InlineData(1024d, false, "1.00 KiB")]
    [InlineData(1048576d, false, "1.00 MiB")]
    [InlineData(1073741824d, false, "1.00 GiB")]
    [InlineData(1500d, false, "1.46 KiB")]
    [InlineData(1000d, true, "1.00 KB")]
    [InlineData(1000000d, true, "1.00 MB")]
    public void HumanReadableFileSizeScalesAndFormats(double bytes, bool useSi, string expected)
        => Assert.Equal(expected, StringUtils.HumanReadableFileSize(bytes, useSi));

    [Fact]
    public void HumanReadableFileSizeAlwaysScalesAtLeastOnce()
    {
        // do/while, so even sub-kilobyte values are divided once. Faithful to the C++.
        Assert.Equal("0.50 KiB", StringUtils.HumanReadableFileSize(512));
    }

    [Fact]
    public void GetRandomAlphaNumericReturnsThirtyTwoHexDigits()
    {
        var value = StringUtils.GetRandomAlphaNumeric();

        Assert.Equal(32, value.Length);
        Assert.All(value, c => Assert.True(Uri.IsHexDigit(c), $"'{c}' is not a hex digit"));
        Assert.NotEqual(value, StringUtils.GetRandomAlphaNumeric());
    }

    [Fact]
    public void SplitFirstOnStringSeparator()
    {
        var (left, right) = StringUtils.SplitFirst("Name: Extreme Launcher", ": ");

        Assert.Equal("Name", left);
        Assert.Equal("Extreme Launcher", right);
    }

    [Fact]
    public void SplitFirstOnStringSeparatorWhenAbsentIsQuirky()
    {
        // QUIRK preserved from upstream: index == -1 makes the left half the whole string and the
        // right half the string minus (separator.Length - 1) leading characters.
        var (left, right) = StringUtils.SplitFirst("abc", ": ");

        Assert.Equal("abc", left);
        Assert.Equal("bc", right);
    }

    [Fact]
    public void SplitFirstOnCharSeparator()
    {
        var (left, right) = StringUtils.SplitFirst("a\nb\nc", '\n');

        Assert.Equal("a", left);
        Assert.Equal("b\nc", right);
    }

    [Fact]
    public void SplitFirstOnCharSeparatorWhenAbsentYieldsEmptyRemainder()
    {
        // The char overload derives the remainder from the left half's length, so unlike the string
        // overload it degrades cleanly.
        var (left, right) = StringUtils.SplitFirst("abc", '\n');

        Assert.Equal("abc", left);
        Assert.Equal(string.Empty, right);
    }

    [Fact]
    public void SplitFirstOnRegex()
    {
        var (left, right) = StringUtils.SplitFirst("key = value", new Regex(@"\s*=\s*"));

        Assert.Equal("key", left);
        Assert.Equal("value", right);
    }

    [Fact]
    public void SplitFirstOnRegexWhenAbsentYieldsEmptyRemainder()
    {
        var (left, right) = StringUtils.SplitFirst("abc", new Regex(@"\d+"));

        Assert.Equal("abc", left);
        Assert.Equal(string.Empty, right);
    }

    [Fact]
    public void HtmlListPatchInsertsBreakBeforeAdjacentImage()
    {
        Assert.Equal(
            "<ul><li>a</li></ul><br><img src=\"x\">",
            StringUtils.HtmlListPatch("<ul><li>a</li></ul><img src=\"x\">"));
    }

    [Fact]
    public void HtmlListPatchLeavesTextSeparatedImagesAlone()
    {
        const string html = "<ul><li>a</li></ul>text<img src=\"x\">";

        Assert.Equal(html, StringUtils.HtmlListPatch(html));
    }

    [Fact]
    public void HtmlListPatchIsANoOpWithoutLists()
    {
        const string html = "<p>no lists here</p><img src=\"x\">";

        Assert.Equal(html, StringUtils.HtmlListPatch(html));
    }

    [Fact]
    public void HtmlListPatchToleratesWhitespaceInTheClosingTag()
    {
        Assert.Equal(
            "<ul><li>a</li>< / ul ><br><img src=\"x\">",
            StringUtils.HtmlListPatch("<ul><li>a</li>< / ul ><img src=\"x\">"));
    }
}
