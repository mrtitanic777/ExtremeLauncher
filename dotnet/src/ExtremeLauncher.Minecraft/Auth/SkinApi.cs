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
 * Ported from launcher/minecraft/skins/{SkinUpload,CapeChange,SkinDelete}.cpp. Apache-2.0, like the
 * files it came from.
 *
 * THE FOUR CALLS THAT CHANGE HOW A PLAYER LOOKS. Unlike everything else in this port, these are
 * MUTATING requests against the user's own Mojang account -- uploading a skin replaces the one they
 * have, and removing a cape unequips it for every client they use, not just this launcher.
 *
 * They are also the shortest possible read of a REST API: three URLs, four methods, one header. So
 * what is worth testing is precisely what goes on the wire, and the requests are built separately from
 * being sent so a test can look at one without a Mojang account.
 *
 * EACH IS ITS OWN VERB, and Mojang chose them well: PUT a cape to equip it, DELETE the same path to
 * remove it, DELETE the active skin to fall back to the default. There is no "unset" body to get
 * wrong, which is why removing a cape and removing a skin look almost identical here.
 */

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ExtremeLauncher.Minecraft.Auth;

/// <summary>Which arm model a skin is drawn for.</summary>
public enum SkinModel
{
    /// <summary>Four-pixel arms. The original.</summary>
    Classic,

    /// <summary>Three-pixel arms, added with the Alex model.</summary>
    Slim,
}

public static class SkinApi
{
    public const string BaseUrl = "https://api.minecraftservices.com/minecraft/profile";

    public static string SkinsUrl => BaseUrl + "/skins";

    public static string ActiveSkinUrl => BaseUrl + "/skins/active";

    public static string ActiveCapeUrl => BaseUrl + "/capes/active";

    /// <summary>
    /// Mojang's spelling of a model, which is UPPERCASE.
    /// </summary>
    /// <remarks>
    /// Worth pinning: the same two words appear lowercase in the profile responses this launcher
    /// parses elsewhere, so the casing is a property of the endpoint rather than of the concept.
    /// </remarks>
    public static string ToVariant(SkinModel model) => model == SkinModel.Slim ? "SLIM" : "CLASSIC";

    /// <summary>Uploads a skin, replacing whatever the account currently has.</summary>
    /// <remarks>
    /// A multipart form of exactly two parts. The filename is always "skin.png" regardless of what the
    /// file is called on disk — Mojang requires a filename and does not care which, so sending the
    /// user's own would leak a path fragment for no benefit.
    /// </remarks>
    public static HttpRequestMessage CreateUploadSkinRequest(string accessToken, byte[] png, SkinModel model)
    {
        ArgumentNullException.ThrowIfNull(png);

        var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        content.Add(file, "file", "skin.png");
        content.Add(new StringContent(ToVariant(model)), "variant");

        return Authorized(new HttpRequestMessage(HttpMethod.Post, SkinsUrl) { Content = content }, accessToken);
    }

    /// <summary>
    /// Removes the active skin, so the account falls back to its default.
    /// </summary>
    /// <remarks>
    /// Not "upload a blank skin": the default is derived from the account's UUID, and there is no
    /// image that reproduces it. Deleting is the only way back.
    /// </remarks>
    public static HttpRequestMessage CreateDeleteSkinRequest(string accessToken)
        => Authorized(new HttpRequestMessage(HttpMethod.Delete, ActiveSkinUrl), accessToken);

    /// <summary>Equips a cape, or unequips whatever is active when the id is empty.</summary>
    /// <remarks>
    /// ONE FUNCTION FOR BOTH, as upstream has it, because the endpoint is the same and only the verb
    /// differs. An empty id meaning "remove" reads oddly in isolation and is exactly right at the call
    /// site: the cape picker has a "none" entry, and it is the same action with no argument.
    /// </remarks>
    public static HttpRequestMessage CreateChangeCapeRequest(string accessToken, string capeId)
    {
        ArgumentNullException.ThrowIfNull(capeId);

        if (capeId.Length == 0)
        {
            return Authorized(new HttpRequestMessage(HttpMethod.Delete, ActiveCapeUrl), accessToken);
        }

        /*
         * Built as JSON rather than interpolated into a string. Upstream writes
         * `QString("{\"capeId\":\"%1\"}").arg(m_capeId)` -- which is fine for the UUIDs Mojang issues
         * and wrong the moment anything else reaches it, since a quote in the id would produce a
         * malformed body. Serialising costs nothing and cannot be got wrong.
         */
        var body = new JsonObject { ["capeId"] = capeId };

        return Authorized(
            new HttpRequestMessage(HttpMethod.Put, ActiveCapeUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            },
            accessToken);
    }

    /// <summary>Adds the bearer token every one of these calls needs.</summary>
    private static HttpRequestMessage Authorized(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return request;
    }
}
