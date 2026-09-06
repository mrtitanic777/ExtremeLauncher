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
 * Ported from launcher/minecraft/MojangVersionFormat.{h,cpp}.
 *
 * Reads and writes Mojang's own version JSON -- the format piston-meta serves. The launcher must be
 * able to write it back out faithfully, because version files get cached to disk and re-read.
 *
 * FIELDS ARE ONLY WRITTEN WHEN SET. Upstream's writeString skips empty strings, minimumLauncherVersion
 * is skipped at -1, and the asset index is skipped unless `known` is true. That last one matters: when
 * a version JSON has "assets" but no "assetIndex" block, the parser SYNTHESISES an index (with a
 * hardcoded URL) and marks it unknown precisely so it does not get written back as though Mojang had
 * supplied it. Dropping that flag would corrupt every cached copy of an old version file.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

public static class MojangVersionFormat
{
    /// <summary>The highest "minimumLauncherVersion" this launcher claims to understand.</summary>
    private const int CurrentMinimumLauncherVersion = 18;

    // ================================================================== reading

    public static VersionFile VersionFileFromJson(JsonNode? document, string filename)
    {
        if (document is null)
        {
            throw new JsonException($"{filename} is empty or null");
        }

        var root = document as JsonObject
                   ?? throw new JsonException($"{filename} is not an object");

        var result = new VersionFile();

        ReadVersionProperties(root, result);

        result.Name = "Minecraft";
        result.Uid = "net.minecraft";
        result.Version = result.MinecraftVersion;

        if (root["libraries"] is JsonArray libraries)
        {
            foreach (var element in libraries)
            {
                var libraryObject = element as JsonObject
                                    ?? throw new JsonException($"{filename} has a non-object library");

                result.Libraries.Add(LibraryFromJson(result, libraryObject, filename));
            }
        }

        return result;
    }

    public static void ReadVersionProperties(JsonObject input, VersionFile output)
    {
        ReadString(input, "id", value => output.MinecraftVersion = value);
        ReadString(input, "mainClass", value => output.MainClass = value);
        ReadString(input, "minecraftArguments", value => output.MinecraftArguments = value);
        ReadString(input, "type", value => output.Type = value);
        ReadString(input, "assets", value => output.Assets = value);

        if (input["assetIndex"] is JsonObject assetIndex)
        {
            output.MojangAssetIndex = AssetIndexFromJson(assetIndex);
        }
        else if (output.Assets.Length != 0)
        {
            // Synthesised, not supplied -- see the file header for why `Known` stays false.
            output.MojangAssetIndex = new MojangAssetIndexInfo(output.Assets);
        }

        if (Json.EnsureString(input, "releaseTime") is { Length: > 0 } releaseTime
            && ParseUtils.TryTimeFromS3Time(releaseTime, out var parsedRelease))
        {
            output.ReleaseTime = parsedRelease;
        }

        if (Json.EnsureString(input, "time") is { Length: > 0 } updateTime
            && ParseUtils.TryTimeFromS3Time(updateTime, out var parsedUpdate))
        {
            output.UpdateTime = parsedUpdate;
        }

        if (input.ContainsKey("minimumLauncherVersion"))
        {
            output.MinimumLauncherVersion = Json.RequireInteger(input, "minimumLauncherVersion");

            if (output.MinimumLauncherVersion > CurrentMinimumLauncherVersion)
            {
                output.AddProblem(
                    ProblemSeverity.Warning,
                    $"The 'minimumLauncherVersion' value of this version ({output.MinimumLauncherVersion}) is higher than "
                    + $"supported by {BuildConfig.Instance.LauncherDisplayName} ({CurrentMinimumLauncherVersion}). "
                    + "It might not work properly!");
            }
        }

        if (input["compatibleJavaMajors"] is JsonArray compatibleJavaMajors)
        {
            foreach (var element in compatibleJavaMajors)
            {
                output.CompatibleJavaMajors.Add(Json.RequireInteger(element));
            }
        }

        if (input.ContainsKey("compatibleJavaName"))
        {
            output.CompatibleJavaName = Json.RequireString(input, "compatibleJavaName");
        }

        if (input["downloads"] is JsonObject downloads)
        {
            foreach (var (classifier, value) in downloads)
            {
                output.MojangDownloads[classifier] = DownloadInfoFromJson(
                    value as JsonObject ?? throw new JsonException($"'{classifier}' is not an object"));
            }
        }
    }

    public static Library LibraryFromJson(ProblemContainer problems, JsonObject libraryObject, string filename)
    {
        if (!libraryObject.ContainsKey("name"))
        {
            throw new JsonException($"{filename} contains a library that doesn't have a 'name' field");
        }

        var rawName = Json.RequireString(libraryObject, "name");
        var result = new Library(rawName);

        // A broken coordinate is a problem to report, not a reason to refuse the whole version file.
        if (!result.Name.IsValid)
        {
            problems.AddProblem(ProblemSeverity.Error, $"Library {rawName} name is broken and cannot be processed.");
        }

        ReadString(libraryObject, "url", value => result.RepositoryUrl = value);

        if (libraryObject["extract"] is JsonObject extract)
        {
            result.HasExcludes = true;

            foreach (var element in Json.RequireArray(extract, "exclude"))
            {
                result.ExtractExcludes.Add(Json.RequireString(element));
            }
        }

        if (libraryObject["natives"] is JsonObject natives)
        {
            foreach (var (platform, value) in natives)
            {
                if (value?.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    // Upstream warns and still stores the (empty) value rather than skipping.
                    problems.AddProblem(ProblemSeverity.Warning, $"{filename} contains an invalid native (skipping)");
                }

                result.NativeClassifiers[platform] = Json.EnsureString(value);
            }
        }

        if (libraryObject.ContainsKey("rules"))
        {
            result.ApplyRules = true;
            result.SetRules(RuleParser.RulesFromJsonV4(libraryObject));
        }

        if (libraryObject.ContainsKey("downloads"))
        {
            result.MojangDownloads = LibraryDownloadInfoFromJson(libraryObject);
        }

        return result;
    }

    private static MojangDownloadInfo DownloadInfoFromJson(JsonObject obj)
    {
        var result = new MojangDownloadInfo();
        ReadDownloadInfo(result, obj);
        return result;
    }

    private static MojangAssetIndexInfo AssetIndexFromJson(JsonObject obj)
    {
        var result = new MojangAssetIndexInfo();
        ReadDownloadInfo(result, obj);

        result.TotalSize = Json.RequireInteger(obj, "totalSize");
        result.Id = Json.RequireString(obj, "id");

        return result;
    }

    private static void ReadDownloadInfo(MojangDownloadInfo output, JsonObject obj)
    {
        // "path" is optional and unused; carried only so it round-trips.
        ReadString(obj, "path", value => output.Path = value);

        output.Sha1 = Json.RequireString(obj, "sha1");
        output.Url = Json.RequireString(obj, "url");
        output.Size = Json.RequireInteger(obj, "size");
    }

    private static MojangLibraryDownloadInfo LibraryDownloadInfoFromJson(JsonObject libraryObject)
    {
        var result = new MojangLibraryDownloadInfo();
        var downloads = Json.RequireObject(libraryObject, "downloads");

        if (downloads["artifact"] is JsonObject artifact)
        {
            result.Artifact = DownloadInfoFromJson(artifact);
        }

        if (downloads["classifiers"] is JsonObject classifiers)
        {
            foreach (var (classifier, value) in classifiers)
            {
                result.Classifiers[classifier] = DownloadInfoFromJson(
                    value as JsonObject ?? throw new JsonException($"classifier '{classifier}' is not an object"));
            }
        }

        return result;
    }

    // ================================================================== writing

    public static JsonObject VersionFileToJson(VersionFile patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var root = new JsonObject();
        WriteVersionProperties(patch, root);

        if (patch.Libraries.Count != 0)
        {
            var array = new JsonArray();

            foreach (var library in patch.Libraries)
            {
                array.Add(LibraryToJson(library));
            }

            root["libraries"] = array;
        }

        return root;
    }

    public static void WriteVersionProperties(VersionFile input, JsonObject output)
    {
        Json.WriteString(output, "id", input.MinecraftVersion);
        Json.WriteString(output, "mainClass", input.MainClass);
        Json.WriteString(output, "minecraftArguments", input.MinecraftArguments);
        Json.WriteString(output, "type", input.Type);

        if (input.ReleaseTime is { } releaseTime)
        {
            Json.WriteString(output, "releaseTime", ParseUtils.TimeToS3Time(releaseTime));
        }

        if (input.UpdateTime is { } updateTime)
        {
            Json.WriteString(output, "time", ParseUtils.TimeToS3Time(updateTime));
        }

        if (input.MinimumLauncherVersion != -1)
        {
            output["minimumLauncherVersion"] = input.MinimumLauncherVersion;
        }

        Json.WriteString(output, "assets", input.Assets);

        // Only write an index Mojang actually gave us; see the file header.
        if (input.MojangAssetIndex is { Known: true } assetIndex)
        {
            output["assetIndex"] = AssetIndexToJson(assetIndex);
        }

        if (input.MojangDownloads.Count != 0)
        {
            var downloads = new JsonObject();

            foreach (var (classifier, info) in input.MojangDownloads)
            {
                downloads[classifier] = DownloadInfoToJson(info);
            }

            output["downloads"] = downloads;
        }

        if (input.CompatibleJavaMajors.Count != 0)
        {
            var majors = new JsonArray();

            foreach (var major in input.CompatibleJavaMajors)
            {
                majors.Add(major);
            }

            output["compatibleJavaMajors"] = majors;
        }

        Json.WriteString(output, "compatibleJavaName", input.CompatibleJavaName);
    }

    public static JsonObject LibraryToJson(Library library)
    {
        ArgumentNullException.ThrowIfNull(library);

        var result = new JsonObject { ["name"] = library.Name.Serialize() };

        if (library.RepositoryUrl.Length != 0)
        {
            result["url"] = library.RepositoryUrl;
        }

        if (library.IsNative)
        {
            var natives = new JsonObject();

            foreach (var (platform, classifier) in library.NativeClassifiers)
            {
                natives[platform] = classifier;
            }

            result["natives"] = natives;

            // Note: upstream only writes "extract" for natives, even though the field is read
            // unconditionally. Preserved.
            if (library.ExtractExcludes.Count != 0)
            {
                var excludes = new JsonArray();

                foreach (var exclude in library.ExtractExcludes)
                {
                    excludes.Add(exclude);
                }

                result["extract"] = new JsonObject { ["exclude"] = excludes };
            }
        }

        if (library.Rules.Count != 0)
        {
            var rules = new JsonArray();

            foreach (var rule in library.Rules)
            {
                rules.Add(rule.ToJson());
            }

            result["rules"] = rules;
        }

        if (library.MojangDownloads is { } downloads)
        {
            result["downloads"] = LibraryDownloadInfoToJson(downloads);
        }

        return result;
    }

    private static JsonObject DownloadInfoToJson(MojangDownloadInfo info)
    {
        var result = new JsonObject();

        if (info.Path.Length != 0)
        {
            result["path"] = info.Path;
        }

        result["sha1"] = info.Sha1;
        result["size"] = info.Size;
        result["url"] = info.Url;

        return result;
    }

    private static JsonObject AssetIndexToJson(MojangAssetIndexInfo info)
    {
        var result = DownloadInfoToJson(info);

        result["totalSize"] = info.TotalSize;
        result["id"] = info.Id;

        return result;
    }

    private static JsonObject LibraryDownloadInfoToJson(MojangLibraryDownloadInfo info)
    {
        var result = new JsonObject();

        if (info.Artifact is { } artifact)
        {
            result["artifact"] = DownloadInfoToJson(artifact);
        }

        if (info.Classifiers.Count != 0)
        {
            var classifiers = new JsonObject();

            foreach (var (classifier, download) in info.Classifiers)
            {
                classifiers[classifier] = DownloadInfoToJson(download);
            }

            result["classifiers"] = classifiers;
        }

        return result;
    }

    /// <summary>Assigns only when the key is present, matching upstream's <c>readString</c>.</summary>
    private static void ReadString(JsonObject root, string key, Action<string> assign)
    {
        if (root.ContainsKey(key))
        {
            assign(Json.RequireString(root, key));
        }
    }
}
