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
 * A .mrpack is an untrusted file from the internet whose index chooses where every download lands, so
 * the path tests below are the important ones. The rest cover the format's own decisions: which files
 * a client installs, what an update no longer needs to fetch, and what it has to delete.
 */

using System.Text;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class ModrinthPackManifestTests
{
    private const string Sha512 =
        "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce"
        + "47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e";

    private static ModrinthPackManifest Parse(string json) => ModrinthPack.Parse(Encoding.UTF8.GetBytes(json));

    private static string Manifest(string files, string dependencies = """{ "minecraft": "1.20.1" }""") => $$"""
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "1.0.0",
          "name": "Test Pack",
          "files": {{files}},
          "dependencies": {{dependencies}}
        }
        """;

    private static string File(string path, string env = "", string downloads = """["https://cdn.modrinth.com/a.jar"]""")
        => $$"""
        {
          "path": "{{path}}",
          {{(env.Length == 0 ? "" : $"\"env\": {env},")}}
          "hashes": { "sha512": "{{Sha512}}" },
          "downloads": {{downloads}}
        }
        """;

    // ================================================================== the format's own gates

    [Fact]
    public void AWellFormedPackIsRead()
    {
        var manifest = Parse(Manifest($"[{File("mods/a.jar")}]"));

        Assert.Equal("Test Pack", manifest.Name);
        Assert.Equal("1.0.0", manifest.VersionId);
        Assert.Equal("1.20.1", manifest.Dependencies.MinecraftVersion);

        var file = Assert.Single(manifest.Files);

        Assert.Equal("mods/a.jar", file.Path);
        Assert.Equal(64, file.Hash.Length);
        Assert.Equal(new Uri("https://cdn.modrinth.com/a.jar"), Assert.Single(file.Downloads));
    }

    /// <summary>A later format could mean anything, including different meanings for fields that parse.</summary>
    [Fact]
    public void AnUnknownFormatVersionIsRefused()
        => Assert.ThrowsAny<LauncherException>(() => Parse("""
            { "formatVersion": 2, "game": "minecraft", "files": [], "dependencies": {} }
            """));

    [Fact]
    public void APackForAnotherGameIsRefused()
        => Assert.ThrowsAny<LauncherException>(() => Parse("""
            { "formatVersion": 1, "game": "terraria", "files": [], "dependencies": {} }
            """));

    /*
     * Refusing an unknown dependency is right: silently skipping one builds an instance missing the
     * loader the pack needs, which fails much later and much less clearly.
     */
    [Fact]
    public void AnUnknownDependencyIsRefusedRatherThanIgnored()
        => Assert.ThrowsAny<LauncherException>(
            () => Parse(Manifest("[]", """{ "minecraft": "1.20.1", "rift-loader": "1.0" }""")));

    [Theory]
    [InlineData("fabric-loader", nameof(ModrinthPackDependencies.FabricVersion))]
    [InlineData("quilt-loader", nameof(ModrinthPackDependencies.QuiltVersion))]
    [InlineData("forge", nameof(ModrinthPackDependencies.ForgeVersion))]
    [InlineData("neoforge", nameof(ModrinthPackDependencies.NeoForgeVersion))]
    public void EveryKnownLoaderDependencyIsRead(string key, string property)
    {
        var manifest = Parse(Manifest("[]", $$"""{ "minecraft": "1.20.1", "{{key}}": "0.15.0" }"""));

        var value = typeof(ModrinthPackDependencies).GetProperty(property)!.GetValue(manifest.Dependencies);

        Assert.Equal("0.15.0", value);
    }

    // ================================================================== client, server, optional

    /// <summary>A server-only file is not an error — it is just not ours to install.</summary>
    [Fact]
    public void AFileUnsupportedOnTheClientIsSkipped()
    {
        var manifest = Parse(Manifest($"""[{File("mods/server-only.jar", """{ "client": "unsupported", "server": "required" }""")}]"""));

        Assert.Empty(manifest.Files);
        Assert.Empty(manifest.OptionalFiles);
    }

    /// <summary>Optional files are kept apart, because the caller has a choice to make about them.</summary>
    [Fact]
    public void OptionalFilesComeBackSeparately()
    {
        var manifest = Parse(Manifest($"""
            [{File("mods/required.jar")},
             {File("mods/optional.jar", """{ "client": "optional", "server": "optional" }""")}]
            """));

        Assert.Equal("mods/required.jar", Assert.Single(manifest.Files).Path);
        Assert.Equal("mods/optional.jar", Assert.Single(manifest.OptionalFiles).Path);
    }

    [Fact]
    public void AFileWithNoEnvBlockIsRequired()
        => Assert.True(Assert.Single(Parse(Manifest($"[{File("mods/a.jar")}]")).Files).Required);

    /*
     * A declined optional mod is still installed, with .disabled appended -- upstream's behaviour and
     * better than it looks: turning it on later is a rename rather than a download.
     */
    [Fact]
    public void DecliningAnOptionalFileInstallsItDisabled()
    {
        var manifest = Parse(Manifest($"""
            [{File("mods/wanted.jar", """{ "client": "optional" }""")},
             {File("mods/declined.jar", """{ "client": "optional" }""")}]
            """));

        ModrinthPack.ApplyOptionalSelection(manifest, new HashSet<string> { "mods/wanted.jar" });

        Assert.Empty(manifest.OptionalFiles);

        var wanted = manifest.Files.Single(f => f.Path == "mods/wanted.jar");
        var declined = manifest.Files.Single(f => f.Path == "mods/declined.jar.disabled");

        Assert.True(wanted.Required);
        Assert.False(declined.Required);
    }

    // ================================================================== downloads

    /// <summary>A malformed mirror is skipped; the pack may still be installable from the others.</summary>
    [Fact]
    public void AMalformedMirrorIsSkippedButTheRestSurvive()
    {
        var manifest = Parse(Manifest($"""
            [{File("mods/a.jar", downloads: """["not a url", "https://cdn.modrinth.com/a.jar"]""")}]
            """));

        Assert.Equal(new Uri("https://cdn.modrinth.com/a.jar"), Assert.Single(Assert.Single(manifest.Files).Downloads));
    }

    /// <summary>No usable mirror at all is fatal — the pack cannot be installed without the file.</summary>
    [Theory]
    [InlineData("""[]""")]
    [InlineData("""["not a url"]""")]
    public void AFileWithNoUsableMirrorIsFatal(string downloads)
        => Assert.ThrowsAny<LauncherException>(() => Parse(Manifest($"""[{File("mods/a.jar", downloads: downloads)}]""")));

    [Fact]
    public void MirrorsKeepThePacksOrder()
    {
        var manifest = Parse(Manifest($"""
            [{File("mods/a.jar", downloads: """["https://a.invalid/x.jar", "https://b.invalid/x.jar"]""")}]
            """));

        Assert.Equal(
            [new Uri("https://a.invalid/x.jar"), new Uri("https://b.invalid/x.jar")],
            Assert.Single(manifest.Files).Downloads);
    }

    // ================================================================== paths

    /// <summary>Packs built on Windows carry backslashes; the path is used relatively everywhere.</summary>
    [Fact]
    public void BackslashesAreNormalised()
        => Assert.Equal(
            "mods/sub/a.jar",
            Assert.Single(Parse(Manifest($"""[{File(@"mods\\sub\\a.jar")}]""")).Files).Path);

    /*
     * SECURITY, AND NOT PRESENT UPSTREAM.
     *
     * A .mrpack is an untrusted download whose index names its own destinations. Upstream reads
     * "path" straight out of the JSON and joins it to the game directory, so an entry like
     * "../../../../.bashrc" writes there -- the same class of hole as zip-slip, which the archive
     * layer already guards. Each spelling below is checked because they fail differently.
     */
    [Theory]
    [InlineData("../outside.jar")]
    [InlineData("mods/../../outside.jar")]
    [InlineData("../../../../.bashrc")]
    [InlineData("..")]
    [InlineData("mods/../..")]
    public void APathThatEscapesTheInstanceIsRefused(string path)
        => Assert.ThrowsAny<LauncherException>(() => Parse(Manifest($"""[{File(path)}]""")));

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/x.dll")]
    public void AnAbsolutePathIsRefused(string path)
        => Assert.ThrowsAny<LauncherException>(() => Parse(Manifest($"""[{File(path)}]""")));

    /// <summary>Backslash escapes do not get a free pass — they are normalised before the check.</summary>
    [Fact]
    public void AnEscapeSpelledWithBackslashesIsAlsoRefused()
        => Assert.ThrowsAny<LauncherException>(() => Parse(Manifest($"""[{File(@"..\\..\\outside.jar")}]""")));

    /*
     * Convert.FromHexString throws a bare FormatException, which nothing here catches -- so a
     * hand-edited or hostile pack would escape the parser as a raw BCL error rather than "this pack
     * is malformed". Every other parse failure in this codebase is a LauncherException, and a caller
     * that handles one handles all of them. Found by a round-trip test using an odd-length fixture.
     */
    [Theory]
    [InlineData("abc")]
    [InlineData("not-hex-at-all")]
    [InlineData("")]
    public void AMalformedHashFailsAsAPackErrorNotABclError(string hash)
    {
        var json = Manifest($$"""
            [{ "path": "mods/a.jar", "hashes": { "sha512": "{{hash}}" },
               "downloads": ["https://cdn.modrinth.com/a.jar"] }]
            """);

        var error = Assert.ThrowsAny<LauncherException>(() => Parse(json));

        Assert.Contains("mods/a.jar", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPathIsRefused()
        => Assert.ThrowsAny<LauncherException>(() => Parse(Manifest($"""[{File("")}]""")));

    /// <summary>Legitimate nesting still works — the guard is about escaping, not about depth.</summary>
    [Theory]
    [InlineData("mods/a.jar")]
    [InlineData("config/some/deep/path/x.toml")]
    [InlineData("mods/sub/../a.jar")]
    public void AContainedPathIsAccepted(string path)
        => ModrinthPack.ValidateRelativePath(path);

    // ================================================================== updating

    /*
     * Matched on HASH, not path: a mod that moved folders is the same file and needs no fetching, and
     * a mod at the same path with different contents does. Path matching gets both backwards.
     */
    [Fact]
    public void AnUnchangedFileIsNeitherDownloadedAgainNorDeleted()
    {
        var current = Parse(Manifest($"""[{File("mods/moved/a.jar")}]"""));
        var previous = Parse(Manifest($"""[{File("mods/a.jar")}]"""));

        var stale = ModrinthPack.RemoveUnchanged(current, previous);

        Assert.Empty(current.Files);
        Assert.Empty(stale);
    }

    /// <summary>What the new version dropped has to be deleted, or the instance accumulates mods.</summary>
    [Fact]
    public void AFileTheNewVersionDroppedIsScheduledForRemoval()
    {
        const string OtherHash = "0000000000000000000000000000000000000000000000000000000000000000"
            + "0000000000000000000000000000000000000000000000000000000000000001";

        var current = Parse(Manifest($"""[{File("mods/kept.jar")}]"""));
        var previous = ModrinthPack.Parse(Encoding.UTF8.GetBytes(Manifest($$"""
            [{{File("mods/kept.jar")}},
             { "path": "mods/gone.jar", "hashes": { "sha512": "{{OtherHash}}" },
               "downloads": ["https://cdn.modrinth.com/gone.jar"] }]
            """)));

        var stale = ModrinthPack.RemoveUnchanged(current, previous);

        Assert.Empty(current.Files);
        Assert.Equal(["mods/gone.jar"], stale);
    }

    [Fact]
    public void AGenuinelyNewFileIsStillDownloaded()
    {
        const string NewHash = "1111111111111111111111111111111111111111111111111111111111111111"
            + "1111111111111111111111111111111111111111111111111111111111111111";

        var current = ModrinthPack.Parse(Encoding.UTF8.GetBytes(Manifest($$"""
            [{{File("mods/kept.jar")}},
             { "path": "mods/new.jar", "hashes": { "sha512": "{{NewHash}}" },
               "downloads": ["https://cdn.modrinth.com/new.jar"] }]
            """)));

        var previous = Parse(Manifest($"""[{File("mods/kept.jar")}]"""));

        var stale = ModrinthPack.RemoveUnchanged(current, previous);

        Assert.Equal("mods/new.jar", Assert.Single(current.Files).Path);
        Assert.Empty(stale);
    }

    /// <summary>The pack's own order survives the filtering.</summary>
    [Fact]
    public void TheRemainingFilesKeepThePacksOrder()
    {
        static string HashOf(int n) => new string((char)('0' + n), 128);

        var current = ModrinthPack.Parse(Encoding.UTF8.GetBytes(Manifest($$"""
            [{ "path": "mods/1.jar", "hashes": { "sha512": "{{HashOf(1)}}" }, "downloads": ["https://a.invalid/1"] },
             { "path": "mods/2.jar", "hashes": { "sha512": "{{HashOf(2)}}" }, "downloads": ["https://a.invalid/2"] },
             { "path": "mods/3.jar", "hashes": { "sha512": "{{HashOf(3)}}" }, "downloads": ["https://a.invalid/3"] }]
            """)));

        var previous = ModrinthPack.Parse(Encoding.UTF8.GetBytes(Manifest($$"""
            [{ "path": "mods/2.jar", "hashes": { "sha512": "{{HashOf(2)}}" }, "downloads": ["https://a.invalid/2"] }]
            """)));

        ModrinthPack.RemoveUnchanged(current, previous);

        Assert.Equal(["mods/1.jar", "mods/3.jar"], current.Files.Select(f => f.Path));
    }

    /// <summary>Two copies of one file cancel two old copies, not all of them.</summary>
    [Fact]
    public void DuplicateFilesCancelOneForOne()
    {
        var current = Parse(Manifest($"""[{File("mods/a.jar")}, {File("mods/b.jar")}]"""));
        var previous = Parse(Manifest($"""[{File("mods/a.jar")}]"""));

        var stale = ModrinthPack.RemoveUnchanged(current, previous);

        // One of the two identical files still needs fetching.
        Assert.Single(current.Files);
        Assert.Empty(stale);
    }
}
