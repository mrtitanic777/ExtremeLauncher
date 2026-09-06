// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * Ported from the format-version and requires halves of launcher/meta/JsonFormat.{h,cpp}.
 *
 * RELOCATED for the same reason as Require: upstream's OneSixVersionFormat.cpp and meta/JsonFormat.cpp
 * include each other, which C++ headers permit and C# projects do not. These pieces are needed by
 * both, so they live in the lower layer.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

public enum MetadataVersion
{
    Invalid = -1,
    InitialRelease = 0,
}

public sealed class ParseException : LauncherException
{
    public ParseException(string message) : base(message)
    {
    }
}

public static class MetadataFormat
{
    /// <summary>
    /// Reads "formatVersion". Absent is tolerated as the initial release unless
    /// <paramref name="required"/>.
    /// </summary>
    /// <remarks>
    /// An unrecognised version is <see cref="MetadataVersion.Invalid"/> rather than a best guess:
    /// metadata decides what gets downloaded and executed, so guessing is not safe.
    /// </remarks>
    public static MetadataVersion ParseFormatVersion(JsonObject obj, bool required = true)
    {
        ArgumentNullException.ThrowIfNull(obj);

        if (!obj.ContainsKey("formatVersion"))
        {
            return required ? MetadataVersion.Invalid : MetadataVersion.InitialRelease;
        }

        if (obj["formatVersion"]?.GetValueKind() != System.Text.Json.JsonValueKind.Number)
        {
            return MetadataVersion.Invalid;
        }

        return Json.RequireInteger(obj, "formatVersion") switch
        {
            0 or 1 => MetadataVersion.InitialRelease,
            _ => MetadataVersion.Invalid,
        };
    }

    public static void SerializeFormatVersion(JsonObject obj, MetadataVersion version)
    {
        ArgumentNullException.ThrowIfNull(obj);

        if (version == MetadataVersion.Invalid)
        {
            return;
        }

        obj["formatVersion"] = (int)version;
    }

    /// <summary>Reads a <c>[ { "uid": …, "equals": …, "suggests": … } ]</c> array.</summary>
    public static RequireSet ParseRequires(JsonObject obj, string key)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var result = new RequireSet();

        if (!obj.ContainsKey(key))
        {
            return result;
        }

        foreach (var element in Json.RequireArray(obj, key))
        {
            var requirement = element as JsonObject ?? throw new ParseException($"'{key}' entry is not an object");

            result.Add(new Require(
                Json.RequireString(requirement, "uid"),
                Json.EnsureString(requirement, "equals"),
                Json.EnsureString(requirement, "suggests")));
        }

        return result;
    }

    public static void SerializeRequires(JsonObject obj, RequireSet? requires, string key)
    {
        ArgumentNullException.ThrowIfNull(obj);

        if (requires is null || requires.Count == 0)
        {
            return;
        }

        var array = new JsonArray();

        foreach (var requirement in requires)
        {
            var item = new JsonObject { ["uid"] = requirement.Uid };

            if (requirement.EqualsVersion.Length != 0)
            {
                item["equals"] = requirement.EqualsVersion;
            }

            if (requirement.Suggests.Length != 0)
            {
                item["suggests"] = requirement.Suggests;
            }

            array.Add(item);
        }

        obj[key] = array;
    }
}
