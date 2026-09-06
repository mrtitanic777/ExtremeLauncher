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
 * Ported from launcher/minecraft/auth/Parsers.{h,cpp}.
 *
 * Every response body the auth chain receives, turned into account data. Nothing here touches the
 * network — these are the pure functions the steps hand their bytes to, which makes the whole
 * subsystem's parsing testable without a live Microsoft login.
 *
 * EVERY PARSER RETURNS false RATHER THAN THROWING, and leaves its output half-written when it does.
 * Callers treat a false as "this response was not usable" and abandon the output; none of them inspect
 * it afterwards. That is inherited, and preserved.
 *
 * The parsers are also inconsistent with each other in ways that matter to anything reading their
 * output — the two profile parsers disagree on skin-variant casing and on what capes are keyed by.
 * Each difference is called out where it happens.
 */

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft.Auth;

public static class Parsers
{
    /*
     * The skins for the MHF_Steve and MHF_Alex accounts, made by a Mojang employee. They are needed
     * because the session server does not return skin urls for default skins.
     */

    private const string SkinUrlSteve =
        "https://textures.minecraft.net/texture/1a4af718455d4aab528e7a61f86fa25e6a369d1768dcb13f7df319a713eb810b";

    private const string SkinUrlAlex =
        "https://textures.minecraft.net/texture/83cee5ca6afcdb171285aa00e8049c297b2dbeba0efb8ff970a5677a1b644032";

    // ================================================================== typed accessors

    /*
     * Qt's isString()/isDouble()/isBool() reject rather than coerce, and every parser below is built
     * out of these. A number where a string was expected has to fail the check, not be stringified.
     */

    public static bool GetString(JsonNode? value, out string result)
    {
        if (value is JsonValue json && json.TryGetValue<string>(out var text))
        {
            result = text;
            return true;
        }

        result = string.Empty;
        return false;
    }

    public static bool GetNumber(JsonNode? value, out double result)
    {
        if (value is JsonValue json && json.TryGetValue<double>(out var number))
        {
            result = number;
            return true;
        }

        result = 0;
        return false;
    }

    public static bool GetBool(JsonNode? value, out bool result)
    {
        if (value is JsonValue json && json.TryGetValue<bool>(out var flag))
        {
            result = flag;
            return true;
        }

        result = false;
        return false;
    }

    /// <remarks>
    /// Qt parses these with Qt::ISODate, which accepts the trailing "Z" and any number of fractional
    /// digits — Xbox sends seven of them. .NET's round-trip parse accepts the same shape. A string
    /// without a zone offset is read as UTC here; Qt would call it local time, but every timestamp
    /// these parsers see carries a zone.
    /// </remarks>
    public static bool GetDateTime(JsonNode? value, out DateTimeOffset result)
    {
        result = default;

        return GetString(value, out var text)
               && DateTimeOffset.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                   out result);
    }

    // ================================================================== Xbox tokens

    /// <summary>
    /// Reads one link in the Xbox token chain — user token, XSTS token or the Xbox API token.
    /// </summary>
    /// <remarks>
    /// The shape:
    /// <code>
    /// {
    ///    "IssueInstant": "2020-12-07T19:52:08.4463796Z",
    ///    "NotAfter":     "2020-12-21T19:52:08.4463796Z",
    ///    "Token":        "token",
    ///    "DisplayClaims": { "xui": [ { "uhs": "userhash" } ] }
    /// }
    /// </code>
    ///
    /// NOT HANDLED, as upstream does not handle it either: the error shape, which arrives with the same
    /// 200-ish framing and an "XErr" code — 2148916233 for a missing Xbox account, 2148916238 for a
    /// child account not linked to a family. Both currently surface as an unhelpful parse failure.
    /// </remarks>
    public static bool ParseXTokenResponse(byte[] data, Token output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (ParseObject(data) is not { } obj)
        {
            return false;
        }

        if (!GetDateTime(obj["IssueInstant"], out var issued))
        {
            // IssueInstant is not a timestamp.
            return false;
        }

        output.IssueInstant = issued;

        if (!GetDateTime(obj["NotAfter"], out var expires))
        {
            return false;
        }

        output.NotAfter = expires;

        if (!GetString(obj["Token"], out var token))
        {
            return false;
        }

        output.Value = token;

        if ((obj["DisplayClaims"] as JsonObject)?["xui"] is not JsonArray claims)
        {
            // Missing xui claims array.
            return false;
        }

        var foundUserHash = false;

        foreach (var item in claims)
        {
            if (item is not JsonObject claim || !claim.ContainsKey("uhs"))
            {
                continue;
            }

            foundUserHash = true;

            // Consume all "display claims", whatever that means. Any non-string among them fails the
            // whole response rather than being skipped.
            foreach (var (key, value) in claim)
            {
                if (!GetString(value, out var text))
                {
                    return false;
                }

                output.Extra[key] = text;
            }

            // Only the first claim carrying a user hash is used.
            break;
        }

        if (!foundUserHash)
        {
            return false;
        }

        output.Validity = Validity.Certain;
        return true;
    }

    // ================================================================== the profile endpoint

    /// <summary>Reads a profile from api.minecraftservices.com/minecraft/profile.</summary>
    public static bool ParseMinecraftProfile(byte[] data, MinecraftProfile output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (ParseObject(data) is not { } obj)
        {
            return false;
        }

        if (!GetString(obj["id"], out var id) || !GetString(obj["name"], out var name))
        {
            return false;
        }

        output.Id = id;
        output.Name = name;

        foreach (var entry in obj["skins"] as JsonArray ?? [])
        {
            if (entry is not JsonObject skinObject)
            {
                continue;
            }

            // A skin missing any field is skipped rather than failing the profile — an inactive or
            // malformed entry should not cost the user their name and capes.
            if (!GetString(skinObject["id"], out var skinId)
                || !GetString(skinObject["state"], out var state)
                || state != "ACTIVE"
                || !GetString(skinObject["url"], out var url)
                || !GetString(skinObject["variant"], out var variant))
            {
                continue;
            }

            output.Skin = new Skin
            {
                Id = skinId,

                // Mojang still serves these over plain http; the launcher will not.
                Url = url.Replace("http://textures.minecraft.net", "https://textures.minecraft.net", StringComparison.Ordinal),
                Variant = variant,
            };

            // Only the active skin is of interest.
            break;
        }

        var currentCape = string.Empty;

        foreach (var entry in obj["capes"] as JsonArray ?? [])
        {
            if (entry is not JsonObject capeObject)
            {
                continue;
            }

            if (!GetString(capeObject["id"], out var capeId) || !GetString(capeObject["state"], out var state))
            {
                continue;
            }

            if (state == "ACTIVE")
            {
                // UPSTREAM BUG (#6), preserved: this is set BEFORE the url and alias are validated, so
                // an active cape missing either one leaves CurrentCape naming a cape that never makes
                // it into the map. Reproduced deliberately — the account writer already guards against
                // an unknown CurrentCape on the way back in, so the damage is contained, and changing
                // it here would diverge from what upstream writes to disk.
                currentCape = capeId;
            }

            if (!GetString(capeObject["url"], out var url) || !GetString(capeObject["alias"], out var alias))
            {
                continue;
            }

            output.Capes[capeId] = new Cape { Id = capeId, Url = url, Alias = alias };
        }

        output.CurrentCape = currentCape;
        output.Validity = Validity.Certain;

        return true;
    }

    // ================================================================== the session server

    /// <summary>
    /// Reads a profile from the session server rather than the profile endpoint.
    /// </summary>
    /// <remarks>
    /// Used because locked Mojang accounts cannot reach api.minecraftservices.com/minecraft/profile.
    /// The interesting part is base64 inside a property value:
    /// <code>
    /// { "id": "...", "name": "...", "properties": [ { "name": "textures", "value": "&lt;base64&gt;" } ] }
    /// </code>
    /// decoding to
    /// <code>
    /// { "textures": { "SKIN": { "url": "..." }, "CAPE": { "url": "..." } } }
    /// </code>
    ///
    /// This parser and <see cref="ParseMinecraftProfile"/> disagree in two ways that anything reading
    /// their output has to know about, and both are inherited: the skin VARIANT is upper case here
    /// ("CLASSIC"/"SLIM") and lower case there, and capes are keyed by the literal string "cape" here
    /// rather than by id, because the session server does not return cape ids.
    /// </remarks>
    /// <exception cref="JsonException">The body, or the decoded texture payload, is not an object.</exception>
    public static bool ParseMinecraftProfileMojang(byte[] data, MinecraftProfile output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (ParseNode(data) is not { } document)
        {
            return false;
        }

        // Note the asymmetry with every other parser here: this one demands an object rather than
        // treating a non-object as an empty one, so a plain-text error body throws out of a function
        // whose other failures are all a returned false. Inherited.
        var obj = Json.RequireObject(document, "mojang minecraft profile");

        if (!GetString(obj["id"], out var id) || !GetString(obj["name"], out var name))
        {
            return false;
        }

        output.Id = id;
        output.Name = name;

        var texturePayload = Array.Empty<byte>();

        foreach (var entry in obj["properties"] as JsonArray ?? [])
        {
            if (entry is not JsonObject property
                || !GetString(property["name"], out var propertyName)
                || propertyName != "textures")
            {
                continue;
            }

            if (GetString(property["value"], out var value))
            {
                texturePayload = DecodeBase64(value);
            }

            if (texturePayload.Length != 0)
            {
                break;
            }
        }

        if (texturePayload.Length == 0)
        {
            // No texture payload data.
            return false;
        }

        if (ParseNode(texturePayload) is not { } payload)
        {
            return false;
        }

        if (Json.RequireObject(payload, "session texture payload")["textures"] is not JsonObject textures)
        {
            return false;
        }

        // The endpoint does not say which default skin applies, so it is derived from the uuid.
        var steve = IsDefaultModelSteve(output.Id);

        var skin = new Skin
        {
            Variant = steve ? "CLASSIC" : "SLIM",
            Url = steve ? SkinUrlSteve : SkinUrlAlex,

            // Not discoverable from this endpoint, and nothing downstream depends on it.
            Id = "00000000-0000-0000-0000-000000000000",
        };

        var cape = new Cape();

        foreach (var (key, value) in textures)
        {
            if (value is not JsonObject texture)
            {
                continue;
            }

            switch (key)
            {
                case "SKIN":
                    if (!GetString(texture["url"], out var skinUrl))
                    {
                        return false;
                    }

                    skin.Url = skinUrl;

                    // Optional: absent for the default model, present for slim.
                    if (texture["metadata"] is JsonObject metadata && GetString(metadata["model"], out var model))
                    {
                        skin.Variant = model;
                    }

                    break;

                case "CAPE":
                    if (!GetString(texture["url"], out var capeUrl))
                    {
                        return false;
                    }

                    cape.Url = capeUrl;

                    // The session server does not return a cape id, so the alias stands in as the key.
                    // Changing capes is locked on these accounts anyway.
                    cape.Alias = "cape";
                    break;
            }
        }

        output.Skin = skin;

        if (cape.Alias == "cape")
        {
            output.Capes.Clear();
            output.Capes[cape.Alias] = cape;
            output.CurrentCape = cape.Alias;
        }

        output.Validity = Validity.Certain;
        return true;
    }

    /// <summary>
    /// Whether an account without a stored skin gets Steve rather than Alex.
    /// </summary>
    /// <remarks>
    /// The game picks the default model from the parity of the UUID's Java hashCode: even is Steve,
    /// odd is Alex. That hash is the two 64-bit halves XORed together, then folded to 32 bits.
    ///
    /// A UUID that is not 32 hex digits gets Steve, and so does one whose halves do not parse — Qt's
    /// toULongLong yields 0 on failure, and 0 is even.
    /// </remarks>
    public static bool IsDefaultModelSteve(string uuid)
    {
        // Just in case dashes are in the id.
        var id = uuid.Replace("-", string.Empty, StringComparison.Ordinal);

        if (id.Length != 32)
        {
            return true;
        }

        // Unsigned throughout, so the fold below truncates rather than sign-extending.
        _ = ulong.TryParse(id.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var most);
        _ = ulong.TryParse(id.AsSpan(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var least);

        var xored = most ^ least;

        return (((uint)(xored >> 32)) ^ (uint)xored) % 2 == 0;
    }

    // ================================================================== entitlements and the rest

    /// <summary>Reads what the account is allowed to do with Minecraft.</summary>
    /// <remarks>
    /// Succeeds even when the items array is missing or empty, leaving both flags false. That is the
    /// correct reading: a valid response listing no entitlements means the account owns nothing.
    /// </remarks>
    public static bool ParseMinecraftEntitlements(byte[] data, MinecraftEntitlement output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (ParseObject(data) is not { } obj)
        {
            return false;
        }

        output.CanPlayMinecraft = false;
        output.OwnsMinecraft = false;

        foreach (var entry in obj["items"] as JsonArray ?? [])
        {
            if (entry is not JsonObject item || !GetString(item["name"], out var name))
            {
                continue;
            }

            switch (name)
            {
                case "game_minecraft":
                    output.CanPlayMinecraft = true;
                    break;

                case "product_minecraft":
                    output.OwnsMinecraft = true;
                    break;
            }
        }

        output.Validity = Validity.Certain;
        return true;
    }

    /// <summary>Reads the msamigration rollout flag.</summary>
    public static bool ParseRolloutResponse(byte[] data, out bool result)
    {
        result = false;

        if (ParseObject(data) is not { } obj)
        {
            return false;
        }

        // A response about some other feature is refused rather than read for its rollout flag.
        return GetString(obj["feature"], out var feature)
               && feature == "msamigration"
               && GetBool(obj["rollout"], out result);
    }

    /// <summary>Reads the token returned by api.minecraftservices.com/launcher/login.</summary>
    /// <remarks>
    /// The response carries no absolute expiry, only a lifetime in seconds, so the clock is read here
    /// rather than by the caller. "username" is required to be present and is then discarded — it is a
    /// well-formedness check on the response, not a source of the account's name.
    /// </remarks>
    public static bool ParseMojangResponse(byte[] data, Token output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (ParseObject(data) is not { } obj)
        {
            return false;
        }

        if (!GetNumber(obj["expires_in"], out var expiresIn))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        output.IssueInstant = now;
        output.NotAfter = now.AddSeconds(expiresIn);

        if (!GetString(obj["username"], out _))
        {
            return false;
        }

        // It is a JWT; upstream leaves it unvalidated and so does this.
        if (!GetString(obj["access_token"], out var accessToken))
        {
            return false;
        }

        output.Value = accessToken;
        output.Validity = Validity.Certain;

        return true;
    }

    // ================================================================== plumbing

    private static JsonNode? ParseNode(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data);
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON is a failed parse, not an exception: these bodies come off the network.
            return null;
        }
    }

    /// <remarks>
    /// A body that parses but is not an object stands in as an empty one, so every field lookup below
    /// simply misses. Qt's doc.object() does the same.
    /// </remarks>
    private static JsonObject? ParseObject(byte[] data)
        => ParseNode(data) is { } node ? node as JsonObject ?? [] : null;

    private static byte[] DecodeBase64(string text)
    {
        Span<byte> buffer = new byte[text.Length * 3 / 4 + 3];

        return Convert.TryFromBase64String(text, buffer, out var written) ? buffer[..written].ToArray() : [];
    }

    /// <summary>Convenience for callers holding text rather than bytes.</summary>
    public static byte[] ToBytes(string json) => Encoding.UTF8.GetBytes(json);
}
