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
 * Ported from tests/Version_test.cpp.
 */

using System.Text;
using Xunit;

using Version = ExtremeLauncher.Core.Version;

namespace ExtremeLauncher.Core.Tests;

public sealed class VersionTests
{
    /// <summary>Cases transcribed from <c>VersionTest::setupVersions()</c>.</summary>
    public static TheoryData<string, string, bool, bool> ExplicitVersions => new()
    {
        // first, second, lessThan, equal
        { "1.2.0", "1.2.0", false, true },
        { "1.42", "1.42", false, true },

        { "1.2.0", "1.2.1", true, false },
        { "1.2.0", "1.3.0", true, false },
        { "1.2.0", "2.2.0", true, false },
        { "1.2", "1.2.0", true, false },
        { "1.2", "1.2.1", true, false },
        { "1.2", "1.3.0", true, false },
        { "1.2", "2.2.0", true, false },
        { "1.41", "1.42", true, false },
        { "1.20.0-rc2", "1.20.1", true, false },

        { "1.2.1", "1.2.0", false, false },
        { "1.3.0", "1.2.0", false, false },
        { "2.2.0", "1.2.0", false, false },
        { "1.2.0", "1.2", false, false },
        { "1.2.1", "1.2", false, false },
        { "1.3.0", "1.2", false, false },
        { "2.2.0", "1.2", false, false },
        { "1.42", "1.41", false, false },
        { "1.20.2-rc2", "1.20.1", false, false },
    };

    /// <summary>
    /// The FlexVer vectors, read from the same file the Qt suite uses.
    /// </summary>
    /// <remarks>
    /// The C++ harness reads with <c>for (line = readLine(); !atEnd(); line = readLine())</c>, which
    /// drops the final line of the file. We read every line instead.
    /// </remarks>
    public static TheoryData<string, string, bool, bool> FlexVerVectors
    {
        get
        {
            var data = new TheoryData<string, string, bool, bool>();
            var path = Path.Combine(AppContext.BaseDirectory, "testdata", "Version", "test_vectors.txt");

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = Simplified(rawLine);

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                // Order matters: match the C++ harness, which tries '<', then '=', then '>'.
                // Note "a0-a < a0=a" carries two operator characters; splitting on '<' first is
                // what makes it a lessThan case in both implementations.
                var matched = false;

                foreach (var op in new[] { '<', '=', '>' })
                {
                    var parts = line.Split(op);

                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    var first = Simplified(parts[0]);
                    var second = Simplified(parts[1]);

                    data.Add(first, second, op == '<', op == '=');
                    matched = true;
                    break;
                }

                if (!matched)
                {
                    // The C++ harness fails the run here rather than skipping; do the same.
                    throw new InvalidDataException($"Unexpected separator in the test vector: {line}");
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ExplicitVersions))]
    public void CompareExplicitVersions(string first, string second, bool lessThan, bool equal)
        => AssertOrdering(first, second, lessThan, equal);

    [Theory]
    [MemberData(nameof(FlexVerVectors))]
    public void CompareFlexVerVectors(string first, string second, bool lessThan, bool equal)
        => AssertOrdering(first, second, lessThan, equal);

    [Fact]
    public void EmptyVersionIsEmpty()
    {
        Assert.True(new Version(string.Empty).IsEmpty);
        Assert.True(new Version().IsEmpty);
        Assert.False(new Version("1.0").IsEmpty);
    }

    [Fact]
    public void ToStringRoundTripsTheRawInput()
    {
        const string raw = "1.20.1-rc2+build.5";
        Assert.Equal(raw, new Version(raw).ToString());
    }

    [Fact]
    public void AppendixIsExcludedFromComparison()
    {
        // Sections at or after a '+' appendix are not compared.
        Assert.Equal(new Version("1.0"), new Version("1.0+build"));
        Assert.Equal(new Version("1.0").GetHashCode(), new Version("1.0+build").GetHashCode());
    }

    private static void AssertOrdering(string first, string second, bool lessThan, bool equal)
    {
        var v1 = new Version(first);
        var v2 = new Version(second);

        Assert.Equal(lessThan, v1 < v2);
        Assert.Equal(!lessThan && !equal, v1 > v2);
        Assert.Equal(equal, v1 == v2);
    }

    /// <summary>Equivalent of <c>QString::simplified()</c>: trim, then collapse internal whitespace runs.</summary>
    private static string Simplified(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
