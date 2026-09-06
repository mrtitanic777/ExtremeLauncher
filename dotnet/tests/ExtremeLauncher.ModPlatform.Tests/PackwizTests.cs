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
 * Ported from tests/Packwiz_test.cpp, run against the Qt suite's own fixtures.
 *
 * The two fixtures are real index files, one per provider — a Modrinth entry identified by two opaque
 * strings and a CurseForge one identified by two integers. Every expectation in the first two tests is
 * upstream's; anything beyond them is marked as added.
 */

using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class PackwizTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-pw-" + Guid.NewGuid().ToString("N"));

    public PackwizTests() => Directory.CreateDirectory(_temp);

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

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "testdata", "packwiz");

    // ================================================================== the inherited contracts

    [Fact]
    public void AModrinthEntryIsRead()
    {
        var mod = Packwiz.GetIndexForMod(FixtureDir, "borderless-mining");

        Assert.True(mod.IsValid);
        Assert.Equal("Borderless Mining", mod.Name);
        Assert.Equal("borderless-mining-1.1.1+1.18.jar", mod.Filename);
        Assert.Equal(PackwizSide.ClientSide, mod.Side);

        Assert.Equal(
            "https://cdn.modrinth.com/data/kYq5qkSL/versions/1.1.1+1.18/borderless-mining-1.1.1+1.18.jar",
            mod.Url);

        Assert.Equal("sha512", mod.HashFormat);
        Assert.Equal(
            "c8fe6e15ddea32668822dddb26e1851e5f03834be4bcb2eff9c0da7fdc086a9b6cead78e31a44d3bc66335cba11144ee0337c6d5346f1ba63623064499b3188d",
            mod.Hash);

        // Modrinth identifies a download by two opaque strings.
        Assert.Equal(ResourceProvider.Modrinth, mod.Provider);
        Assert.Equal("kYq5qkSL", mod.ModId);
        Assert.Equal("ug2qKTPR", mod.Version);
    }

    [Fact]
    public void ACurseForgeEntryIsRead()
    {
        var mod = Packwiz.GetIndexForMod(FixtureDir, "screenshot-to-clipboard-fabric");

        Assert.True(mod.IsValid);
        Assert.Equal("Screenshot to Clipboard (Fabric)", mod.Name);
        Assert.Equal("screenshot-to-clipboard-1.0.7-fabric.jar", mod.Filename);
        Assert.Equal(PackwizSide.UniversalSide, mod.Side);

        Assert.Equal("https://edge.forgecdn.net/files/3509/43/screenshot-to-clipboard-1.0.7-fabric.jar", mod.Url);
        Assert.Equal("murmur2", mod.HashFormat);
        Assert.Equal("1781245820", mod.Hash);

        // CurseForge identifies one by two integers instead.
        Assert.Equal(ResourceProvider.Flame, mod.Provider);
        Assert.Equal(3509043, mod.FileId);
        Assert.Equal(327154, mod.ProjectId);
    }

    [Fact]
    public void TheSlugMayCarryItsExtension()
    {
        // The caller may hold either form; both name the same entry.
        var withExtension = Packwiz.GetIndexForMod(FixtureDir, "borderless-mining.pw.toml");
        var without = Packwiz.GetIndexForMod(FixtureDir, "borderless-mining");

        Assert.Equal(without.Slug, withExtension.Slug);
        Assert.Equal("borderless-mining", withExtension.Slug);
    }

    // ================================================================== side mapping

    [Theory]
    [InlineData("client", PackwizSide.ClientSide)]
    [InlineData("server", PackwizSide.ServerSide)]
    [InlineData("both", PackwizSide.UniversalSide)]
    [InlineData("", PackwizSide.UniversalSide)]
    [InlineData("nonsense", PackwizSide.UniversalSide)]
    public void SidesRoundTripAndDefaultToUniversal(string text, PackwizSide expected)
    {
        // A mod that does not say is assumed to work on both — the safe reading, since refusing to
        // install it would be worse than installing it where it is not needed.
        Assert.Equal(expected, Packwiz.StringToSide(text));
    }

    [Theory]
    [InlineData(PackwizSide.ClientSide, "client")]
    [InlineData(PackwizSide.ServerSide, "server")]
    [InlineData(PackwizSide.UniversalSide, "both")]
    public void SidesAreWrittenInPackwizsVocabulary(PackwizSide side, string expected)
        => Assert.Equal(expected, Packwiz.SideToString(side));

    // ================================================================== round trip

    [Fact]
    public void AnEntryRoundTripsThroughDisk()
    {
        var original = new PackwizMod
        {
            Slug = "jei",
            Name = "Just Enough Items",
            Filename = "jei-1.20.1-15.2.0.27.jar",
            Side = PackwizSide.ClientSide,
            Url = "https://example.invalid/jei.jar",
            HashFormat = "sha512",
            Hash = "abc123",
            Provider = ResourceProvider.Flame,
            FileId = 4712868,
            ProjectId = 238222,
        };

        Assert.True(Packwiz.UpdateModIndex(_temp, original));

        var read = Packwiz.GetIndexForMod(_temp, "jei");

        Assert.Equal(original.Name, read.Name);
        Assert.Equal(original.Filename, read.Filename);
        Assert.Equal(original.Side, read.Side);
        Assert.Equal(original.Url, read.Url);
        Assert.Equal(original.FileId, read.FileId);
        Assert.Equal(original.ProjectId, read.ProjectId);
    }

    [Fact]
    public void TheLauncherSpecificKeysSurvive()
    {
        var original = new PackwizMod
        {
            Slug = "sodium",
            Name = "Sodium",
            Filename = "sodium.jar",
            Url = "https://example.invalid/sodium.jar",
            HashFormat = "sha512",
            Hash = "def",
            Provider = ResourceProvider.Modrinth,
            ModId = "AANobbMI",
            Version = "mc1.20.1",
            ReleaseType = "release",
        };

        original.Loaders.Add("fabric");
        original.McVersions.Add("1.20.1");
        original.McVersions.Add("1.20");

        Packwiz.UpdateModIndex(_temp, original);

        var read = Packwiz.GetIndexForMod(_temp, "sodium");

        // packwiz ignores unknown keys, so these travel harmlessly in a shared index.
        Assert.Equal(["fabric"], read.Loaders);
        Assert.Equal("release", read.ReleaseType);

        // Sorted on read, so a written file is stable regardless of the order an API returned them.
        Assert.Equal(["1.20", "1.20.1"], read.McVersions);
    }

    [Fact]
    public void TheWrittenFileLooksLikePackwizsOwn()
    {
        var mod = new PackwizMod
        {
            Slug = "test",
            Name = "Test",
            Filename = "test.jar",
            Side = PackwizSide.ClientSide,
            Url = "https://example.invalid/test.jar",
            HashFormat = "sha512",
            Hash = "abc",
            Provider = ResourceProvider.Modrinth,
            ModId = "aaa",
            Version = "bbb",
        };

        var text = Packwiz.Serialize(mod);

        // These files are read by the packwiz CLI and other launchers, so the section layout matters:
        // a diff against a packwiz-managed index should be empty rather than a reshuffle.
        Assert.Contains("[download]", text, StringComparison.Ordinal);
        Assert.Contains("[update]", text, StringComparison.Ordinal);

        // CurseForge is spelled "curseforge" in the file, not "flame".
        Assert.Contains("[update.modrinth]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Flame", text, StringComparison.Ordinal);

        Assert.StartsWith("name = \"Test\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACurseForgeEntryIsWrittenUnderItsPackwizName()
    {
        var mod = new PackwizMod
        {
            Slug = "test",
            Name = "Test",
            Filename = "test.jar",
            Url = "https://example.invalid/test.jar",
            Provider = ResourceProvider.Flame,
            FileId = 1,
            ProjectId = 2,
        };

        Assert.Contains("[update.curseforge]", Packwiz.Serialize(mod), StringComparison.Ordinal);
    }

    // ================================================================== refusals

    [Fact]
    public void AMissingEntryYieldsNothing()
    {
        var mod = Packwiz.GetIndexForMod(_temp, "not-installed");

        Assert.False(mod.IsValid);
        Assert.Equal(string.Empty, mod.Slug);
    }

    [Fact]
    public void AnEntryWithoutADownloadSectionIsRefused()
    {
        File.WriteAllText(Path.Combine(_temp, "broken.pw.toml"), """
            name = "Broken"
            filename = "broken.jar"

            [update]
            [update.modrinth]
            mod-id = "aaa"
            version = "bbb"
            """);

        // Without [download] there is nothing to fetch, so the entry is worse than absent — it would
        // look like tracking that is not there.
        Assert.False(Packwiz.GetIndexForMod(_temp, "broken").IsValid);
    }

    [Fact]
    public void AnEntryWithoutAProviderIsRefused()
    {
        File.WriteAllText(Path.Combine(_temp, "orphan.pw.toml"), """
            name = "Orphan"
            filename = "orphan.jar"

            [download]
            url = "https://example.invalid/orphan.jar"
            hash-format = "sha512"
            hash = "abc"

            [update]
            """);

        // [update] with no provider inside means there is no way to look for a newer version, which is
        // the only reason the index exists.
        Assert.False(Packwiz.GetIndexForMod(_temp, "orphan").IsValid);
    }

    [Fact]
    public void AnInvalidEntryIsNotWritten()
    {
        // Missing both provider identifiers.
        var mod = new PackwizMod { Slug = "half", Name = "Half", Provider = ResourceProvider.Modrinth };

        Assert.False(Packwiz.UpdateModIndex(_temp, mod));
        Assert.False(File.Exists(Path.Combine(_temp, "half.pw.toml")));
    }

    [Fact]
    public void UnparseableTomlIsRefusedRatherThanThrowing()
    {
        File.WriteAllText(Path.Combine(_temp, "garbage.pw.toml"), "this is not [[[ toml");

        Assert.False(Packwiz.GetIndexForMod(_temp, "garbage").IsValid);
    }

    // ================================================================== case handling

    [Fact]
    public void AnEntryIsFoundDespiteADifferenceInCase()
    {
        File.WriteAllText(Path.Combine(_temp, "JEI.pw.toml"), """
            name = "JEI"
            filename = "jei.jar"

            [download]
            url = "https://example.invalid/jei.jar"
            hash-format = "sha512"
            hash = "abc"

            [update]
            [update.curseforge]
            file-id = 1
            project-id = 2
            """);

        // Providers are not consistent about case, and on Linux the filesystem would otherwise treat
        // these as two different files.
        Assert.True(Packwiz.GetIndexForMod(_temp, "jei").IsValid);
    }

    [Fact]
    public void WritingConvergesOnOneSpelling()
    {
        File.WriteAllText(Path.Combine(_temp, "JEI.pw.toml"), "name = \"old\"");

        var mod = new PackwizMod
        {
            Slug = "jei",
            Name = "JEI",
            Filename = "jei.jar",
            Url = "https://example.invalid/jei.jar",
            Provider = ResourceProvider.Flame,
            FileId = 1,
            ProjectId = 2,
        };

        Packwiz.UpdateModIndex(_temp, mod);

        // Renamed rather than left alongside, so the index does not accumulate both spellings.
        Assert.True(File.Exists(Path.Combine(_temp, "jei.pw.toml")));
        Assert.Single(Directory.GetFiles(_temp, "*.pw.toml"));
    }

    // ================================================================== lookup and deletion

    [Fact]
    public void AnEntryCanBeFoundByItsProviderId()
    {
        var mod = new PackwizMod
        {
            Slug = "sodium",
            Name = "Sodium",
            Filename = "sodium.jar",
            Url = "https://example.invalid/sodium.jar",
            Provider = ResourceProvider.Modrinth,
            ModId = "AANobbMI",
            Version = "mc1.20.1",
        };

        Packwiz.UpdateModIndex(_temp, mod);

        Assert.Equal("sodium", Packwiz.GetIndexForModId(_temp, "AANobbMI").Slug);
        Assert.False(Packwiz.GetIndexForModId(_temp, "not-there").IsValid);
    }

    [Fact]
    public void AnEntryCanBeDeleted()
    {
        var mod = new PackwizMod
        {
            Slug = "jei",
            Name = "JEI",
            Filename = "jei.jar",
            Url = "https://example.invalid/jei.jar",
            Provider = ResourceProvider.Flame,
            FileId = 1,
            ProjectId = 2,
        };

        Packwiz.UpdateModIndex(_temp, mod);

        Assert.True(Packwiz.DeleteModIndex(_temp, "jei"));
        Assert.False(Packwiz.GetIndexForMod(_temp, "jei").IsValid);

        // Deleting something that is not there is not an error, just nothing.
        Assert.False(Packwiz.DeleteModIndex(_temp, "jei"));
    }
}
