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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
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
 * Ported from launcher/Json.{h,cpp}.
 *
 * Qt's QJsonObject/QJsonArray/QJsonValue are a *mutable* DOM, and the launcher both reads and writes
 * JSON, so the analogue here is System.Text.Json.Nodes (JsonNode/JsonObject/JsonArray) rather than
 * the read-only JsonElement.
 *
 * C++ templates specialize on T; C# generics cannot. The upstream header papers over this with the
 * JSON_HELPERFUNCTIONS macro that emits requireString/ensureInteger/... -- and those macro-generated
 * names are what call sites actually use, so they are the primary API here too.
 *
 * DROPPED (Qt-specific with no C# analogue):
 *   - requireIsType<QVariant> / toJson<QVariant> -- JsonNode already covers "any JSON value".
 *   - requireIsType<QDir>                        -- callers should use a path string directly.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeLauncher.Core;

public sealed class JsonException : LauncherException
{
    public JsonException(string message) : base(message)
    {
    }

    public JsonException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public static class Json
{
    /// <summary>Token replaced with the quoted key name in "what" descriptions.</summary>
    public const string Placeholder = "__placeholder__";

    private static readonly string[] IsoDateTimeFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd",
    ];

    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    // ------------------------------------------------------------------ serialization

    /// <summary>Compact UTF-8 serialization, equivalent to <c>QJsonDocument::toJson(Compact)</c>.</summary>
    public static byte[] ToText(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString(CompactOptions));
    }

    /// <summary>Indented UTF-8 serialization, equivalent to <c>QJsonDocument::toJson()</c>.</summary>
    public static byte[] ToIndentedText(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString(IndentedOptions));
    }

    // ------------------------------------------------------------------ documents

    /// <exception cref="JsonException">The data is not well-formed JSON.</exception>
    public static JsonNode RequireDocument(byte[] data, string what = "Document")
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            return JsonNode.Parse(SkipByteOrderMark(data))
                   ?? throw new JsonException($"{what}: Error parsing JSON: document is null");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new JsonException($"{what}: Error parsing JSON: {e.Message}", e);
        }
    }

    /// <summary>Drops a leading UTF-8 byte order mark, which is not JSON but is everywhere.</summary>
    /// <remarks>
    /// LENIENT ON THE WAY IN, STRICT ON THE WAY OUT. System.Text.Json refuses a document starting
    /// EF BB BF with "'0xEF' is an invalid start of a value", while Qt's QJsonDocument::fromJson skips
    /// it -- so a file upstream reads happily was, until this, unreadable here.
    ///
    /// The files are real. The inherited pack.mcmeta fixture has a BOM because whoever made it used an
    /// editor that adds one, and so does many a hand-authored modrinth.index.json. The person who
    /// wrote it cannot see the difference and has no way to guess why one launcher takes their pack
    /// and another calls it corrupt.
    ///
    /// This does NOT contradict the export wave's finding that a BOM this port WROTE was a bug: the
    /// writer must not emit one, and the reader must not care. Those are the same rule seen from the
    /// two ends.
    /// </remarks>
    public static ReadOnlySpan<byte> SkipByteOrderMark(ReadOnlySpan<byte> data)
        => data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? data[3..] : data;

    /// <exception cref="JsonException">The data is not well-formed JSON.</exception>
    public static JsonNode RequireDocument(string text, string what = "Document")
        => RequireDocument(Encoding.UTF8.GetBytes(text), what);

    /// <summary>Reads and parses a JSON file.</summary>
    /// <remarks>
    /// Named distinctly rather than overloaded: the C++ distinguishes the text and filename overloads
    /// by QByteArray vs QString, which collapses to an ambiguous <c>string</c> in C#.
    /// </remarks>
    /// <exception cref="FileSystemException">The file could not be read.</exception>
    /// <exception cref="JsonException">The contents are not well-formed JSON.</exception>
    public static JsonNode RequireDocumentFromFile(string filename, string what = "Document")
        => RequireDocument(FileSystem.Read(filename), what);

    /// <summary>Writes indented JSON, atomically, via <see cref="FileSystem.Write"/>.</summary>
    /// <exception cref="FileSystemException">Writing failed.</exception>
    public static void Write(JsonNode node, string filename)
    {
        ArgumentNullException.ThrowIfNull(node);
        FileSystem.Write(filename, ToIndentedText(node));
    }

    /// <exception cref="JsonException">The document is not a JSON object.</exception>
    public static JsonObject RequireObject(JsonNode? doc, string what = "Document")
        => doc as JsonObject ?? throw new JsonException($"{what} is not an object");

    /// <exception cref="JsonException">The document is not a JSON array.</exception>
    public static JsonArray RequireArray(JsonNode? doc, string what = "Document")
        => doc as JsonArray ?? throw new JsonException($"{what} is not an array");

    // ------------------------------------------------------------------ writing helpers

    /// <summary>Writes <paramref name="value"/> only when it is non-empty, as upstream does.</summary>
    public static void WriteString(JsonObject to, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(to);

        if (!string.IsNullOrEmpty(value))
        {
            to[key] = value;
        }
    }

    /// <summary>Writes <paramref name="values"/> only when the list is non-empty, as upstream does.</summary>
    public static void WriteStringList(JsonObject to, string key, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(values);

        var array = new JsonArray();

        foreach (var value in values)
        {
            array.Add(value);
        }

        if (array.Count != 0)
        {
            to[key] = array;
        }
    }

    // ------------------------------------------------------------------ string

    public static string RequireString(JsonNode? value, string what = "Value")
        => value is not null && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw new JsonException($"{what} is not a string");

    public static string EnsureString(JsonNode? value, string @default = "", string what = "Value")
        => Ensure(value, @default, () => RequireString(value, what));

    public static string RequireString(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireString(RequireMember(parent, key, localWhat), localWhat);
    }

    public static string EnsureString(JsonObject parent, string key, string @default = "", string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureString(node, @default, localWhat)
            : @default;

    // ------------------------------------------------------------------ boolean

    public static bool RequireBoolean(JsonNode? value, string what = "Value")
        => value is not null && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : throw new JsonException($"{what} is not a bool");

    public static bool EnsureBoolean(JsonNode? value, bool @default = false, string what = "Value")
        => Ensure(value, @default, () => RequireBoolean(value, what));

    public static bool RequireBoolean(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireBoolean(RequireMember(parent, key, localWhat), localWhat);
    }

    public static bool EnsureBoolean(JsonObject parent, string key, bool @default = false, string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureBoolean(node, @default, localWhat)
            : @default;

    // ------------------------------------------------------------------ double

    /// <remarks>
    /// The type probing is necessary, not defensive. A JsonNode parsed from text wraps a JsonElement
    /// and converts freely between numeric types, but one built in memory (<c>obj["x"] = 18</c>)
    /// wraps the CLR value and <c>GetValue&lt;double&gt;()</c> throws for an int. Every
    /// serialize-then-reread path goes through the in-memory form.
    /// </remarks>
    public static double RequireDouble(JsonNode? value, string what = "Value")
    {
        if (value is JsonValue number && value.GetValueKind() == JsonValueKind.Number)
        {
            if (number.TryGetValue<double>(out var asDouble))
            {
                return asDouble;
            }

            if (number.TryGetValue<long>(out var asLong))
            {
                return asLong;
            }

            if (number.TryGetValue<int>(out var asInt))
            {
                return asInt;
            }

            if (number.TryGetValue<decimal>(out var asDecimal))
            {
                return (double)asDecimal;
            }
        }

        throw new JsonException($"{what} is not a double");
    }

    public static double EnsureDouble(JsonNode? value, double @default = 0, string what = "Value")
        => Ensure(value, @default, () => RequireDouble(value, what));

    public static double RequireDouble(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireDouble(RequireMember(parent, key, localWhat), localWhat);
    }

    public static double EnsureDouble(JsonObject parent, string key, double @default = 0, string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureDouble(node, @default, localWhat)
            : @default;

    // ------------------------------------------------------------------ integer

    /// <remarks>Upstream reads a double and rejects it if it has a fractional part.</remarks>
    public static int RequireInteger(JsonNode? value, string what = "Value")
    {
        var d = RequireDouble(value, what);

        // C#'s % on doubles is fmod, so this is the same test the C++ makes.
        if (d % 1 != 0)
        {
            throw new JsonException($"{what} is not an integer");
        }

        return (int)d;
    }

    public static int EnsureInteger(JsonNode? value, int @default = 0, string what = "Value")
        => Ensure(value, @default, () => RequireInteger(value, what));

    public static int RequireInteger(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireInteger(RequireMember(parent, key, localWhat), localWhat);
    }

    public static int EnsureInteger(JsonObject parent, string key, int @default = 0, string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureInteger(node, @default, localWhat)
            : @default;

    // ------------------------------------------------------------------ object / array / any

    public static JsonObject RequireObjectValue(JsonNode? value, string what = "Value")
        => value as JsonObject ?? throw new JsonException($"{what} is not an object");

    public static JsonObject RequireObject(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireObjectValue(RequireMember(parent, key, localWhat), localWhat);
    }

    public static JsonObject EnsureObject(JsonNode? value, JsonObject? @default = null, string what = "Value")
        => Ensure(value, @default ?? new JsonObject(), () => RequireObjectValue(value, what));

    public static JsonObject EnsureObject(JsonObject parent, string key, JsonObject? @default = null, string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureObject(node, @default, localWhat)
            : @default ?? new JsonObject();

    public static JsonArray RequireArrayValue(JsonNode? value, string what = "Value")
        => value as JsonArray ?? throw new JsonException($"{what} is not an array");

    public static JsonArray RequireArray(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireArrayValue(RequireMember(parent, key, localWhat), localWhat);
    }

    public static JsonArray EnsureArray(JsonNode? value, JsonArray? @default = null, string what = "Value")
        => Ensure(value, @default ?? new JsonArray(), () => RequireArrayValue(value, what));

    public static JsonArray EnsureArray(JsonObject parent, string key, JsonArray? @default = null, string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureArray(node, @default, localWhat)
            : @default ?? new JsonArray();

    /// <summary>Any present, non-null JSON value.</summary>
    public static JsonNode RequireJsonValue(JsonNode? value, string what = "Value")
        => value is not null && value.GetValueKind() != JsonValueKind.Null
            ? value
            : throw new JsonException($"{what} is null or undefined");

    public static JsonNode? EnsureJsonValue(JsonNode? value, JsonNode? @default = null, string what = "Value")
        => Ensure(value, @default, () => RequireJsonValue(value, what));

    public static JsonNode RequireJsonValue(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireJsonValue(RequireMember(parent, key, localWhat), localWhat);
    }

    // ------------------------------------------------------------------ date/time

    /// <remarks>
    /// Upstream parses with <c>Qt::ISODate</c>. Exact formats are used here rather than a plain
    /// <c>TryParse</c>, which would accept non-ISO shapes like "01/02/2003" that Qt rejects.
    /// </remarks>
    public static DateTimeOffset RequireDateTime(JsonNode? value, string what = "Value")
    {
        var text = RequireString(value, what);

        if (!DateTimeOffset.TryParseExact(
                text,
                IsoDateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new JsonException($"{what} is not a ISO formatted date/time value");
        }

        return parsed;
    }

    public static DateTimeOffset EnsureDateTime(JsonNode? value, DateTimeOffset @default = default, string what = "Value")
        => Ensure(value, @default, () => RequireDateTime(value, what));

    public static DateTimeOffset RequireDateTime(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireDateTime(RequireMember(parent, key, localWhat), localWhat);
    }

    public static DateTimeOffset EnsureDateTime(
        JsonObject parent,
        string key,
        DateTimeOffset @default = default,
        string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureDateTime(node, @default, localWhat)
            : @default;

    // ------------------------------------------------------------------ url

    /// <remarks>
    /// Upstream uses <c>ensureIsType&lt;QString&gt;</c> (not require) for the inner read, so a
    /// non-string value degrades to an empty URL rather than throwing. Null is returned where the C++
    /// returns a default-constructed QUrl.
    /// </remarks>
    public static Uri? RequireUrl(JsonNode? value, string what = "Value")
    {
        var text = EnsureString(value, string.Empty, what);

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var url))
        {
            throw new JsonException($"{what} is not a correctly formatted URL");
        }

        return url;
    }

    public static Uri? EnsureUrl(JsonNode? value, Uri? @default = null, string what = "Value")
        => Ensure(value, @default, () => RequireUrl(value, what));

    public static Uri? RequireUrl(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireUrl(RequireMember(parent, key, localWhat), localWhat);
    }

    public static Uri? EnsureUrl(JsonObject parent, string key, Uri? @default = null, string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureUrl(node, @default, localWhat)
            : @default;

    // ------------------------------------------------------------------ byte array (hex)

    /// <remarks>
    /// Mirrors <c>QByteArray::fromHex</c>, which skips characters that are not hex digits rather than
    /// failing. The Latin-1 encodability check from upstream is preserved.
    /// </remarks>
    public static byte[] RequireByteArray(JsonNode? value, string what = "Value")
    {
        var text = EnsureString(value, string.Empty, what);

        foreach (var c in text)
        {
            if (c > 0xFF)
            {
                throw new JsonException($"{what} is not encodable as Latin1");
            }
        }

        return FromHexLenient(text);
    }

    public static byte[] EnsureByteArray(JsonNode? value, byte[]? @default = null, string what = "Value")
        => Ensure(value, @default ?? [], () => RequireByteArray(value, what));

    public static byte[] RequireByteArray(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireByteArray(RequireMember(parent, key, localWhat), localWhat);
    }

    // ------------------------------------------------------------------ uuid

    /// <remarks>
    /// Upstream parses then requires that <c>toString()</c> reproduces the input exactly, which means
    /// only the braced lowercase form round-trips.
    /// </remarks>
    public static Guid RequireUuid(JsonNode? value, string what = "Value")
    {
        var text = RequireString(value, what);

        if (!Guid.TryParse(text, out var uuid) || !string.Equals(uuid.ToString("B"), text, StringComparison.Ordinal))
        {
            throw new JsonException($"{what} is not a valid UUID");
        }

        return uuid;
    }

    public static Guid EnsureUuid(JsonNode? value, Guid @default = default, string what = "Value")
        => Ensure(value, @default, () => RequireUuid(value, what));

    public static Guid RequireUuid(JsonObject parent, string key, string what = Placeholder)
    {
        var localWhat = ResolveWhat(what, key);
        return RequireUuid(RequireMember(parent, key, localWhat), localWhat);
    }

    public static Guid EnsureUuid(JsonObject parent, string key, Guid @default = default, string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureUuid(node, @default, localWhat)
            : @default;

    // ------------------------------------------------------------------ arrays of T

    /// <summary>
    /// Equivalent of <c>requireIsArrayOf&lt;T&gt;</c>; the element conversion is supplied as a
    /// delegate since C# cannot specialize generics the way the C++ template does.
    /// </summary>
    public static List<T> RequireArrayOf<T>(JsonNode? value, Func<JsonNode?, string, T> element, string what = "Value")
    {
        ArgumentNullException.ThrowIfNull(element);

        var array = RequireArrayValue(value, what);
        var result = new List<T>(array.Count);

        foreach (var item in array)
        {
            result.Add(element(item, what));
        }

        return result;
    }

    /// <summary>Equivalent of <c>ensureIsArrayOf&lt;T&gt;</c>: a missing array yields an empty list.</summary>
    public static List<T> EnsureArrayOf<T>(JsonNode? value, Func<JsonNode?, string, T> element, string what = "Value")
    {
        ArgumentNullException.ThrowIfNull(element);

        var array = EnsureArray(value, null, what);
        var result = new List<T>(array.Count);

        foreach (var item in array)
        {
            result.Add(element(item, what));
        }

        return result;
    }

    /// <summary>Convenience for the most common case.</summary>
    public static List<string> EnsureStringList(JsonNode? value, string what = "Value")
        => EnsureArrayOf(value, (node, w) => RequireString(node, w), what);

    /// <summary>Convenience for the most common case.</summary>
    public static List<string> EnsureStringList(JsonObject parent, string key, string what = Placeholder)
        => EnsureMember(parent, key, what, out var localWhat, out var node)
            ? EnsureStringList(node, localWhat)
            : [];

    // ------------------------------------------------------------------ internals

    private static string ResolveWhat(string what, string key) => what.Replace(Placeholder, $"'{key}'");

    private static JsonNode? RequireMember(JsonObject parent, string key, string localWhat)
    {
        ArgumentNullException.ThrowIfNull(parent);

        // Upstream message reads e.g. "'id's parent does not contain 'id'".
        return parent.ContainsKey(key)
            ? parent[key]
            : throw new JsonException($"{localWhat}s parent does not contain {localWhat}");
    }

    private static bool EnsureMember(
        JsonObject parent,
        string key,
        string what,
        out string localWhat,
        out JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(parent);

        localWhat = ResolveWhat(what, key);

        if (!parent.ContainsKey(key))
        {
            node = null;
            return false;
        }

        node = parent[key];
        return true;
    }

    /// <summary>Undefined and null both degrade to the default, matching <c>ensureIsType</c>.</summary>
    private static T Ensure<T>(JsonNode? value, T @default, Func<T> require)
    {
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return @default;
        }

        try
        {
            return require();
        }
        catch (JsonException)
        {
            return @default;
        }
    }

    private static byte[] FromHexLenient(string text)
    {
        var nibbles = new List<byte>(text.Length);

        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                nibbles.Add((byte)Uri.FromHex(c));
            }
        }

        // Qt pads an odd leading nibble, decoding "abc" as 0x0a 0xbc.
        var result = new byte[nibbles.Count / 2 + nibbles.Count % 2];
        var offset = nibbles.Count % 2;

        if (offset == 1 && nibbles.Count > 0)
        {
            result[0] = nibbles[0];
        }

        for (var i = offset; i < nibbles.Count; i += 2)
        {
            result[(i + offset) / 2] = (byte)((nibbles[i] << 4) | nibbles[i + 1]);
        }

        return result;
    }
}
