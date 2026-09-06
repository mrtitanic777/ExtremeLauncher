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
 * Characterization tests for command-line construction. There is no upstream Qt test for this, and it
 * is the last transformation before a process starts, so the pieces are pinned individually.
 */

using ExtremeLauncher.Java;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class LaunchCommandBuilderTests
{
    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };

    private static LaunchOptions Options(Action<LaunchProfile>? _ = null) => new()
    {
        InstanceName = "My Instance",
        GameDirectory = Path.Combine(Path.GetTempPath(), "instance", ".minecraft"),
        AssetsDirectory = Path.Combine(Path.GetTempPath(), "assets"),
        JavaVersion = new JavaVersion("17.0.1"),
    };

    private static LaunchProfile VanillaProfile()
    {
        var profile = new LaunchProfile();

        var patch = new VersionFile
        {
            Uid = "net.minecraft",
            Version = "1.20.1",
            Type = "release",
            MainClass = "net.minecraft.client.main.Main",
            MinecraftArguments =
                "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} "
                + "--assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} "
                + "--accessToken ${auth_access_token} --userType ${user_type} --versionType ${version_type}",
            MojangAssetIndex = new MojangAssetIndexInfo { Id = "5" },
            MainJar = new Library("com.mojang:minecraft:1.20.1:client"),
        };

        profile.Apply(patch, Context());
        return profile;
    }

    private static AuthSession Session() => new()
    {
        PlayerName = "Steve",
        Uuid = "069a79f4-44e9-4726-a5be-fca90e38aaf5",
        AccessToken = "secret-token",
        UserType = "msa",
    };

    // ================================================================== token substitution

    [Fact]
    public void KnownTokensAreSubstituted()
        => Assert.Equal(
            "hello world",
            LaunchCommandBuilder.ReplaceTokens("hello ${who}", new Dictionary<string, string> { ["who"] = "world" }));

    [Fact]
    public void AnUnknownTokenExpandsToNothing()
    {
        // QUIRK, preserved: it is dropped, not left in place. Old version files reference arguments
        // this launcher never supplies, and they still have to run.
        Assert.Equal("hello ", LaunchCommandBuilder.ReplaceTokens("hello ${nobody}", new Dictionary<string, string>()));
    }

    [Fact]
    public void MultipleTokensInOneStringAreEachHandled()
        => Assert.Equal(
            "a-b",
            LaunchCommandBuilder.ReplaceTokens(
                "${x}-${y}",
                new Dictionary<string, string> { ["x"] = "a", ["y"] = "b" }));

    // ================================================================== game arguments

    [Fact]
    public void GameArgumentsExpandTheProfileTemplate()
    {
        var args = LaunchCommandBuilder.BuildGameArguments(VanillaProfile(), Options(), Session());

        Assert.Equal("Steve", args[Array.IndexOf([.. args], "--username") + 1]);
        Assert.Equal("1.20.1", args[Array.IndexOf([.. args], "--version") + 1]);
        Assert.Equal("5", args[Array.IndexOf([.. args], "--assetIndex") + 1]);
        Assert.Equal("secret-token", args[Array.IndexOf([.. args], "--accessToken") + 1]);
        Assert.Equal("release", args[Array.IndexOf([.. args], "--versionType") + 1]);
    }

    [Fact]
    public void WithNoSessionTheAuthTokensGoBlankRatherThanLeakingPlaceholders()
    {
        var args = LaunchCommandBuilder.BuildGameArguments(VanillaProfile(), Options());

        // Every ${...} is gone, even without an account to fill them in.
        Assert.DoesNotContain(args, a => a.Contains("${", StringComparison.Ordinal));
    }

    [Fact]
    public void TweakersAreAppendedInLoadOrder()
    {
        var profile = VanillaProfile();

        var forge = new VersionFile { Uid = "net.minecraftforge", Version = "1" };
        forge.AddTweakers.Add("FirstTweaker");
        forge.AddTweakers.Add("SecondTweaker");
        profile.Apply(forge, Context());

        var args = LaunchCommandBuilder.BuildGameArguments(profile, Options(), Session());

        var first = Array.IndexOf([.. args], "FirstTweaker");
        var second = Array.IndexOf([.. args], "SecondTweaker");

        Assert.True(first > 0);
        Assert.True(second > first);
    }

    [Fact]
    public void DemoSessionsGetTheDemoFlag()
    {
        var args = LaunchCommandBuilder.BuildGameArguments(
            VanillaProfile(),
            Options(),
            new AuthSession { PlayerName = "Steve", Uuid = "x", AccessToken = "y", Demo = true });

        Assert.Contains("--demo", args);
    }

    [Fact]
    public void AServerTargetUsesTheLegacySpellingWithoutQuickPlay()
    {
        var args = LaunchCommandBuilder.BuildGameArguments(
            VanillaProfile(),
            Options(),
            Session(),
            new LaunchTarget { Address = "mc.example.com", Port = 25566 });

        Assert.Contains("--server", args);
        Assert.Contains("mc.example.com", args);
        Assert.Contains("25566", args);
        Assert.DoesNotContain("--quickPlayMultiplayer", args);
    }

    [Fact]
    public void AServerTargetUsesQuickPlayWhenTheProfileSupportsIt()
    {
        var profile = VanillaProfile();

        var patch = new VersionFile { Uid = "net.minecraft.quickplay", Version = "1" };
        patch.Traits.Add("feature:is_quick_play_multiplayer");
        profile.Apply(patch, Context());

        var args = LaunchCommandBuilder.BuildGameArguments(
            profile,
            Options(),
            Session(),
            new LaunchTarget { Address = "mc.example.com", Port = 25566 });

        Assert.Contains("--quickPlayMultiplayer", args);
        Assert.Contains("mc.example.com:25566", args);
        Assert.DoesNotContain("--server", args);
    }

    // ================================================================== choosing between server and world

    [Fact]
    public void AServerWinsOverAWorldWhenBothAreGiven()
    {
        /*
         * The game takes one --quickPlay target, so a choice is forced. Upstream (Application.cpp)
         * picks the server; the port must too. Fails if Choose is reordered to prefer the world.
         */
        var target = LaunchTarget.Choose("play.example.net:25566", "New World");

        Assert.NotNull(target);
        Assert.Equal("play.example.net", target!.Address);
        Assert.Equal(25566, target.Port);
        Assert.Equal(string.Empty, target.World);
    }

    [Fact]
    public void AWorldIsChosenWhenThereIsNoServer()
    {
        var target = LaunchTarget.Choose(server: null, world: "New World");

        Assert.NotNull(target);
        Assert.Equal("New World", target!.World);
        Assert.Equal(string.Empty, target.Address);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void NeitherAServerNorAWorldMeansNoTarget(string? server, string? world)
    {
        // An empty string is "not set", not "join a server called empty".
        Assert.Null(LaunchTarget.Choose(server, world));
    }

    [Fact]
    public void ASingleplayerTargetNeedsTheMatchingTrait()
    {
        var target = new LaunchTarget { World = "My World" };

        // Without the trait, the request is ignored rather than passed to a game that cannot use it.
        Assert.DoesNotContain("--quickPlaySingleplayer",
            LaunchCommandBuilder.BuildGameArguments(VanillaProfile(), Options(), Session(), target));

        var profile = VanillaProfile();
        var patch = new VersionFile { Uid = "qp", Version = "1" };
        patch.Traits.Add("feature:is_quick_play_singleplayer");
        profile.Apply(patch, Context());

        Assert.Contains("--quickPlaySingleplayer",
            LaunchCommandBuilder.BuildGameArguments(profile, Options(), Session(), target));
    }

    // ================================================================== jvm arguments

    [Fact]
    public void MemoryFlagsAreEmittedInOrder()
    {
        var options = new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            MinMemoryMegabytes = 512,
            MaxMemoryMegabytes = 4096,
            JavaVersion = new JavaVersion("17"),
        };

        var args = LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), options);

        Assert.Contains("-Xms512m", args);
        Assert.Contains("-Xmx4096m", args);
    }

    [Fact]
    public void BackwardsMemorySettingsAreSwappedRatherThanPassedThrough()
    {
        var options = new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            MinMemoryMegabytes = 4096,
            MaxMemoryMegabytes = 512,
            JavaVersion = new JavaVersion("17"),
        };

        var args = LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), options);

        // Handing the JVM -Xms4096m -Xmx512m would simply fail to start.
        Assert.Contains("-Xms512m", args);
        Assert.Contains("-Xmx4096m", args);
    }

    [Fact]
    public void PermGenIsOnlyForOldJava()
    {
        var modern = LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            PermGenMegabytes = 128,
            JavaVersion = new JavaVersion("17"),
        });

        Assert.DoesNotContain(modern, a => a.StartsWith("-XX:PermSize", StringComparison.Ordinal));

        var legacy = LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            PermGenMegabytes = 128,
            JavaVersion = new JavaVersion("1.7.0_80"),
        });

        Assert.Contains("-XX:PermSize=128m", legacy);
    }

    [Fact]
    public void TheDefaultPermGenSizeIsNotEmitted()
    {
        var args = LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            PermGenMegabytes = 64,
            JavaVersion = new JavaVersion("1.7.0_80"),
        });

        Assert.DoesNotContain(args, a => a.StartsWith("-XX:PermSize", StringComparison.Ordinal));
    }

    [Fact]
    public void JarModsBringTheForgeCertificateWorkarounds()
    {
        var profile = VanillaProfile();

        var patch = new VersionFile { Uid = "jarmods", Version = "1" };
        patch.JarMods.Add(new Library("org.multimc.jarmods:something:1"));
        profile.Apply(patch, Context());

        var args = LaunchCommandBuilder.BuildJvmArguments(profile, Options());

        // A modded jar no longer matches Mojang's signatures; Forge refuses to start without these.
        Assert.Contains("-Dfml.ignoreInvalidMinecraftCertificates=true", args);
        Assert.Contains("-Dfml.ignorePatchDiscrepancies=true", args);
    }

    [Fact]
    public void CustomArgumentsComeFirstSoLaterOnesCanOverrideThem()
    {
        var options = new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            CustomJvmArguments = ["-Dcustom=1"],
            JavaVersion = new JavaVersion("17"),
        };

        var args = LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), options);

        Assert.Equal("-Dcustom=1", args[0]);
    }

    [Fact]
    public void ProfileJvmArgumentsAreIncluded()
    {
        var profile = VanillaProfile();

        var patch = new VersionFile { Uid = "loader", Version = "1" };
        patch.AddnJvmArguments.Add("-Xss1M");
        profile.Apply(patch, Context());

        Assert.Contains("-Xss1M", LaunchCommandBuilder.BuildJvmArguments(profile, Options()));
    }

    [Fact]
    public void OnlineFixesOnlyApplyToModularJava()
    {
        var modular = LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            JavaVersion = new JavaVersion("17"),
            ApplyOnlineFixes = true,
        });

        Assert.Contains("--add-opens", modular);
        Assert.Contains("java.base/java.net=ALL-UNNAMED", modular);

        // Java 8 has no module system, so the flag would be rejected.
        var legacy = LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            JavaVersion = new JavaVersion("1.8.0_302"),
            ApplyOnlineFixes = true,
        });

        Assert.DoesNotContain("--add-opens", legacy);
    }

    [Fact]
    public void AMissingNativeLibraryOverrideIsIgnored()
    {
        var options = new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            JavaVersion = new JavaVersion("17"),
            NativeOpenAlPath = Path.Combine(Path.GetTempPath(), "definitely-not-here.so"),
        };

        // A stale setting must not break the launch.
        Assert.DoesNotContain(
            LaunchCommandBuilder.BuildJvmArguments(VanillaProfile(), options),
            a => a.StartsWith("-Dorg.lwjgl.openal", StringComparison.Ordinal));
    }

    // ================================================================== the whole line

    [Fact]
    public void TheFullCommandLineIsOrderedCorrectly()
    {
        var profile = VanillaProfile();

        var patch = new VersionFile { Uid = "libs", Version = "1" };
        patch.Libraries.Add(new Library("com.google.guava:guava:31.1-jre"));
        profile.Apply(patch, Context());

        var line = LaunchCommandBuilder.BuildCommandLine(profile, Context(), Options(), Session());

        var classpathIndex = line.IndexOf("-cp");
        var mainClassIndex = line.IndexOf("net.minecraft.client.main.Main");

        // JVM flags, then -cp, then the main class, then the game's own arguments.
        Assert.True(classpathIndex > 0);
        Assert.Equal(classpathIndex + 2, mainClassIndex);
        Assert.True(line.IndexOf("--username") > mainClassIndex);

        // Everything on the classpath is separated the way this platform expects.
        Assert.Contains(Path.PathSeparator.ToString(), line[classpathIndex + 1]);
    }

    [Fact]
    public void TheNativesDirectoryBecomesTheLibraryPath()
    {
        var options = new LaunchOptions
        {
            InstanceName = "x",
            GameDirectory = ".",
            AssetsDirectory = ".",
            NativesDirectory = Path.Combine(Path.GetTempPath(), "natives"),
            JavaVersion = new JavaVersion("17"),
        };

        Assert.Contains(
            LaunchCommandBuilder.BuildCommandLine(VanillaProfile(), Context(), options, Session()),
            a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal));
    }

    // ============================================================ the server address (MinecraftTarget)

    /*
     * These are read off upstream's MinecraftTarget::parse, not off my own implementation. That
     * distinction matters here: the first version of this parser split on the LAST colon, which looks
     * right for "host:port" and quietly mangles every IPv6 address and every junk value. Upstream
     * splits on ALL colons and gives up when there is more than one.
     */
    [Theory]
    [InlineData("example.com", "example.com", 25565)]
    [InlineData("example.com:25566", "example.com", 25566)]
    [InlineData("192.168.1.10:1234", "192.168.1.10", 1234)]
    public void AServerAddressSplitsIntoHostAndPort(string input, string address, int port)
    {
        var target = LaunchTarget.Parse(input);

        Assert.Equal(address, target.Address);
        Assert.Equal(port, target.Port);
        Assert.Equal(string.Empty, target.World);
    }

    /// <summary>A bracketed IPv6 address loses its brackets and keeps its own colons.</summary>
    [Theory]
    [InlineData("[::1]:25566", "::1", 25566)]
    [InlineData("[::1]", "::1", 25565)]
    [InlineData("[fe80::1ff:fe23:4567:890a]:25570", "fe80::1ff:fe23:4567:890a", 25570)]
    public void ABracketedIpv6AddressIsUnwrapped(string input, string address, int port)
    {
        var target = LaunchTarget.Parse(input);

        Assert.Equal(address, target.Address);
        Assert.Equal(port, target.Port);
    }

    /*
     * A BARE IPv6 ADDRESS IS TAKEN WHOLE. This is the case the last-colon version got wrong: "::1"
     * would have become the host ":" on port 1, which resolves to nothing and reports no error.
     */
    [Fact]
    public void ABareIpv6AddressKeepsAllOfItself()
    {
        var target = LaunchTarget.Parse("::1");

        Assert.Equal("::1", target.Address);
        Assert.Equal(25565, target.Port);
    }

    /// <summary>An unparseable port defaults, and the address keeps only the part before the colon.</summary>
    [Fact]
    public void ANonNumericPortFallsBackToTheDefault()
    {
        var target = LaunchTarget.Parse("example.com:notaport");

        Assert.Equal("example.com", target.Address);
        Assert.Equal(25565, target.Port);
    }

    /*
     * UPSTREAM QUIRK, deliberately preserved: the port is parsed as 32 bits and then truncated into a
     * quint16, so this is port 4464 rather than a rejected address. Recorded because it is the kind of
     * thing a "tidy-up" would silently change.
     */
    [Fact]
    public void AnOversizedPortWrapsAsUpstreamDoes()
    {
        Assert.Equal(70000 % 65536, LaunchTarget.Parse("example.com:70000").Port);
    }

    [Fact]
    public void AWorldIsTakenWhole()
    {
        var target = LaunchTarget.Parse("My World: The Sequel", useWorld: true);

        Assert.Equal("My World: The Sequel", target.World);
        Assert.Equal(string.Empty, target.Address);
    }
}
