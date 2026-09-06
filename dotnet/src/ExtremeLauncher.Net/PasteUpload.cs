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
 * Ported from launcher/net/PasteUpload.cpp.
 *
 * PUTTING A LOG SOMEWHERE SOMEBODY CAN READ IT. The single most useful thing a person asking for help
 * can do is hand over their log, and the single most annoying way to do it is paste 4,000 lines into
 * a chat window. Every Minecraft support channel runs on paste links.
 *
 * FOUR SERVICES, upstream's, and they agree about NOTHING -- not the request body, not the content
 * type, not where the resulting link comes from. That is the entire complexity of this file:
 *
 *   0x0.st      multipart form upload; the link is the RESPONSE BODY, as plain text
 *   hastebin    the raw text as the whole body; the link is built from a "key" in the JSON
 *   paste.gg    a JSON envelope with a files array; the link is built from result.id
 *   mclo.gs     form-urlencoded "content="; the link is "url" in the JSON, and there is a
 *               success flag that can be FALSE with a 200, which is the one shape that would
 *               otherwise produce a link to nothing
 *
 * MCLO.GS IS THE DEFAULT, and it is the right default: it parses Minecraft logs, folds the stack
 * traces, and highlights known errors, so the person helping gets something better than raw text.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Net;

/// <summary>Which paste service to use.</summary>
/// <remarks>
/// THE NUMBERS ARE THE COMPATIBILITY SURFACE. Upstream stores this setting as the raw integer value
/// of its enum, so the ORDER of these members is on-disk format shared with it -- a new service may
/// only ever be added at the end, and none may be removed. This is a poor way to store a setting and
/// it is not this port's to change.
/// </remarks>
public enum PasteType
{
    /// <summary>0x0.st</summary>
    NullPointer = 0,

    /// <summary>hastebin</summary>
    Hastebin = 1,

    /// <summary>paste.gg</summary>
    PasteGG = 2,

    /// <summary>mclo.gs -- the default, and the only one that understands Minecraft logs.</summary>
    Mclogs = 3,
}

/// <summary>What a service is called and where it lives.</summary>
/// <param name="Retired">
/// Whether the service is gone. Checked 2026-08-20: paste.gg's domain does not resolve at all, while
/// 0x0.st, hst.sh and api.mclo.gs all answer.
/// </param>
public sealed record PasteTypeInfo(
    PasteType Type,
    string Name,
    string DefaultBase,
    string EndpointPath,
    bool Retired = false)
{
    /// <summary>What to call it on screen, saying so when it no longer exists.</summary>
    public string Label => Retired ? Name + " (no longer available)" : Name;
}

/// <summary>Uploads text to a paste service and reports the link.</summary>
public sealed class PasteUpload : LauncherTask
{
    /// <summary>Upstream's table, in enum order.</summary>
    public static readonly IReadOnlyList<PasteTypeInfo> PasteTypes =
    [
        new(PasteType.NullPointer, "0x0.st", "https://0x0.st", ""),
        new(PasteType.Hastebin, "hastebin", "https://hst.sh", "/documents"),
        /*
         * RETIRED. paste.gg's domain stopped resolving -- not a 404, no DNS record at all. Upstream
         * still offers it, so somebody picking it gets a failure that reads like their own network
         * being broken.
         *
         * KEPT IN THE LIST ANYWAY, because the enum's numbers are on-disk format: a config out there
         * says PastebinType=2, and removing this entry would renumber mclo.gs. It stays offered and
         * says what it is, which is better than vanishing from the picker and silently switching
         * somebody to a service they did not choose.
         */
        new(PasteType.PasteGG, "paste.gg", "https://paste.gg", "/api/v1/pastes", Retired: true),
        new(PasteType.Mclogs, "mclo.gs", "https://api.mclo.gs", "/1/log"),
    ];

    private readonly HttpClient _client;
    private readonly string _text;
    private readonly PasteType _type;
    private readonly string _baseUrl;
    private readonly string _uploadUrl;

    public PasteUpload(HttpClient client, string text, PasteType type, string customBase = "")
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _text = text ?? string.Empty;
        _type = type;
        _baseUrl = customBase.Length != 0 ? customBase.TrimEnd('/') : Info(type).DefaultBase;

        _uploadUrl = UploadUrlFor(type, _baseUrl, customBase.Length != 0);
    }

    /// <summary>The link, once the upload has succeeded.</summary>
    public string PasteLink { get; private set; } = string.Empty;

    /// <summary>Where the upload will be sent, for the confirmation dialog.</summary>
    public string UploadUrl => _uploadUrl;

    public static PasteTypeInfo Info(PasteType type)
        => PasteTypes.FirstOrDefault(t => t.Type == type) ?? PasteTypes[(int)PasteType.Mclogs];

    /// <summary>The host a paste would go to, for the "are you sure" prompt.</summary>
    /// <remarks>
    /// A HOST, NOT A URL, because that is the part somebody can actually judge. "Upload to
    /// https://api.mclo.gs/1/log?" invites nobody to think; "upload to api.mclo.gs?" does.
    /// </remarks>
    public static string HostFor(PasteType type, string customBase)
    {
        var raw = customBase.Length != 0 ? customBase : Info(type).DefaultBase;

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri.Host : raw;
    }

    private static string UploadUrlFor(PasteType type, string baseUrl, bool custom)
    {
        /*
         * HACK, upstream's, comment and all: paste.gg documents its API at /api/<version> but the
         * official instance does not serve it there. Only the default base gets the substitution --
         * somebody running their own instance presumably followed the documentation.
         */
        if (type == PasteType.PasteGG && !custom)
        {
            return "https://api.paste.gg/v1/pastes";
        }

        return baseUrl + Info(type).EndpointPath;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus($"Uploading to {_uploadUrl}");

        using var request = new HttpRequestMessage(HttpMethod.Post, _uploadUrl) { Content = BuildBody() };

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Upstream accepts 200 and 201 and nothing else. 201 is what a created paste properly is.
        if (response.StatusCode is not (System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Created))
        {
            throw new TaskFailedException(
                $"Error: {_uploadUrl} returned unexpected status code "
                + $"{((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}");
        }

        PasteLink = ReadLink(body);
    }

    private HttpContent BuildBody()
    {
        switch (_type)
        {
            case PasteType.NullPointer:
            {
                // A multipart form with one file part, which is what 0x0.st takes.
                var multipart = new MultipartFormDataContent();
                var file = new StringContent(_text, Encoding.UTF8, "text/plain");

                multipart.Add(file, "file", "log.txt");

                return multipart;
            }

            case PasteType.Hastebin:
                // The raw text, as the entire body.
                return new StringContent(_text, Encoding.UTF8);

            case PasteType.Mclogs:
                /*
                 * Form-urlencoded. FormUrlEncodedContent has a length limit that a 5 MB log walks
                 * straight past (it builds the escaped string through a Uri helper), so the body is
                 * encoded here instead. A log big enough to hit that is exactly the log somebody most
                 * needs to share.
                 */
                return new StringContent(
                    "content=" + Uri.EscapeDataString(_text),
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded");

            case PasteType.PasteGG:
            {
                var payload = new JsonObject
                {
                    // Upstream's hundred days. A log nobody has looked at in three months is not one
                    // anybody is still helping with.
                    ["expires"] = DateTimeOffset.UtcNow.AddDays(100).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["files"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "log.txt",
                            ["content"] = new JsonObject
                            {
                                ["format"] = "text",
                                ["value"] = _text,
                            },
                        },
                    },
                };

                return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            }

            default:
                throw new TaskFailedException($"Unknown paste service {_type}");
        }
    }

    private string ReadLink(string body)
    {
        switch (_type)
        {
            case PasteType.NullPointer:
                // The body IS the link. Nothing to parse and nothing to go wrong.
                return body.Trim();

            case PasteType.Hastebin:
            {
                var key = Root(body)?["key"]?.GetValue<string>();

                if (string.IsNullOrEmpty(key))
                {
                    throw new TaskFailedException($"Error: {_uploadUrl} returned a malformed response body");
                }

                return _baseUrl + "/" + key;
            }

            case PasteType.Mclogs:
            {
                var root = Root(body) ?? throw new TaskFailedException($"Error: {_uploadUrl} returned a malformed response body");

                if (root["success"] is not JsonValue flag || !flag.TryGetValue<bool>(out var success))
                {
                    throw new TaskFailedException($"Error: {_uploadUrl} returned a malformed response body");
                }

                if (!success)
                {
                    /*
                     * A 200 WITH success:false. Trusting the status code alone here would hand
                     * somebody a link to nothing and tell them it worked.
                     */
                    throw new TaskFailedException(
                        $"Error: {_uploadUrl} returned an error: {root["error"]?.GetValue<string>() ?? "unknown"}");
                }

                var url = root["url"]?.GetValue<string>();

                return string.IsNullOrEmpty(url)
                    ? throw new TaskFailedException($"Error: {_uploadUrl} returned a malformed response body")
                    : url;
            }

            case PasteType.PasteGG:
            {
                var root = Root(body) ?? throw new TaskFailedException($"Error: {_uploadUrl} returned a malformed response body");

                if (root["status"]?.GetValue<string>() is not { } status)
                {
                    throw new TaskFailedException($"Error: {_uploadUrl} returned a malformed response body");
                }

                if (status != "success")
                {
                    var error = root["error"]?.GetValue<string>() ?? "unknown";
                    var message = root["message"]?.GetValue<string>() ?? "none";

                    throw new TaskFailedException(
                        $"Error: {_uploadUrl} returned an error code: {error}\nError message: {message}");
                }

                var id = root["result"]?["id"]?.GetValue<string>();

                return string.IsNullOrEmpty(id)
                    ? throw new TaskFailedException($"Error: {_uploadUrl} returned a malformed response body")
                    : _baseUrl + "/p/anonymous/" + id;
            }

            default:
                throw new TaskFailedException($"Unknown paste service {_type}");
        }
    }

    private static JsonObject? Root(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            // A service returning HTML -- a proxy error page, say -- is not an exception worth
            // showing; it is a malformed body, which the caller already has a message for.
            return null;
        }
    }
}
