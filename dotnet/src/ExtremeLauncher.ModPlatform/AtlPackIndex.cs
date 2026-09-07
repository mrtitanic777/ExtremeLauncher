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
 * Ported from launcher/modplatform/atlauncher/{ATLPackIndex,ATLShareCode}.{h,cpp}. Two small readers
 * that sit around the version manifest (AtlPackManifest): the pack index is the list the browser shows
 * -- a name, a type, and the versions on offer -- and a share code is a saved selection of optional
 * mods for one version of one pack, wrapped in the API's error envelope. Parsing only; the browser and
 * the installer that use these live elsewhere.
 */

using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

using ExtremeLauncher.Core;

namespace ExtremeLauncher.ModPlatform;

/// <summary>Whether a pack is listed publicly or needs a code to reach.</summary>
public enum AtlPackType
{
    Public,
    Private,
}

/// <summary>One installable version of a pack as the index lists it.</summary>
public sealed class AtlIndexedVersion
{
    public string Version { get; set; } = string.Empty;

    public string Minecraft { get; set; } = string.Empty;
}

/// <summary>A pack as it appears in the ATLauncher pack index — the browser's list.</summary>
public sealed class AtlIndexedPack
{
    public int Id { get; set; }

    public int Position { get; set; }

    public string Name { get; set; } = string.Empty;

    public AtlPackType Type { get; set; }

    public List<AtlIndexedVersion> Versions { get; } = [];

    /// <summary>Whether this is an ATLauncher system pack rather than a user-facing modpack.</summary>
    public bool System { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The pack's logo filename: the name with everything but letters and digits stripped, lowercased,
    /// plus ".png". Derived, so a pack always resolves to a stable icon name.
    /// </summary>
    public string SafeName { get; set; } = string.Empty;
}

/// <summary>Reads the ATLauncher pack index.</summary>
public static partial class AtlPackIndex
{
    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    public static AtlIndexedPack LoadIndexedPack(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var name = Json.RequireString(obj, "name");

        var pack = new AtlIndexedPack
        {
            Id = Json.RequireInteger(obj, "id"),
            Position = Json.RequireInteger(obj, "position"),
            Name = name,
            Type = Json.RequireString(obj, "type") == "private" ? AtlPackType.Private : AtlPackType.Public,
            System = Json.EnsureBoolean(obj["system"], false),
            Description = Json.EnsureString(obj, "description"),
            SafeName = NonAlphanumeric().Replace(name, string.Empty).ToLowerInvariant() + ".png",
        };

        foreach (var element in Json.RequireArray(obj, "versions"))
        {
            var versionObj = Json.RequireObjectValue(element);

            pack.Versions.Add(new AtlIndexedVersion
            {
                Version = Json.RequireString(versionObj, "version"),
                Minecraft = Json.RequireString(versionObj, "minecraft"),
            });
        }

        return pack;
    }
}

/// <summary>One optional mod named in a share code, and whether it was selected.</summary>
public sealed class AtlShareCodeMod
{
    public bool Selected { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>A shared selection of optional mods for one version of one pack.</summary>
public sealed class AtlShareCode
{
    public string Pack { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public List<AtlShareCodeMod> Mods { get; } = [];
}

/// <summary>The API envelope around a share code: an error flag, a code, a message, and the data.</summary>
public sealed class AtlShareCodeResponse
{
    public bool Error { get; set; }

    public int Code { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>The share code itself, present only when <see cref="Error"/> is false.</summary>
    public AtlShareCode? Data { get; set; }
}

/// <summary>Reads an ATLauncher share-code response.</summary>
public static class AtlShareCodeReader
{
    public static AtlShareCodeResponse LoadResponse(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var response = new AtlShareCodeResponse
        {
            Error = Json.RequireBoolean(obj, "error"),
            Code = Json.RequireInteger(obj, "code"),
        };

        // A message is present on both success and failure, but may be a JSON null.
        if (obj.TryGetPropertyValue("message", out var message) && message is not null)
        {
            response.Message = Json.RequireString(obj, "message");
        }

        // The data is only meaningful when there was no error.
        if (!response.Error)
        {
            var data = Json.RequireObject(obj, "data");

            var code = new AtlShareCode
            {
                Pack = Json.RequireString(data, "pack"),
                Version = Json.RequireString(data, "version"),
            };

            foreach (var element in Json.RequireArray(Json.RequireObject(data, "mods"), "optional"))
            {
                var modObj = Json.RequireObjectValue(element);

                code.Mods.Add(new AtlShareCodeMod
                {
                    Selected = Json.RequireBoolean(modObj, "selected"),
                    Name = Json.RequireString(modObj, "name"),
                });
            }

            response.Data = code;
        }

        return response;
    }
}
