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
 * Ported from launcher/minecraft/MojangDownloadInfo.h.
 *
 * The "downloads" blocks of a Mojang version JSON: where each artifact lives, its sha1, and its size.
 * A library has one primary artifact plus a map of classifier-keyed natives.
 */

namespace ExtremeLauncher.Minecraft;

public class MojangDownloadInfo
{
    /// <summary>
    /// Local filesystem path. NOT used by the launcher -- carried only so Mojang files can be passed
    /// through unmolested on re-serialization.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Sha1 { get; set; } = string.Empty;

    public int Size { get; set; }
}

public sealed class MojangLibraryDownloadInfo
{
    public MojangLibraryDownloadInfo()
    {
    }

    public MojangLibraryDownloadInfo(MojangDownloadInfo? artifact) => Artifact = artifact;

    /// <summary>The plain, non-native jar.</summary>
    public MojangDownloadInfo? Artifact { get; set; }

    /// <summary>Native jars, keyed by classifier ("natives-linux").</summary>
    public Dictionary<string, MojangDownloadInfo> Classifiers { get; } = new(StringComparer.Ordinal);

    /// <summary>A null classifier asks for the primary artifact, matching upstream.</summary>
    public MojangDownloadInfo? GetDownloadInfo(string? classifier)
    {
        if (classifier is null)
        {
            return Artifact;
        }

        return Classifiers.GetValueOrDefault(classifier);
    }
}

public sealed class MojangAssetIndexInfo : MojangDownloadInfo
{
    public MojangAssetIndexInfo()
    {
    }

    /// <remarks>
    /// The hard-coded URLs are upstream's workaround for the 2017 S3 outage that left asset indexes
    /// unreachable; "legacy" points at piston-meta while everything else still uses the old S3 host.
    /// Preserved verbatim rather than modernised, since changing it would change what gets fetched.
    /// </remarks>
    public MojangAssetIndexInfo(string id)
    {
        Id = id;

        Url = id == "legacy"
            ? "https://piston-meta.mojang.com/mc/assets/legacy/c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729/legacy.json"
            : $"https://s3.amazonaws.com/Minecraft.Download/indexes/{id}.json";

        Known = false;
    }

    public int TotalSize { get; set; }

    public string Id { get; set; } = string.Empty;

    /// <summary>False when the URL was synthesised rather than read from the version JSON.</summary>
    public bool Known { get; set; } = true;
}
