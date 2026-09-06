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
 * Ported from launcher/screenshots/ImgurUpload.cpp and ImgurAlbumCreation.cpp.
 *
 * PUTTING A SCREENSHOT SOMEWHERE SHAREABLE. Upload one and you get a link to the image; upload several
 * and they are bundled into an anonymous album and you get one link to the lot -- which is upstream's
 * behaviour and the only reason the album call exists.
 *
 * KEY-GATED, like the paste services and CurseForge. Imgur wants a Client-ID, and this build ships
 * without one (see BuildConfig) -- so IsAvailable is false and the screenshots page hides the button
 * rather than offering an upload that would 401. A fork that fills Launcher_IMGUR_CLIENT_ID in gets it.
 */

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Net;

/// <summary>An uploaded image: the link people share, and the hash that could delete it.</summary>
public sealed record ImgurImage(string Id, string Link, string DeleteHash);

public sealed class ImgurClient
{
    private readonly HttpClient _client;
    private readonly string _clientId;
    private readonly string _baseUrl;

    public ImgurClient(HttpClient client, string clientId, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _clientId = clientId ?? string.Empty;

        // A trailing slash matters: the endpoints are baseUrl + "image" / + "album".
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "https://api.imgur.com/3/" : baseUrl;
    }

    /// <summary>Whether a Client-ID is configured; without one, imgur refuses every request.</summary>
    public bool IsAvailable => _clientId.Length != 0;

    /// <summary>The host an upload goes to, for the confirmation prompt.</summary>
    public string Host => Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) ? uri.Host : _baseUrl;

    /// <summary>Uploads one image and returns its link.</summary>
    public async Task<ImgurImage> UploadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new LauncherException("Screenshot upload is not configured in this build.");
        }

        if (!File.Exists(filePath))
        {
            throw new LauncherException($"There is no file at {filePath}.");
        }

        var title = Path.GetFileNameWithoutExtension(filePath);

        // Multipart, upstream's exact fields: the image bytes, type=file, and the title.
        using var body = new MultipartFormDataContent();

        var image = new ByteArrayContent(await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false));
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        body.Add(image, "image", Path.GetFileName(filePath));
        body.Add(new StringContent("file"), "type");
        body.Add(new StringContent(title), "title");

        var data = await PostAsync(_baseUrl + "image", body, cancellationToken).ConfigureAwait(false);

        return new ImgurImage(
            data["id"]?.GetValue<string>() ?? string.Empty,
            data["link"]?.GetValue<string>() ?? string.Empty,
            data["deletehash"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>Bundles already-uploaded images into an anonymous album and returns its page link.</summary>
    /// <remarks>
    /// FROM THE DELETE HASHES, not the ids, which is what imgur's anonymous-album API takes -- the
    /// delete hash is the only handle an anonymous uploader has on an image it did not authenticate to
    /// create. Upstream titles the album "Minecraft Screenshots" and marks it hidden; kept verbatim.
    /// </remarks>
    public async Task<string> CreateAlbumAsync(
        IReadOnlyList<string> deleteHashes,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new LauncherException("Screenshot upload is not configured in this build.");
        }

        var form = "deletehashes=" + string.Join(',', deleteHashes)
                   + "&title=Minecraft%20Screenshots&privacy=hidden";

        using var body = new StringContent(form, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        var data = await PostAsync(_baseUrl + "album", body, cancellationToken).ConfigureAwait(false);

        var id = data["id"]?.GetValue<string>() ?? string.Empty;

        // The album's shareable page, which is not the API URL: imgur.com/a/<id>.
        return id.Length != 0 ? $"https://imgur.com/a/{id}" : string.Empty;
    }

    /// <summary>Posts, checks imgur's success envelope, and returns its "data" object.</summary>
    private async Task<JsonObject> PostAsync(string url, HttpContent content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        // Client-ID auth, and Accept: application/json so a failure comes back as JSON to parse rather
        // than an HTML error page.
        request.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {_clientId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonObject? root;

        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new LauncherException("Imgur did not reply with JSON.");
        }

        /*
         * THE SUCCESS FLAG, NOT THE STATUS CODE. Imgur wraps everything in { success, status, data },
         * and a rejected upload can still carry a 200-shaped envelope with success:false -- trusting
         * the transport would hand back an empty link. Upstream checks the flag; so does this.
         */
        if (root is null || root["success"]?.GetValue<bool>() != true)
        {
            throw new LauncherException(
                $"Imgur upload failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        return root["data"] as JsonObject
               ?? throw new LauncherException("Imgur reply had no data.");
    }
}
