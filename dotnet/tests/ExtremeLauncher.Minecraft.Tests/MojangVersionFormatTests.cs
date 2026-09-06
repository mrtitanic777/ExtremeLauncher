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
 * Ported from tests/MojangVersionFormat_test.cpp, using the same 1.9.json and 1.9-simple.json.
 *
 * ============================ DELIBERATE CONTRACT CHANGE ============================
 * Upstream asserts QCOMPARE(doc.toJson(), doc2.toJson()) -- a BYTE-identical round trip. That holds
 * only because both sides go through Qt's JSON writer, which sorts object keys alphabetically and
 * indents with four spaces. System.Text.Json does neither, so a literal port would require
 * reimplementing Qt's writer to test a formatting coincidence.
 *
 * These tests assert a SEMANTIC round trip instead: parse the fixture, serialize, and deep-compare
 * the resulting tree against the original. That still catches everything the original was protecting
 * against -- a dropped field, an invented field, a mangled value, a lost list element -- because the
 * comparison is over the whole document, not a hand-picked subset. What it does not check is byte
 * layout, which no consumer depends on.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using Xunit;

using Json = ExtremeLauncher.Core.Json;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class MojangVersionFormatTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "testdata", "MojangVersionFormat");

    private static JsonObject ReadFixture(string name)
        => Json.RequireObject(Json.RequireDocument(File.ReadAllBytes(Path.Combine(FixtureDir, name)), name));

    /// <summary>Order-insensitive structural equality over two JSON trees.</summary>
    private static bool DeepEquals(JsonNode? left, JsonNode? right, string path, out string difference)
    {
        difference = string.Empty;

        if (left is null || right is null)
        {
            if (left is null && right is null)
            {
                return true;
            }

            difference = $"{path}: one side is null ({(left is null ? "left" : "right")})";
            return false;
        }

        switch (left)
        {
            case JsonObject leftObject:
            {
                if (right is not JsonObject rightObject)
                {
                    difference = $"{path}: object vs {right.GetValueKind()}";
                    return false;
                }

                foreach (var (key, value) in leftObject)
                {
                    if (!rightObject.ContainsKey(key))
                    {
                        difference = $"{path}.{key}: missing on the right";
                        return false;
                    }

                    if (!DeepEquals(value, rightObject[key], $"{path}.{key}", out difference))
                    {
                        return false;
                    }
                }

                foreach (var (key, _) in rightObject)
                {
                    if (!leftObject.ContainsKey(key))
                    {
                        difference = $"{path}.{key}: invented on the right";
                        return false;
                    }
                }

                return true;
            }

            case JsonArray leftArray:
            {
                if (right is not JsonArray rightArray)
                {
                    difference = $"{path}: array vs {right.GetValueKind()}";
                    return false;
                }

                if (leftArray.Count != rightArray.Count)
                {
                    difference = $"{path}: {leftArray.Count} elements vs {rightArray.Count}";
                    return false;
                }

                for (var i = 0; i < leftArray.Count; i++)
                {
                    if (!DeepEquals(leftArray[i], rightArray[i], $"{path}[{i}]", out difference))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
            {
                // Compare by rendered value so 18 and 18.0 do not read as different.
                var leftText = left.ToJsonString();
                var rightText = right.ToJsonString();

                if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
                {
                    difference = $"{path}: {leftText} vs {rightText}";
                    return false;
                }

                return true;
            }
        }
    }

    private static void AssertRoundTrips(string fixture)
    {
        var original = ReadFixture(fixture);

        var versionFile = MojangVersionFormat.VersionFileFromJson(original, fixture);
        var written = MojangVersionFormat.VersionFileToJson(versionFile);

        Assert.True(DeepEquals(original, written, fixture, out var difference), difference);
    }

    [Fact]
    public void SimpleVersionFileRoundTrips() => AssertRoundTrips("1.9-simple.json");

    [Fact]
    public void FullVersionFileRoundTrips() => AssertRoundTrips("1.9.json");

    // ================================================================== specifics worth pinning

    [Fact]
    public void ParsesTheTopLevelProperties()
    {
        var version = MojangVersionFormat.VersionFileFromJson(ReadFixture("1.9.json"), "1.9.json");

        Assert.Equal("1.9", version.MinecraftVersion);
        Assert.Equal("net.minecraft.client.main.Main", version.MainClass);
        Assert.Equal("release", version.Type);
        Assert.Equal("1.9", version.Assets);
        Assert.Equal(18, version.MinimumLauncherVersion);

        // Identity is assigned by the parser, not read from the file.
        Assert.Equal("Minecraft", version.Name);
        Assert.Equal("net.minecraft", version.Uid);
        Assert.Equal("1.9", version.Version);
    }

    [Fact]
    public void ParsesEveryLibrary()
    {
        var version = MojangVersionFormat.VersionFileFromJson(ReadFixture("1.9.json"), "1.9.json");

        Assert.Equal(33, version.Libraries.Count);
        Assert.Contains(version.Libraries, l => l.IsNative);
        Assert.Contains(version.Libraries, l => l.Rules.Count != 0);
    }

    [Fact]
    public void ParsesClientAndServerDownloads()
    {
        var version = MojangVersionFormat.VersionFileFromJson(ReadFixture("1.9.json"), "1.9.json");

        Assert.Equal(2, version.MojangDownloads.Count);
        Assert.True(version.MojangDownloads.ContainsKey("client"));
        Assert.True(version.MojangDownloads.ContainsKey("server"));
        Assert.NotEqual(string.Empty, version.MojangDownloads["client"].Sha1);
    }

    [Fact]
    public void ASuppliedAssetIndexIsKnownAndWrittenBack()
    {
        var version = MojangVersionFormat.VersionFileFromJson(ReadFixture("1.9.json"), "1.9.json");

        Assert.NotNull(version.MojangAssetIndex);
        Assert.True(version.MojangAssetIndex.Known);

        Assert.True(MojangVersionFormat.VersionFileToJson(version).ContainsKey("assetIndex"));
    }

    [Fact]
    public void ASynthesisedAssetIndexIsNotWrittenBack()
    {
        // 1.9-simple.json has "assets" but no "assetIndex" block, so one gets synthesised.
        var version = MojangVersionFormat.VersionFileFromJson(ReadFixture("1.9-simple.json"), "1.9-simple.json");

        Assert.NotNull(version.MojangAssetIndex);
        Assert.False(version.MojangAssetIndex.Known);

        // Writing it back out would fabricate data Mojang never sent, and corrupt every cached copy.
        Assert.False(MojangVersionFormat.VersionFileToJson(version).ContainsKey("assetIndex"));
    }

    [Fact]
    public void TimestampsSurviveTheRoundTrip()
    {
        var original = ReadFixture("1.9.json");
        var version = MojangVersionFormat.VersionFileFromJson(original, "1.9.json");
        var written = MojangVersionFormat.VersionFileToJson(version);

        Assert.Equal(Json.EnsureString(original, "releaseTime"), Json.EnsureString(written, "releaseTime"));
        Assert.Equal(Json.EnsureString(original, "time"), Json.EnsureString(written, "time"));
    }

    [Fact]
    public void ABrokenLibraryNameIsAProblemNotAnException()
    {
        var document = Json.RequireObject(Json.RequireDocument("""
            { "id": "1.0", "libraries": [ { "name": "I like turtles" } ] }
            """));

        var version = MojangVersionFormat.VersionFileFromJson(document, "broken.json");

        // The version file still loads; the complaint is recorded instead.
        Assert.Single(version.Libraries);
        Assert.Equal(ProblemSeverity.Error, version.GetProblemSeverity());
        Assert.Contains(version.GetProblems(), p => p.Description.Contains("broken", StringComparison.Ordinal));
    }

    [Fact]
    public void ALibraryWithoutANameIsFatal()
    {
        var document = Json.RequireObject(Json.RequireDocument("""
            { "id": "1.0", "libraries": [ { "url": "https://example.invalid/" } ] }
            """));

        Assert.Throws<JsonException>(() => MojangVersionFormat.VersionFileFromJson(document, "nameless.json"));
    }

    [Fact]
    public void AFutureMinimumLauncherVersionWarns()
    {
        var document = Json.RequireObject(Json.RequireDocument("""
            { "id": "9.9", "minimumLauncherVersion": 9999 }
            """));

        var version = MojangVersionFormat.VersionFileFromJson(document, "future.json");

        Assert.Equal(ProblemSeverity.Warning, version.GetProblemSeverity());
    }
}
