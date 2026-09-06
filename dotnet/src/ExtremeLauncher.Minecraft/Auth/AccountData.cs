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
 * Ported from launcher/minecraft/auth/AccountData.{h,cpp}.
 *
 * The stored shape of an account, and the reader and writer for accounts.json's "v3" format. This is
 * the one file in the auth subsystem that must be byte-honest with what is already on users' disks:
 * a launcher that cannot read its own accounts.json silently logs everyone out.
 *
 * The reader is deliberately strict in the places upstream is strict — a profile whose skin block is
 * missing a mandatory field is discarded WHOLE rather than half-loaded, because a half-loaded profile
 * would be written back out and destroy the original. Every such rejection is marked below.
 */

using System.Text.Json.Nodes;

namespace ExtremeLauncher.Minecraft.Auth;

/// <summary>How far a piece of account data can be trusted.</summary>
public enum Validity
{
    /// <summary>Known bad or never established.</summary>
    None,

    /// <summary>Read from disk and believed current, but not checked with a server this session.</summary>
    Assumed,

    /// <summary>Verified this session.</summary>
    Certain,
}

public enum AccountType
{
    Msa,
    Offline,
}

public enum AccountState
{
    Unchecked,
    Offline,
    Working,
    Online,
    Disabled,
    Errored,
    Expired,
    Gone,
}

public sealed class Token
{
    public DateTimeOffset? IssueInstant { get; set; }

    public DateTimeOffset? NotAfter { get; set; }

    /// <summary>The token itself. Named Value rather than Token to avoid Token.Token.</summary>
    public string Value { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Service-specific extras — "userName" and "clientToken" offline, "gtg" for Xbox.</summary>
    public Dictionary<string, string> Extra { get; } = new(StringComparer.Ordinal);

    public Validity Validity { get; set; } = Validity.None;

    /// <summary>When false the token is held in memory only and never reaches disk.</summary>
    public bool Persistent { get; set; } = true;
}

public sealed class Skin
{
    public string Id { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>"classic" or "slim".</summary>
    public string Variant { get; set; } = string.Empty;

    /// <summary>The cached PNG, if it has been downloaded.</summary>
    public byte[] Data { get; set; } = [];
}

public sealed class Cape
{
    public string Id { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Alias { get; set; } = string.Empty;

    public byte[] Data { get; set; } = [];
}

public sealed class MinecraftProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Skin Skin { get; set; } = new();

    /// <summary>The equipped cape's id, or empty. Only ever set to a cape present in <see cref="Capes"/>.</summary>
    public string CurrentCape { get; set; } = string.Empty;

    public Dictionary<string, Cape> Capes { get; } = new(StringComparer.Ordinal);

    public Validity Validity { get; set; } = Validity.None;
}

public sealed class MinecraftEntitlement
{
    public bool OwnsMinecraft { get; set; }

    public bool CanPlayMinecraft { get; set; }

    public Validity Validity { get; set; } = Validity.None;
}

public sealed class AccountData
{
    public AccountType Type { get; set; } = AccountType.Msa;

    /// <summary>The OAuth client id this account was created against. Empty for older accounts.</summary>
    public string MsaClientId { get; set; } = string.Empty;

    public Token MsaToken { get; set; } = new();

    public Token UserToken { get; set; } = new();

    public Token XboxApiToken { get; set; } = new();

    public Token MojangservicesToken { get; set; } = new();

    /// <summary>The token handed to the game.</summary>
    public Token YggdrasilToken { get; set; } = new();

    public MinecraftProfile Profile { get; set; } = new();

    public MinecraftEntitlement Entitlement { get; set; } = new();

    /// <summary>The account's overall trustworthiness. Tracks the profile's.</summary>
    public Validity Validity { get; set; } = Validity.None;

    // ------------------------------------------------- runtime only, never written to disk

    /// <summary>Identifies the account within a session. Regenerated on every load.</summary>
    public string InternalId { get; set; } = string.Empty;

    public string ErrorString { get; set; } = string.Empty;

    public AccountState AccountState { get; set; } = AccountState.Unchecked;

    // ------------------------------------------------- queries

    public string AccessToken => YggdrasilToken.Value;

    public string ProfileId => Profile.Id;

    /// <summary>The profile's name, or a stand-in naming the account when there is no profile yet.</summary>
    public string ProfileName => Profile.Name.Length != 0 ? Profile.Name : $"No profile ({AccountDisplayString})";

    /// <summary>Gamertag for MSA accounts; offline accounts have no such name.</summary>
    public string AccountDisplayString => Type switch
    {
        AccountType.Offline => "<Offline>",
        AccountType.Msa => XboxApiToken.Extra.GetValueOrDefault("gtg", "Xbox profile missing"),
        _ => "Invalid Account",
    };

    public string LastError => ErrorString;

    // ================================================================== writing

    public JsonObject SaveState()
    {
        var output = new JsonObject();

        if (Type == AccountType.Msa)
        {
            output["type"] = "MSA";
            output["msa-client-id"] = MsaClientId;

            TokenToJson(output, MsaToken, "msa");
            TokenToJson(output, UserToken, "utoken");
            TokenToJson(output, XboxApiToken, "xrp-main");
            TokenToJson(output, MojangservicesToken, "xrp-mc");
        }
        else if (Type == AccountType.Offline)
        {
            output["type"] = "Offline";
        }

        TokenToJson(output, YggdrasilToken, "ygg");
        ProfileToJson(output, Profile, "profile");
        EntitlementToJson(output, Entitlement);

        return output;
    }

    /// <remarks>
    /// A token with timestamps but no token string, refresh token or extras is dropped entirely — the
    /// timestamps are filled in first but the block is only attached once something worth keeping
    /// appears. Inherited, and worth keeping: it stops empty stubs accumulating in accounts.json.
    /// </remarks>
    private static void TokenToJson(JsonObject parent, Token token, string name)
    {
        if (!token.Persistent)
        {
            return;
        }

        var output = new JsonObject();

        if (token.IssueInstant is { } issued)
        {
            output["iat"] = issued.ToUnixTimeSeconds();
        }

        if (token.NotAfter is { } expires)
        {
            output["exp"] = expires.ToUnixTimeSeconds();
        }

        var save = false;

        if (token.Value.Length != 0)
        {
            output["token"] = token.Value;
            save = true;
        }

        if (token.RefreshToken.Length != 0)
        {
            output["refresh_token"] = token.RefreshToken;
            save = true;
        }

        if (token.Extra.Count != 0)
        {
            var extra = new JsonObject();

            foreach (var (key, value) in token.Extra)
            {
                extra[key] = value;
            }

            output["extra"] = extra;
            save = true;
        }

        if (save)
        {
            parent[name] = output;
        }
    }

    private static void ProfileToJson(JsonObject parent, MinecraftProfile profile, string name)
    {
        // No id means no profile; writing an empty one back would look like a real profile on reload.
        if (profile.Id.Length == 0)
        {
            return;
        }

        var output = new JsonObject
        {
            ["id"] = profile.Id,
            ["name"] = profile.Name,
        };

        if (profile.CurrentCape.Length != 0)
        {
            output["cape"] = profile.CurrentCape;
        }

        var skin = new JsonObject
        {
            ["id"] = profile.Skin.Id,
            ["url"] = profile.Skin.Url,
            ["variant"] = profile.Skin.Variant,
        };

        if (profile.Skin.Data.Length != 0)
        {
            skin["data"] = Convert.ToBase64String(profile.Skin.Data);
        }

        output["skin"] = skin;

        var capes = new JsonArray();

        foreach (var cape in profile.Capes.Values)
        {
            var capeObject = new JsonObject
            {
                ["id"] = cape.Id,
                ["url"] = cape.Url,
                ["alias"] = cape.Alias,
            };

            if (cape.Data.Length != 0)
            {
                capeObject["data"] = Convert.ToBase64String(cape.Data);
            }

            capes.Add(capeObject);
        }

        output["capes"] = capes;
        parent[name] = output;
    }

    private static void EntitlementToJson(JsonObject parent, MinecraftEntitlement entitlement)
    {
        // Nothing is known, so writing false/false would be a claim rather than an absence.
        if (entitlement.Validity == Validity.None)
        {
            return;
        }

        parent["entitlement"] = new JsonObject
        {
            ["ownsMinecraft"] = entitlement.OwnsMinecraft,
            ["canPlayMinecraft"] = entitlement.CanPlayMinecraft,
        };
    }

    // ================================================================== reading

    /// <summary>Reads an account back out of accounts.json.</summary>
    /// <returns><see langword="false"/> when the entry is not an account this launcher understands.</returns>
    public bool ResumeStateFromV3(JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (AsString(data["type"]) is not { } typeString)
        {
            // Missing type: not an account entry at all.
            return false;
        }

        switch (typeString)
        {
            case "MSA":
                Type = AccountType.Msa;
                break;

            case "Offline":
                Type = AccountType.Offline;
                break;

            default:
                // A type from a newer or different launcher. Refused rather than guessed at, so the
                // entry survives untouched instead of being rewritten as something it is not.
                return false;
        }

        if (Type == AccountType.Msa)
        {
            // Left empty when absent: an older account predating per-account client ids.
            MsaClientId = AsString(data["msa-client-id"]) ?? string.Empty;

            MsaToken = TokenFromJson(data, "msa");
            UserToken = TokenFromJson(data, "utoken");
            XboxApiToken = TokenFromJson(data, "xrp-main");
            MojangservicesToken = TokenFromJson(data, "xrp-mc");
        }

        YggdrasilToken = TokenFromJson(data, "ygg");

        // MIGRATION: versions before 7.2 wrote "offline" as the offline placeholder token; the game
        // wants "0". Accounts created back then still exist on disk.
        if (YggdrasilToken.Value == "offline")
        {
            YggdrasilToken.Value = "0";
        }

        Profile = ProfileFromJson(data, "profile");

        if (!EntitlementFromJson(data, Entitlement) && Profile.Validity != Validity.None)
        {
            // No entitlement block, but there is a profile — which the servers only hand out to
            // accounts that own the game. Assumed rather than certain, so it is rechecked when online.
            Entitlement.CanPlayMinecraft = true;
            Entitlement.OwnsMinecraft = true;
            Entitlement.Validity = Validity.Assumed;
        }

        Validity = Profile.Validity;
        return true;
    }

    private static Token TokenFromJson(JsonObject parent, string name)
    {
        var output = new Token();

        if (parent[name] is not JsonObject tokenObject || tokenObject.Count == 0)
        {
            return output;
        }

        if (AsUnixSeconds(tokenObject["iat"]) is { } issued)
        {
            output.IssueInstant = issued;
        }

        if (AsUnixSeconds(tokenObject["exp"]) is { } expires)
        {
            output.NotAfter = expires;
        }

        if (AsString(tokenObject["token"]) is { } token)
        {
            output.Value = token;

            // Restored from disk, so believed rather than verified.
            output.Validity = Validity.Assumed;
        }

        if (AsString(tokenObject["refresh_token"]) is { } refresh)
        {
            output.RefreshToken = refresh;
        }

        if (tokenObject["extra"] is JsonObject extra)
        {
            foreach (var (key, value) in extra)
            {
                if (AsString(value) is { } text)
                {
                    output.Extra[key] = text;
                }
            }
        }

        return output;
    }

    /// <remarks>
    /// Every rejection below returns a fresh empty profile rather than a partly-filled one. A profile
    /// that loaded half its fields would be written straight back on the next save, and the half that
    /// failed to parse would be gone for good.
    /// </remarks>
    private static MinecraftProfile ProfileFromJson(JsonObject parent, string name)
    {
        var output = new MinecraftProfile();

        if (parent[name] is not JsonObject profileObject || profileObject.Count == 0)
        {
            return output;
        }

        if (AsString(profileObject["id"]) is not { } id || AsString(profileObject["name"]) is not { } profileName)
        {
            // Mandatory attributes missing or of unexpected type.
            return new MinecraftProfile();
        }

        output.Id = id;
        output.Name = profileName;

        if (profileObject["skin"] is not JsonObject skinObject)
        {
            return new MinecraftProfile();
        }

        if (AsString(skinObject["id"]) is not { } skinId
            || AsString(skinObject["url"]) is not { } skinUrl
            || AsString(skinObject["variant"]) is not { } skinVariant)
        {
            return new MinecraftProfile();
        }

        output.Skin.Id = skinId;
        output.Skin.Url = skinUrl;
        output.Skin.Variant = skinVariant;

        // The cached image is optional, but a "data" of some other type means the entry is corrupt.
        if (AsString(skinObject["data"]) is { } skinData)
        {
            output.Skin.Data = DecodeBase64(skinData);
        }
        else if (skinObject.ContainsKey("data"))
        {
            return new MinecraftProfile();
        }

        if (profileObject["capes"] is not JsonArray capesArray)
        {
            return new MinecraftProfile();
        }

        foreach (var capeValue in capesArray)
        {
            if (capeValue is not JsonObject capeObject)
            {
                return new MinecraftProfile();
            }

            if (AsString(capeObject["id"]) is not { } capeId
                || AsString(capeObject["url"]) is not { } capeUrl
                || AsString(capeObject["alias"]) is not { } capeAlias)
            {
                return new MinecraftProfile();
            }

            var cape = new Cape { Id = capeId, Url = capeUrl, Alias = capeAlias };

            if (AsString(capeObject["data"]) is { } capeData)
            {
                cape.Data = DecodeBase64(capeData);
            }
            else if (capeObject.ContainsKey("data"))
            {
                return new MinecraftProfile();
            }

            output.Capes[cape.Id] = cape;
        }

        // Only honoured when the cape is actually in the list — otherwise the profile would claim to
        // wear something it does not have.
        if (AsString(profileObject["cape"]) is { } currentCape && output.Capes.ContainsKey(currentCape))
        {
            output.CurrentCape = currentCape;
        }

        output.Validity = Validity.Assumed;
        return output;
    }

    private static bool EntitlementFromJson(JsonObject parent, MinecraftEntitlement output)
    {
        if (parent["entitlement"] is not JsonObject entitlementObject || entitlementObject.Count == 0)
        {
            return false;
        }

        if (AsBoolean(entitlementObject["ownsMinecraft"]) is not { } owns
            || AsBoolean(entitlementObject["canPlayMinecraft"]) is not { } canPlay)
        {
            return false;
        }

        output.OwnsMinecraft = owns;
        output.CanPlayMinecraft = canPlay;
        output.Validity = Validity.Assumed;

        return true;
    }

    // ================================================================== strict JSON accessors

    /*
     * Qt's isString()/isBool()/isDouble() reject rather than coerce, and the reader above depends on
     * that: a "capes" that is an object, or an "ownsMinecraft" that is the string "true", must fail the
     * check instead of being converted. These return null on any type mismatch.
     */

    private static string? AsString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? AsBoolean(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static DateTimeOffset? AsUnixSeconds(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<double>(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            : null;

    /// <remarks>Upstream leaves this unvalidated; malformed base64 there yields an empty array.</remarks>
    private static byte[] DecodeBase64(string text)
    {
        Span<byte> buffer = new byte[text.Length * 3 / 4 + 3];

        return Convert.TryFromBase64String(text, buffer, out var written)
            ? buffer[..written].ToArray()
            : [];
    }
}
