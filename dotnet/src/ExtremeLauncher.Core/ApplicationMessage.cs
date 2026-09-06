// SPDX-License-Identifier: Apache-2.0
/*
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from launcher/ApplicationMessage.{h,cpp}. Apache-2.0, like the file it came from.
 *
 * WHAT A SECOND COPY OF THE LAUNCHER SAYS TO THE FIRST. Only one instance may own the data directory,
 * so a second one started with "launch this instance" hands its arguments over and exits. This is the
 * envelope: a command and a flat map of named arguments.
 *
 * IT IS A TRUST BOUNDARY, thin as it is. What arrives is bytes from another process, and the receiver
 * acts on it -- so every field is read defensively and a message that is not what it claims yields an
 * empty command rather than a half-populated one that gets acted on anyway.
 */

using System.Text;
using System.Text.Json.Nodes;

namespace ExtremeLauncher.Core;

/// <summary>One instruction passed between launcher processes.</summary>
public sealed class ApplicationMessage
{
    public string Command { get; set; } = string.Empty;

    /// <summary>Named arguments. Flat and all strings; the format has no nesting.</summary>
    public Dictionary<string, string> Args { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads a message.
    /// </summary>
    /// <remarks>
    /// UPSTREAM USES toString() ON EVERY VALUE, which yields an empty string for a number, an array or
    /// an object rather than failing. Kept: a sender on a different version may include fields this
    /// one does not know, and refusing the whole message over one would break the handover entirely.
    /// The command is what decides what happens, and that is checked by the receiver.
    /// </remarks>
    public static ApplicationMessage Parse(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var root = Json.RequireObject(Json.RequireDocument(input, "ApplicationMessage"));
        var message = new ApplicationMessage
        {
            // Absent, or present as a non-string, both give an empty command -- which does nothing.
            Command = AsString(root["command"]),
        };

        if (root["args"] is JsonObject args)
        {
            foreach (var (key, value) in args)
            {
                message.Args[key] = AsString(value);
            }
        }

        return message;
    }

    /// <summary>Writes a message.</summary>
    public byte[] Serialize()
    {
        var args = new JsonObject();

        foreach (var (key, value) in Args)
        {
            args[key] = value;
        }

        var root = new JsonObject
        {
            ["command"] = Command,
            ["args"] = args,
        };

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>QJsonValue::toString semantics: anything that is not a string becomes empty.</summary>
    private static string AsString(JsonNode? node)
        => node?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : string.Empty;
}
