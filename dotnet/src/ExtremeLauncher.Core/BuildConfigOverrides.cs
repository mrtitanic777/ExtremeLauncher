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
 * WHERE THE API CREDENTIALS COME FROM, given that BuildConfig cannot hold them.
 *
 * Upstream bakes them in at configure time: CMakeLists.txt carries Launcher_MSA_CLIENT_ID and friends,
 * and BuildConfig.cpp.in is expanded with them. This port deliberately leaves them empty in source --
 * README.md requires a fork to supply its own or blank them, and shipping the upstream ones means
 * accepting the Microsoft Identity Platform and CurseForge third-party terms on the user's behalf.
 *
 * But empty forever means Microsoft sign-in can never work, which is not a port of anything. So they
 * are read at STARTUP instead of compiled in, from either:
 *
 *   1. environment variables    EXTREMELAUNCHER_MSA_CLIENT_ID, _FLAME_API_KEY, _IMGUR_CLIENT_ID
 *   2. credentials.json         beside the executable, or in the data directory
 *
 * Both are outside source control, which is the property that matters. A launcher built from this
 * repository has no keys until somebody deliberately supplies them, and each dependent feature
 * disables itself with a message rather than failing obscurely -- which is upstream's own escape hatch.
 *
 * THE MSA CLIENT ID IS NOT A SECRET, and it is worth being precise about that rather than treating
 * every credential the same. It identifies a public OAuth client; the device-code flow is designed for
 * clients that cannot keep one. The reason it is not committed here is the terms, not confidentiality.
 * The CurseForge key IS a secret and must be treated as one.
 */

using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeLauncher.Core;

public static class BuildConfigOverrides
{
    public const string FileName = "credentials.json";

    private const string Prefix = "EXTREMELAUNCHER_";

    /// <summary>
    /// Returns a copy of a config with any supplied credentials filled in.
    /// </summary>
    /// <param name="dataRoot">The data directory, searched after the executable's own folder.</param>
    /// <returns>The config to use, and a note for the log about where anything came from.</returns>
    public static (BuildConfig Config, IReadOnlyList<string> Notes) Apply(BuildConfig config, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(config);

        var notes = new List<string>();

        var msa = config.MsaClientId;
        var flame = config.FlameApiKey;
        var imgur = config.ImgurClientId;

        /*
         * The FILE first, then the environment, so a machine-wide variable can override a checked-out
         * file rather than the other way round. Someone setting a variable is being more deliberate
         * than someone who has a file lying in a folder.
         */
        foreach (var directory in new[] { AppContext.BaseDirectory, dataRoot })
        {
            if (directory is null or { Length: 0 })
            {
                continue;
            }

            var path = FileSystem.PathCombine(directory, FileName);

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                {
                    notes.Add($"{path} is not a JSON object; ignored.");

                    continue;
                }

                msa = Read(root, "msaClientId", msa);
                flame = Read(root, "flameApiKey", flame);
                imgur = Read(root, "imgurClientId", imgur);

                notes.Add($"Read credentials from {path}.");
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                // Named rather than silent: a credentials file that will not parse is the reason
                // sign-in is about to be missing, and that is worth being told.
                notes.Add($"Could not read {path}: {e.Message}");
            }
        }

        msa = FromEnvironment("MSA_CLIENT_ID", msa, notes);
        flame = FromEnvironment("FLAME_API_KEY", flame, notes);
        imgur = FromEnvironment("IMGUR_CLIENT_ID", imgur, notes);

        if (msa.Length == 0)
        {
            /*
             * Said plainly at startup, because the alternative is a Sign in button that is disabled for
             * a reason nobody can discover. Upstream has the same hole and says nothing about it.
             */
            notes.Add(
                "No Microsoft client id is configured, so Microsoft sign-in is unavailable. "
                + $"Set {Prefix}MSA_CLIENT_ID or put \"msaClientId\" in {FileName}.");
        }

        return (
            config with
            {
                MsaClientId = msa,
                FlameApiKey = flame,
                ImgurClientId = imgur,
            },
            notes);
    }

    private static string Read(JsonObject root, string key, string fallback)
        => root[key]?.GetValue<string>() is { Length: > 0 } value ? value : fallback;

    private static string FromEnvironment(string name, string fallback, List<string> notes)
    {
        var value = Environment.GetEnvironmentVariable(Prefix + name);

        if (value is not { Length: > 0 })
        {
            return fallback;
        }

        notes.Add($"Read {name} from the environment.");

        return value;
    }
}
