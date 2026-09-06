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
 * An ATLauncher manifest is closer to an install program than a file list, so the tests are about the
 * decisions it encodes: what the user is offered, what starts ticked, what gets pulled in with it,
 * and what a mod type actually instructs the launcher to do with a download.
 */

using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ModPlatform.Tests;

public sealed class AtlPackManifestTests
{
    private static JsonObject Parse(string json)
        => Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes(json), "test"));

    private static AtlVersionMod Mod(string extra = "", string name = "Some Mod", string type = "mods")
        => AtlPackManifest.LoadVersionMod(Parse($$"""
            {
              "name": "{{name}}", "version": "1.0", "url": "https://atl.invalid/a.jar",
              "file": "a.jar", "download": "direct", "type": "{{type}}" {{extra}}
            }
            """));

    // ================================================================== types

    [Theory]
    [InlineData("server", AtlDownloadType.Server)]
    [InlineData("browser", AtlDownloadType.Browser)]
    [InlineData("direct", AtlDownloadType.Direct)]
    [InlineData("ftp", AtlDownloadType.Unknown)]
    public void DownloadTypesAreParsed(string raw, AtlDownloadType expected)
        => Assert.Equal(expected, AtlPackManifest.ParseDownloadType(raw));

    /// <summary>A delivery instruction, not a category — each says what to do with the download.</summary>
    [Theory]
    [InlineData("mods", AtlModType.Mods)]
    [InlineData("jar", AtlModType.Jar)]
    [InlineData("forge", AtlModType.Forge)]
    [InlineData("extract", AtlModType.Extract)]
    [InlineData("decomp", AtlModType.Decomp)]
    [InlineData("resourcepack", AtlModType.ResourcePack)]
    [InlineData("millenaire", AtlModType.Millenaire)]
    [InlineData("somethingelse", AtlModType.Unknown)]
    public void ModTypesAreParsed(string raw, AtlModType expected)
        => Assert.Equal(expected, AtlPackManifest.ParseModType(raw));

    /*
     * ATLauncher shipped the misspelling, packs were published with it, and it has to be accepted
     * forever. Removing it would break real packs that are still installable today.
     */
    [Fact]
    public void TheMisspelledDependencyTypeIsStillAccepted()
    {
        Assert.Equal(AtlModType.Dependency, AtlPackManifest.ParseModType("dependency"));
        Assert.Equal(AtlModType.Dependency, AtlPackManifest.ParseModType("depandency"));
    }

    // ================================================================== loaders

    /*
     * EACH LOADER HIDES ITS VERSION UNDER A DIFFERENT KEY, so the type has to be read first and the
     * version looked up by it.
     */
    [Fact]
    public void ForgeKeepsItsVersionUnderVersionAndFabricUnderLoader()
    {
        var forge = AtlPackManifest.LoadVersionLoader(Parse("""
            { "type": "forge", "metadata": { "version": "47.2.0", "recommended": true } }
            """));

        var fabric = AtlPackManifest.LoadVersionLoader(Parse("""
            { "type": "fabric", "metadata": { "loader": "0.15.0", "latest": true } }
            """));

        Assert.Equal("47.2.0", forge.Version);
        Assert.True(forge.Recommended);

        Assert.Equal("0.15.0", fabric.Version);
        Assert.True(fabric.Latest);
    }

    /// <summary>An unknown loader leaves the version empty — the pack may still install as vanilla.</summary>
    [Fact]
    public void AnUnknownLoaderTypeYieldsNoVersion()
    {
        var loader = AtlPackManifest.LoadVersionLoader(Parse("""
            { "type": "rift", "metadata": { "version": "1.0" } }
            """));

        Assert.Equal("rift", loader.Type);
        Assert.Equal(string.Empty, loader.Version);
    }

    [Fact]
    public void LoaderFlagsDefaultToFalse()
    {
        var loader = AtlPackManifest.LoadVersionLoader(Parse("""{ "type": "forge", "metadata": {} }"""));

        Assert.False(loader.Latest);
        Assert.False(loader.Recommended);
        Assert.False(loader.Choose);
    }

    // ================================================================== mods

    [Fact]
    public void AModEntryIsRead()
    {
        var mod = Mod(""", "md5": "abc", "description": "Does things", "group": "worldgen" """);

        Assert.Equal("Some Mod", mod.Name);
        Assert.Equal("abc", mod.Md5);
        Assert.Equal(AtlDownloadType.Direct, mod.Download);
        Assert.Equal(AtlModType.Mods, mod.Type);
        Assert.Equal("worldgen", mod.Group);
    }

    /*
     * A NAME-BASED CORRECTION, and upstream explains why it exists: Forge detection relies on the
     * type being "forge", but some packs use "jar" for Forge itself. Without this the pack installs
     * with no Forge component and every mod fails to load.
     */
    [Fact]
    public void AJarTypedMinecraftForgeIsCorrectedToForge()
    {
        var mod = Mod(name: "Minecraft Forge", type: "jar");

        Assert.Equal(AtlModType.Forge, mod.Type);
        Assert.Equal("forge", mod.TypeRaw);
    }

    /// <summary>The correction is exactly that name and exactly that type, not a general rule.</summary>
    [Theory]
    [InlineData("Minecraft Forge", "mods", AtlModType.Mods)]
    [InlineData("Some Other Mod", "jar", AtlModType.Jar)]
    [InlineData("minecraft forge", "jar", AtlModType.Jar)]
    public void TheForgeCorrectionIsNarrow(string name, string type, AtlModType expected)
        => Assert.Equal(expected, Mod(name: name, type: type).Type);

    /// <summary>%s% is the placeholder for a separator, since the field is hand-written into JSON.</summary>
    [Fact]
    public void ExtractFolderSeparatorsArePlaceholders()
    {
        var mod = Mod(""", "extractTo": "mods", "extractFolder": "%s%config%s%thing" """);

        Assert.Equal(AtlModType.Mods, mod.ExtractTo);
        Assert.Equal("/config/thing", mod.ExtractFolder);
    }

    [Fact]
    public void ADecompModNamesTheFileToTakeOut()
    {
        var mod = Mod(""", "decompType": "jar", "decompFile": "inner.jar" """);

        Assert.Equal(AtlModType.Jar, mod.DecompType);
        Assert.Equal("inner.jar", mod.DecompFile);
    }

    /// <summary>Blocks absent means the mod is delivered as it is.</summary>
    [Fact]
    public void AModWithNoExtractOrDecompBlockHasNeither()
    {
        var mod = Mod();

        Assert.Equal(AtlModType.Unknown, mod.ExtractTo);
        Assert.Equal(AtlModType.Unknown, mod.DecompType);
        Assert.Equal(string.Empty, mod.ExtractFolder);
    }

    [Fact]
    public void DependenciesAreNamedByModName()
    {
        var mod = Mod(""", "depends": ["Library A", "Library B"] """);

        Assert.Equal(["Library A", "Library B"], mod.Depends);
    }

    /*
     * A library is not marked hidden but has no business in a mod chooser either -- the user cannot
     * make a meaningful decision about IC2's shim jar. Computed rather than read.
     */
    [Theory]
    [InlineData("", false)]
    [InlineData(""", "hidden": true """, true)]
    [InlineData(""", "library": true """, true)]
    public void EffectivelyHiddenCoversLibrariesAsWellAsHiddenMods(string extra, bool expected)
        => Assert.Equal(expected, Mod(extra).EffectivelyHidden);

    // ================================================================== selection

    /*
     * "The user may decline this" and "it starts ticked" are different statements, and a pack can
     * make either without the other.
     */
    [Fact]
    public void ARequiredModIsAlwaysSelectedAndAnOptionalOneOnlyIfMarked()
    {
        var required = Mod(name: "Required");
        var optionalOn = Mod(""", "optional": true, "selected": true """, name: "Opt In");
        var optionalOff = Mod(""", "optional": true """, name: "Opt Out");

        var selection = AtlPackManifest.GetDefaultSelection([required, optionalOn, optionalOff]);

        Assert.Equal(["Required", "Opt In"], selection.Select(m => m.Name));
    }

    /// <summary>Recommended is advice to the user, not a selection.</summary>
    [Fact]
    public void RecommendedAloneDoesNotSelectAMod()
    {
        var mod = Mod(""", "optional": true, "recommended": true """);

        Assert.True(mod.Recommended);
        Assert.Empty(AtlPackManifest.GetDefaultSelection([mod]));
    }

    // ================================================================== dependencies

    [Fact]
    public void SelectingAModPullsInWhatItDependsOn()
    {
        var library = Mod(name: "Library A");
        var mod = Mod(""", "depends": ["Library A"] """, name: "Big Mod");

        var resolved = AtlPackManifest.ResolveDependencies([library, mod], [mod]);

        Assert.Equal(["Library A", "Big Mod"], resolved.Select(m => m.Name));
    }

    [Fact]
    public void DependenciesAreFollowedTransitively()
    {
        var deep = Mod(name: "Deep");
        var middle = Mod(""", "depends": ["Deep"] """, name: "Middle");
        var top = Mod(""", "depends": ["Middle"] """, name: "Top");

        var resolved = AtlPackManifest.ResolveDependencies([deep, middle, top], [top]);

        Assert.Equal(["Deep", "Middle", "Top"], resolved.Select(m => m.Name));
    }

    /// <summary>Packs list dependencies on mods they no longer ship.</summary>
    [Fact]
    public void ADependencyOnAModThatIsNotThereIsIgnored()
    {
        var mod = Mod(""", "depends": ["Gone"] """, name: "Big Mod");

        Assert.Equal(["Big Mod"], AtlPackManifest.ResolveDependencies([mod], [mod]).Select(m => m.Name));
    }

    /// <summary>Nothing in the format forbids a cycle, so resolution has to survive one.</summary>
    [Fact]
    public void ADependencyCycleTerminates()
    {
        var a = Mod(""", "depends": ["B"] """, name: "A");
        var b = Mod(""", "depends": ["A"] """, name: "B");

        Assert.Equal(["A", "B"], AtlPackManifest.ResolveDependencies([a, b], [a]).Select(m => m.Name));
    }

    /// <summary>The pack's own order, so the install list does not depend on traversal order.</summary>
    [Fact]
    public void ResolvedModsComeBackInThePacksOrder()
    {
        var first = Mod(name: "First");
        var second = Mod(""", "depends": ["First"] """, name: "Second");
        var third = Mod(""", "depends": ["Second"] """, name: "Third");

        var resolved = AtlPackManifest.ResolveDependencies([first, second, third], [third]);

        Assert.Equal(["First", "Second", "Third"], resolved.Select(m => m.Name));
    }

    [Fact]
    public void SelectingNothingResolvesToNothing()
        => Assert.Empty(AtlPackManifest.ResolveDependencies([Mod()], []));

    // ================================================================== libraries and configs

    [Fact]
    public void ALibraryIsRead()
    {
        var library = AtlPackManifest.LoadVersionLibrary(Parse("""
            { "url": "https://atl.invalid/l.jar", "file": "l.jar", "md5": "abc",
              "download": "server", "server": "yes" }
            """));

        Assert.Equal(AtlDownloadType.Server, library.Download);
        Assert.Equal("yes", library.Server);
    }

    [Fact]
    public void ConfigsCarryTheirSizeAndHash()
    {
        var configs = AtlPackManifest.LoadVersionConfigs(Parse("""{ "filesize": 4096, "sha1": "abc" }"""));

        Assert.Equal(4096, configs.FileSize);
        Assert.Equal("abc", configs.Sha1);
    }

    [Fact]
    public void AModMissingARequiredFieldIsRefused()
        => Assert.ThrowsAny<LauncherException>(
            () => AtlPackManifest.LoadVersionMod(Parse("""{ "name": "n", "version": "1" }""")));
}
