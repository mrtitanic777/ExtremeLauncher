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
 * Characterization tests for the Json port. There is no upstream Qt test for launcher/Json.cpp.
 */

using System.Text;
using System.Text.Json.Nodes;
using Xunit;

using Json = ExtremeLauncher.Core.Json;
using JsonException = ExtremeLauncher.Core.JsonException;

namespace ExtremeLauncher.Core.Tests;

public sealed class JsonTests
{
    private static JsonObject Parse(string json) => Json.RequireObject(Json.RequireDocument(json));

    [Fact]
    public void RequireDocumentRejectsMalformedJson()
    {
        var e = Assert.Throws<JsonException>(() => Json.RequireDocument("{ not json"));
        Assert.Contains("Error parsing JSON", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireObjectAndArrayEnforceTheDocumentShape()
    {
        Assert.Throws<JsonException>(() => Json.RequireObject(Json.RequireDocument("[1,2,3]")));
        Assert.Throws<JsonException>(() => Json.RequireArray(Json.RequireDocument("{\"a\":1}")));

        Assert.Equal(3, Json.RequireArray(Json.RequireDocument("[1,2,3]")).Count);
    }

    [Fact]
    public void RequireStringReadsAndRejectsByType()
    {
        var obj = Parse("""{"name":"Extreme","count":3}""");

        Assert.Equal("Extreme", Json.RequireString(obj, "name"));
        Assert.Throws<JsonException>(() => Json.RequireString(obj, "count"));
    }

    [Fact]
    public void RequireOnMissingKeyReportsTheKeyName()
    {
        var obj = Parse("""{"a":1}""");

        var e = Assert.Throws<JsonException>(() => Json.RequireString(obj, "missing"));

        // Upstream message shape: "'missing's parent does not contain 'missing'".
        Assert.Equal("'missing's parent does not contain 'missing'", e.Message);
    }

    [Fact]
    public void EnsureFallsBackInsteadOfThrowing()
    {
        var obj = Parse("""{"name":"Extreme","count":3,"nothing":null}""");

        Assert.Equal("fallback", Json.EnsureString(obj, "missing", "fallback"));
        Assert.Equal("fallback", Json.EnsureString(obj, "count", "fallback"));   // wrong type
        Assert.Equal("fallback", Json.EnsureString(obj, "nothing", "fallback")); // explicit null
        Assert.Equal("Extreme", Json.EnsureString(obj, "name", "fallback"));
    }

    [Fact]
    public void IntegerRejectsFractionalNumbers()
    {
        var obj = Parse("""{"whole":42,"fraction":1.5,"negative":-7}""");

        Assert.Equal(42, Json.RequireInteger(obj, "whole"));
        Assert.Equal(-7, Json.RequireInteger(obj, "negative"));
        Assert.Throws<JsonException>(() => Json.RequireInteger(obj, "fraction"));
        Assert.Equal(0, Json.EnsureInteger(obj, "fraction"));
    }

    [Fact]
    public void BooleanAndDoubleAreTypeChecked()
    {
        var obj = Parse("""{"flag":true,"num":2.5,"text":"no"}""");

        Assert.True(Json.RequireBoolean(obj, "flag"));
        Assert.Equal(2.5, Json.RequireDouble(obj, "num"));
        Assert.Throws<JsonException>(() => Json.RequireBoolean(obj, "text"));
        Assert.Throws<JsonException>(() => Json.RequireDouble(obj, "text"));
    }

    [Fact]
    public void DateTimeRequiresIsoFormat()
    {
        var obj = Parse("""{"iso":"2024-08-16T12:34:56Z","dateOnly":"2024-08-16","american":"08/16/2024"}""");

        Assert.Equal(
            new DateTimeOffset(2024, 8, 16, 12, 34, 56, TimeSpan.Zero),
            Json.RequireDateTime(obj, "iso"));

        Assert.Equal(2024, Json.RequireDateTime(obj, "dateOnly").Year);

        // Would be accepted by a plain DateTimeOffset.TryParse, but Qt::ISODate rejects it.
        Assert.Throws<JsonException>(() => Json.RequireDateTime(obj, "american"));
    }

    [Fact]
    public void UuidRequiresTheBracedRoundTrippableForm()
    {
        var braced = Guid.NewGuid().ToString("B");
        var obj = Parse($$"""{"braced":"{{braced}}","bare":"not-a-uuid"}""");

        Assert.Equal(Guid.Parse(braced), Json.RequireUuid(obj, "braced"));
        Assert.Throws<JsonException>(() => Json.RequireUuid(obj, "bare"));
    }

    [Fact]
    public void UuidRejectsUnbracedFormBecauseItDoesNotRoundTrip()
    {
        // QUuid::toString() emits braces, and upstream requires toString() == input.
        var obj = Parse($$"""{"id":"{{Guid.NewGuid():D}}"}""");

        Assert.Throws<JsonException>(() => Json.RequireUuid(obj, "id"));
    }

    [Fact]
    public void ByteArrayDecodesHexLeniently()
    {
        var obj = Parse("""{"even":"616f6b","odd":"abc","dirty":"61-6f-6b"}""");

        Assert.Equal(Encoding.ASCII.GetBytes("aok"), Json.RequireByteArray(obj, "even"));

        // QByteArray::fromHex pads an odd leading nibble.
        Assert.Equal(new byte[] { 0x0a, 0xbc }, Json.RequireByteArray(obj, "odd"));

        // ...and skips characters that are not hex digits rather than failing.
        Assert.Equal(Encoding.ASCII.GetBytes("aok"), Json.RequireByteArray(obj, "dirty"));
    }

    [Fact]
    public void UrlReturnsNullForEmptyAndParsesOtherwise()
    {
        var obj = Parse("""{"url":"https://extremelauncher.net/download/","empty":"","number":5}""");

        Assert.Equal(new Uri("https://extremelauncher.net/download/"), Json.RequireUrl(obj, "url"));
        Assert.Null(Json.RequireUrl(obj, "empty"));

        // Upstream reads the inner string with ensure, not require, so a non-string degrades to empty.
        Assert.Null(Json.RequireUrl(obj, "number"));
    }

    [Fact]
    public void WriteStringSkipsEmptyValues()
    {
        var obj = new JsonObject();

        Json.WriteString(obj, "kept", "value");
        Json.WriteString(obj, "dropped", string.Empty);

        Assert.True(obj.ContainsKey("kept"));
        Assert.False(obj.ContainsKey("dropped"));
    }

    [Fact]
    public void WriteStringListSkipsEmptyLists()
    {
        var obj = new JsonObject();

        Json.WriteStringList(obj, "kept", ["a", "b"]);
        Json.WriteStringList(obj, "dropped", []);

        Assert.Equal(2, Json.RequireArray(obj, "kept").Count);
        Assert.False(obj.ContainsKey("dropped"));
    }

    [Fact]
    public void ArrayOfHelpersConvertElements()
    {
        var obj = Parse("""{"names":["a","b","c"],"mixed":["a",1]}""");

        Assert.Equal(["a", "b", "c"], Json.EnsureStringList(obj, "names"));
        Assert.Empty(Json.EnsureStringList(obj, "missing"));

        Assert.Throws<JsonException>(() => Json.RequireArrayOf(obj["mixed"], (n, w) => Json.RequireString(n, w)));
    }

    [Fact]
    public void ToTextIsCompactAndRoundTrips()
    {
        var obj = Parse("""{ "a" : 1, "b" : [ 2, 3 ] }""");
        var text = Encoding.UTF8.GetString(Json.ToText(obj));

        Assert.Equal("""{"a":1,"b":[2,3]}""", text);
        Assert.Equal(1, Json.RequireInteger(Parse(text), "a"));
    }

    [Fact]
    public void NumbersReadBackFromInMemoryNodesNotJustParsedText()
    {
        // A node parsed from text wraps a JsonElement and converts numeric types freely; one built in
        // memory wraps the CLR value and is far pickier. Every serialize-then-reread path uses the
        // latter, so both must work.
        var built = new JsonObject
        {
            ["asInt"] = 18,
            ["asLong"] = 4_000_000_000L,
            ["asDouble"] = 1.5,
        };

        Assert.Equal(18, Json.RequireInteger(built, "asInt"));
        Assert.Equal(18d, Json.RequireDouble(built, "asInt"));
        Assert.Equal(4_000_000_000d, Json.RequireDouble(built, "asLong"));
        Assert.Equal(1.5, Json.RequireDouble(built, "asDouble"));
    }

    [Fact]
    public void WriteAndReadRoundTripThroughAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"el-json-{Guid.NewGuid():N}.json");

        try
        {
            var written = Parse("""{"name":"Extreme","versions":[1,2]}""");
            Json.Write(written, path);

            var read = Json.RequireObject(Json.RequireDocumentFromFile(path));

            Assert.Equal("Extreme", Json.RequireString(read, "name"));
            Assert.Equal(2, Json.RequireArray(read, "versions").Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RequireDocumentFromFileSurfacesMissingFilesAsFileSystemErrors()
        => Assert.Throws<FileSystemException>(
            () => Json.RequireDocumentFromFile(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));

    [Fact]
    public void CustomWhatFlowsIntoMessages()
    {
        var obj = Parse("""{"count":"three"}""");

        var e = Assert.Throws<JsonException>(() => Json.RequireInteger(obj, "count", "The pack's __placeholder__ field"));

        Assert.Equal("The pack's 'count' field is not a double", e.Message);
    }

    [Fact]
    public void ALeadingByteOrderMarkIsSkipped()
    {
        /*
         * NOT JSON, BUT EVERYWHERE. System.Text.Json refuses a document starting EF BB BF with
         * "'0xEF' is an invalid start of a value"; Qt's QJsonDocument::fromJson skips it. So a pack
         * upstream reads happily was, until this, unreadable here -- and the person who wrote the
         * file cannot see the difference or guess why one launcher calls their pack corrupt.
         *
         * Found by a .mrpack fixture written with Encoding.UTF8, which emits one.
         */
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("""{"name":"a pack"}"""))
            .ToArray();

        var obj = Json.RequireObject(Json.RequireDocument(withBom, "modrinth.index.json"));

        Assert.Equal("a pack", Json.RequireString(obj, "name"));
    }

    [Fact]
    public void OnlyARealByteOrderMarkIsSkipped()
    {
        // Three bytes that merely start with 0xEF are not a BOM, and eating them would corrupt a
        // document that happened to be valid.
        Assert.Throws<JsonException>(() => Json.RequireDocument(new byte[] { 0xEF, 0xBB, 0x7B }, "x"));

        // And a document with no BOM is untouched.
        var obj = Json.RequireObject(Json.RequireDocument(Encoding.UTF8.GetBytes("""{"name":"plain"}"""), "x"));

        Assert.Equal("plain", Json.RequireString(obj, "name"));
    }
}
