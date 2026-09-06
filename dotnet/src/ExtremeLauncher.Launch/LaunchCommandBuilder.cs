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
 * Ported from MinecraftInstance::javaArguments, ::extraArguments, ::processMinecraftArgs and
 * ::replaceTokensIn in launcher/minecraft/MinecraftInstance.cpp.
 *
 * Turns a resolved LaunchProfile into the two argument lists a JVM needs: the flags before -cp, and
 * the game's own arguments after the main class. This is the last transformation before a process
 * actually starts.
 *
 * EXTRACTED FROM MinecraftInstance, deliberately. Upstream builds the command line as methods on a
 * 1,256-line class that also owns settings, paths, logging, the mod folders and the window title.
 * Everything the construction actually needs is passed in here instead, which is what makes it
 * possible to assert on a command line without standing up an instance.
 *
 * GAME ARGUMENTS ARE A TEMPLATE. The profile carries a pattern like
 *   "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory}"
 * and the tokens are substituted from a map. An UNKNOWN token expands to nothing rather than being
 * left in place -- see ReplaceTokens.
 */

using System.Globalization;
using System.Text.RegularExpressions;
using ExtremeLauncher.Java;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Minecraft.Auth;

namespace ExtremeLauncher.Launch;

/// <summary>Everything the command line needs that does not come from the profile.</summary>
public sealed class LaunchOptions
{
    public required string InstanceName { get; init; }

    /// <summary>The instance's .minecraft folder.</summary>
    public required string GameDirectory { get; init; }

    /// <summary>Where the shared asset store lives.</summary>
    public required string AssetsDirectory { get; init; }

    /// <summary>Where the per-version assets for this profile were laid out.</summary>
    public string GameAssetsDirectory { get; init; } = string.Empty;

    /// <summary>Where extracted natives were placed.</summary>
    public string NativesDirectory { get; init; } = string.Empty;

    public string LocalLibraryPath { get; init; } = string.Empty;

    public int MinMemoryMegabytes { get; init; } = 512;

    public int MaxMemoryMegabytes { get; init; } = 4096;

    /// <summary>PermGen size in MiB. Only emitted for Java below 8, and only when not the default 64.</summary>
    public int PermGenMegabytes { get; init; } = 64;

    /// <summary>User-supplied JVM arguments, applied first so later ones can override them.</summary>
    public IReadOnlyList<string> CustomJvmArguments { get; init; } = [];

    public JavaVersion JavaVersion { get; init; } = new();

    /// <summary>Allows reflective access to java.net, which the skin fix needs on modular Java.</summary>
    public bool ApplyOnlineFixes { get; init; }

    public string? NativeOpenAlPath { get; init; }

    public string? NativeGlfwPath { get; init; }
}

/*
 * The account details substituted into the game arguments used to be a placeholder type declared here,
 * because the auth subsystem had not been ported yet. It now comes from AuthSession, which is what
 * upstream's argument builder reads directly.
 */

/// <summary>A server or world to join straight from launch.</summary>
public sealed class LaunchTarget
{
    public const int DefaultPort = 25565;

    public string Address { get; init; } = string.Empty;

    public int Port { get; init; } = DefaultPort;

    public string World { get; init; } = string.Empty;

    /// <summary>Chooses the one target to join from a server address and a world name, either or both
    /// of which may be absent.</summary>
    /// <remarks>
    /// SERVER WINS when both are given. The game takes a single <c>--quickPlay</c> target, so a choice
    /// has to be made, and upstream makes it server-first: Application.cpp is
    /// <c>if (!m_serverToJoin.isEmpty()) ... else if (!m_worldToJoin.isEmpty())</c>. A single home for
    /// the rule so the two front-ends (CLI and window) cannot drift on which one wins.
    /// </remarks>
    public static LaunchTarget? Choose(string? server, string? world)
        => server is { Length: > 0 }
            ? Parse(server)
            : world is { Length: > 0 }
                ? Parse(world, useWorld: true)
                : null;

    /// <summary>
    /// Parses a <c>--server</c> address, or takes a world name whole.
    /// </summary>
    /// <remarks>
    /// Ported from MinecraftTarget::parse, which is MultiMC-origin and Apache-2.0; the surrounding
    /// file stays GPL-3.0, which Apache-2.0 permits in this direction.
    ///
    /// IT VALIDATES NOTHING, deliberately, and upstream says as much in a FIXME. The point is to
    /// accept exactly what the game accepts: an address the launcher rejected but Minecraft would
    /// have taken is a worse outcome than one that is passed along and fails in the game, where the
    /// user can see it.
    /// </remarks>
    public static LaunchTarget Parse(string fullAddress, bool useWorld = false)
    {
        ArgumentNullException.ThrowIfNull(fullAddress);

        if (useWorld)
        {
            return new LaunchTarget { World = fullAddress };
        }

        var split = fullAddress.Split(':');

        // "[::1]:25565" -- the brackets come off and the address keeps its own colons.
        if (fullAddress.StartsWith('['))
        {
            var bracket = fullAddress.IndexOf(']', StringComparison.Ordinal);

            if (bracket > 0)
            {
                var ipv6 = fullAddress[1..bracket];
                var port = fullAddress[(bracket + 1)..].Trim();

                split = port.StartsWith(':') && ipv6.Length != 0
                    ? [ipv6, port[1..]]
                    : [ipv6];
            }
        }

        /*
         * More than one colon and no brackets: taken whole rather than guessed at. A bare IPv6 address
         * lands here, which is why splitting on the LAST colon would be wrong -- it would turn "::1"
         * into the host ":" on port 1.
         */
        if (split.Length > 2)
        {
            split = [fullAddress];
        }

        var address = split[0];
        var realPort = DefaultPort;

        if (split.Length > 1)
        {
            /*
             * QUIRK, preserved: parsed as a 32-bit value and then TRUNCATED to 16 bits, because
             * upstream assigns an unsigned int into a quint16. So "example.com:70000" is port 4464,
             * not a rejected address. Kept because a silently different port is worse than a strange
             * one, and because nothing upstream validates this either.
             */
            realPort = uint.TryParse(
                split[1],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? (ushort)parsed
                : DefaultPort;
        }

        return new LaunchTarget { Address = address, Port = realPort };
    }
}

public static partial class LaunchCommandBuilder
{
    /// <remarks>InvertedGreediness in Qt; non-greedy here. Same effect: match one token at a time.</remarks>
    [GeneratedRegex(@"\$\{(.+?)\}")]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Substitutes <c>${token}</c> placeholders.
    /// </summary>
    /// <remarks>
    /// QUIRK, preserved: an unknown token expands to the EMPTY string rather than being left as-is.
    /// A version JSON referencing a token the launcher does not supply silently drops it, which is
    /// how old version files survive being run by a launcher that never heard of their arguments.
    /// </remarks>
    public static string ReplaceTokens(string text, IReadOnlyDictionary<string, string> tokens)
        => TokenPattern().Replace(text, match => tokens.GetValueOrDefault(match.Groups[1].Value, string.Empty));

    // ================================================================== JVM side

    /// <summary>
    /// Builds the JVM flags: user arguments, platform workarounds, memory, agents and native paths.
    /// </summary>
    public static List<string> BuildJvmArguments(LaunchProfile profile, LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        // Custom args go first so anything below can override them.
        var args = new List<string>(options.CustomJvmArguments);

        AddJarModWorkarounds(profile, args);
        args.AddRange(profile.AddnJvmArguments);
        AddAgents(profile, options, args);
        AddNativeLibraryOverrides(options, args);
        AddPlatformWorkarounds(profile, args);
        AddMemory(options, args);

        // PermGen was removed in Java 8; emitting it there is an error, not just noise.
        if (options.JavaVersion.RequiresPermGen && options.PermGenMegabytes != 64)
        {
            args.Add($"-XX:PermSize={options.PermGenMegabytes.ToString(CultureInfo.InvariantCulture)}m");
        }

        args.Add("-Duser.language=en");

        if (options.JavaVersion.IsModular && options.ApplyOnlineFixes)
        {
            // The skin fix reflects into java.net, which the module system forbids by default.
            args.Add("--add-opens");
            args.Add("java.base/java.net=ALL-UNNAMED");
        }

        return args;
    }

    private static void AddJarModWorkarounds(LaunchProfile profile, List<string> args)
    {
        if (profile.JarMods.Count == 0)
        {
            return;
        }

        // A jar-modded jar no longer matches Mojang's signatures, and Forge refuses to start unless
        // told to expect that.
        args.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
        args.Add("-Dfml.ignorePatchDiscrepancies=true");
    }

    private static void AddAgents(LaunchProfile profile, LaunchOptions options, List<string> args)
    {
        foreach (var agent in profile.Agents)
        {
            List<string> jar = [], native = [], native32 = [], native64 = [];
            agent.Library.GetApplicableFiles(
                new RuntimeContext(), jar, native, native32, native64, options.LocalLibraryPath);

            if (jar.Count == 0)
            {
                continue;
            }

            args.Add(agent.Argument.Length == 0
                ? $"-javaagent:{jar[0]}"
                : $"-javaagent:{jar[0]}={agent.Argument}");
        }
    }

    private static void AddNativeLibraryOverrides(LaunchOptions options, List<string> args)
    {
        // Only applied when the file is actually there; a stale setting must not break the launch.
        if (options.NativeOpenAlPath is { Length: > 0 } openAl && File.Exists(openAl))
        {
            args.Add($"-Dorg.lwjgl.openal.libname={Path.GetFullPath(openAl)}");
        }

        if (options.NativeGlfwPath is { Length: > 0 } glfw && File.Exists(glfw))
        {
            args.Add($"-Dorg.lwjgl.glfw.libname={Path.GetFullPath(glfw)}");
        }
    }

    private static void AddPlatformWorkarounds(LaunchProfile profile, List<string> args)
    {
        if (OperatingSystem.IsMacOS())
        {
            args.Add("-Xdock:icon=icon.png");

            // 1.13 snapshots deadlock on macOS unless LWJGL owns the first thread.
            if (profile.HasTrait("FirstThreadOnMacOS"))
            {
                args.Add("-XstartOnFirstThread");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // Intel's driver looks for "javaw.exe" or "minecraft.exe" in a path to enable
            // optimisations; this filename tricks it into applying them. See MCL-767.
            args.Add(
                "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
        }
    }

    private static void AddMemory(LaunchOptions options, List<string> args)
    {
        // Swapped if the user has them backwards, rather than handing the JVM an impossible range.
        var min = Math.Min(options.MinMemoryMegabytes, options.MaxMemoryMegabytes);
        var max = Math.Max(options.MinMemoryMegabytes, options.MaxMemoryMegabytes);

        args.Add($"-Xms{min.ToString(CultureInfo.InvariantCulture)}m");
        args.Add($"-Xmx{max.ToString(CultureInfo.InvariantCulture)}m");
    }

    // ================================================================== game side

    /// <summary>
    /// Builds the game's own arguments by expanding the profile's argument template.
    /// </summary>
    public static List<string> BuildGameArguments(
        LaunchProfile profile,
        LaunchOptions options,
        AuthSession? session = null,
        LaunchTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        var pattern = profile.MinecraftArguments;

        // LaunchWrapper tweakers are appended as arguments, in load order.
        foreach (var tweaker in profile.Tweakers)
        {
            pattern += $" --tweakClass {tweaker}";
        }

        pattern += BuildTargetArguments(profile, target);

        var tokens = BuildTokens(profile, options, session);

        if (session is { Demo: true })
        {
            pattern += " --demo";
        }

        return [.. pattern
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ReplaceTokens(part, tokens))];
    }

    /// <remarks>
    /// Newer versions declare quick-play support as a trait; older ones only understand
    /// <c>--server</c>/<c>--port</c>, so the profile decides which spelling to use.
    /// </remarks>
    private static string BuildTargetArguments(LaunchProfile profile, LaunchTarget? target)
    {
        if (target is null)
        {
            return string.Empty;
        }

        if (target.Address.Length != 0)
        {
            return profile.HasTrait("feature:is_quick_play_multiplayer")
                ? $" --quickPlayMultiplayer {target.Address}:{target.Port.ToString(CultureInfo.InvariantCulture)}"
                : $" --server {target.Address} --port {target.Port.ToString(CultureInfo.InvariantCulture)}";
        }

        if (target.World.Length != 0 && profile.HasTrait("feature:is_quick_play_singleplayer"))
        {
            return $" --quickPlaySingleplayer {target.World}";
        }

        return string.Empty;
    }

    private static Dictionary<string, string> BuildTokens(
        LaunchProfile profile,
        LaunchOptions options,
        AuthSession? session)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile_name"] = options.InstanceName,
            ["version_name"] = profile.MinecraftVersion,
            ["version_type"] = profile.MinecraftVersionType,
            ["game_directory"] = Path.GetFullPath(options.GameDirectory),
            ["assets_root"] = Path.GetFullPath(options.AssetsDirectory),
            ["assets_index_name"] = profile.MinecraftAssets?.Id ?? string.Empty,

            // Pre-1.7.3 layout, where assets were a plain folder rather than a hashed store.
            ["game_assets"] = options.GameAssetsDirectory.Length != 0
                ? Path.GetFullPath(options.GameAssetsDirectory)
                : Path.GetFullPath(options.AssetsDirectory),
        };

        if (session is null)
        {
            return tokens;
        }

        tokens["auth_player_name"] = session.PlayerName;
        tokens["auth_uuid"] = session.Uuid;
        tokens["auth_access_token"] = session.AccessToken;
        tokens["auth_session"] = session.Session;
        tokens["user_type"] = session.UserType;
        tokens["user_properties"] = AuthSession.SerializeUserProperties();

        return tokens;
    }

    // ================================================================== the whole line

    /// <summary>
    /// Assembles the complete command line: JVM flags, classpath, main class, then game arguments.
    /// </summary>
    public static List<string> BuildCommandLine(
        LaunchProfile profile,
        RuntimeContext runtimeContext,
        LaunchOptions options,
        AuthSession? session = null,
        LaunchTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var args = BuildJvmArguments(profile, options);

        if (options.NativesDirectory.Length != 0)
        {
            args.Add($"-Djava.library.path={Path.GetFullPath(options.NativesDirectory)}");
        }

        List<string> jars = [], natives = [];
        profile.GetLibraryFiles(runtimeContext, jars, natives, options.LocalLibraryPath, options.GameDirectory);

        if (jars.Count != 0)
        {
            args.Add("-cp");
            args.Add(string.Join(Path.PathSeparator, jars));
        }

        args.Add(profile.MainClass);
        args.AddRange(BuildGameArguments(profile, options, session, target));

        return args;
    }
}
