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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Authors: Orochimarufan <orochimarufan.x3@gmail.com>
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
 * Ported from launcher/minecraft/auth/MinecraftAccount.{h,cpp} and launcher/Usable.h.
 *
 * One account: how it is created, whether its token needs refreshing, and how it fills in a session.
 *
 * NOT PORTED YET: login(), refresh() and the AuthFlow they drive — the MSA device-code and refresh
 * chains. Those are network flows and land with the rest of minecraft/auth/steps/. What is here is the
 * whole offline path plus everything that decides whether a stored account can be used, which is what
 * the launch pipeline needs.
 *
 * getFace() is deliberately absent: it is QPixmap compositing for the account list, and belongs with
 * the UI wave rather than here.
 */

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ExtremeLauncher.Minecraft.Auth;

public sealed class MinecraftAccount
{
    /// <summary>A fresh token is valid for 24 hours.</summary>
    private static readonly TimeSpan FreshTokenValidity = TimeSpan.FromHours(24);

    /// <summary>Refresh once less than this remains, so a token never expires mid-session.</summary>
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromHours(12);

    private int _uses;

    public MinecraftAccount() => Data.InternalId = StrippedUuid(Guid.NewGuid());

    /// <summary>Raised when anything a list view would show has changed.</summary>
    public event EventHandler? Changed;

    public AccountData Data { get; } = new();

    // ================================================================== construction

    public static MinecraftAccount? LoadFromJsonV3(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var account = new MinecraftAccount();
        return account.Data.ResumeStateFromV3(json) ? account : null;
    }

    /// <summary>An MSA account with nothing filled in yet, ready to be logged into.</summary>
    public static MinecraftAccount CreateBlankMsa() => new() { Data = { Type = AccountType.Msa } };

    /// <summary>Builds an account that plays offline, with no server-side identity behind it.</summary>
    public static MinecraftAccount CreateOffline(string username)
    {
        var account = new MinecraftAccount();

        account.Data.Type = AccountType.Offline;

        // "0" is the placeholder token the game accepts when there is nothing to authenticate against.
        account.Data.YggdrasilToken.Value = "0";
        account.Data.YggdrasilToken.Validity = Validity.Certain;
        account.Data.YggdrasilToken.IssueInstant = DateTimeOffset.UtcNow;
        account.Data.YggdrasilToken.Extra["userName"] = username;
        account.Data.YggdrasilToken.Extra["clientToken"] = StrippedUuid(Guid.NewGuid());

        account.Data.Profile.Id = StrippedUuid(UuidFromUsername(username));
        account.Data.Profile.Name = username;
        account.Data.Profile.Validity = Validity.Certain;

        return account;
    }

    public JsonObject SaveToJson() => Data.SaveState();

    // ================================================================== queries

    public string InternalId => Data.InternalId;

    public string AccountDisplayString => Data.AccountDisplayString;

    public string AccessToken => Data.AccessToken;

    public string ProfileId => Data.ProfileId;

    public string ProfileName => Data.ProfileName;

    public AccountType AccountType => Data.Type;

    public AccountState AccountState => Data.AccountState;

    public string LastError => Data.LastError;

    /// <summary>Offline accounts never own the game, whatever the stored entitlement says.</summary>
    public bool OwnsMinecraft => Data.Type != AccountType.Offline && Data.Entitlement.OwnsMinecraft;

    public bool HasProfile => Data.ProfileId.Length != 0;

    public string TypeString => Data.Type switch
    {
        AccountType.Msa => "msa",
        AccountType.Offline => "offline",
        _ => "unknown",
    };

    // ================================================================== in-use tracking

    /*
     * Ported from Usable.h. The count exists so the launcher knows an account is backing a running
     * game — refreshing its token then would invalidate the session the game is holding.
     */

    /// <summary>How many running games are using this account.</summary>
    public int Uses => _uses;

    public bool IsInUse => _uses > 0;

    public void IncrementUses()
    {
        var wasInUse = IsInUse;
        _uses++;

        if (!wasInUse)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void DecrementUses()
    {
        if (_uses == 0)
        {
            throw new InvalidOperationException("Tried to release an account that was not in use.");
        }

        _uses--;

        if (!IsInUse)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ================================================================== refreshing

    /// <summary>Whether the stored token should be refreshed before the account is used.</summary>
    /// <remarks>
    /// Never refresh accounts that are being used by the game — it breaks the game session.
    /// Always refresh accounts that have not been refreshed yet during this session.
    /// Don't refresh broken accounts.
    /// Refresh accounts that would expire in the next 12 hours (fresh token validity is 24 hours).
    /// </remarks>
    public bool ShouldRefresh()
    {
        if (IsInUse)
        {
            return false;
        }

        switch (Data.Validity)
        {
            case Validity.Certain:
                break;

            case Validity.None:
                return false;

            case Validity.Assumed:
                return true;
        }

        var expires = Data.YggdrasilToken.NotAfter ?? Data.YggdrasilToken.IssueInstant?.Add(FreshTokenValidity);

        // Neither timestamp is set, so there is nothing to say the token has expired.
        if (expires is null)
        {
            return false;
        }

        return expires.Value - DateTimeOffset.UtcNow < RefreshThreshold;
    }

    // ================================================================== sessions

    /// <summary>Fills in the details the game is launched with.</summary>
    public void FillSession(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (OwnsMinecraft && !HasProfile)
        {
            // Owns the game but has never picked a name; the game cannot start until they do.
            session.Status = SessionStatus.RequiresProfileSetup;
        }
        else
        {
            session.Status = session.WantsOnline ? SessionStatus.PlayableOnline : SessionStatus.PlayableOffline;
        }

        session.AccessToken = Data.AccessToken;
        session.PlayerName = Data.ProfileName;
        session.Uuid = Data.ProfileId;

        // No stored profile id, so derive the same one a vanilla server would.
        if (session.Uuid.Length == 0)
        {
            session.Uuid = StrippedUuid(UuidFromUsername(session.PlayerName));
        }

        session.UserType = TypeString;

        // NOTE: the profile id, not session.Uuid — so a session that fell back to a derived uuid above
        // still gets an empty tail here. Inherited; the game ignores this half of the string.
        session.Session = Data.AccessToken.Length != 0
            ? $"token:{Data.AccessToken}:{Data.ProfileId}"
            : "-";
    }

    /// <summary>Convenience wrapper around <see cref="FillSession"/> for a fresh session.</summary>
    public AuthSession CreateSession(bool wantsOnline = true)
    {
        var session = new AuthSession { WantsOnline = wantsOnline };
        FillSession(session);

        return session;
    }

    // ================================================================== offline identity

    /// <summary>
    /// Derives an offline player's UUID the way the vanilla server does.
    /// </summary>
    /// <remarks>
    /// A version-3 UUID over MD5("OfflinePlayer:" + name) — a reimplementation of Java's
    /// UUID.nameUUIDFromBytes.
    ///
    /// ENDIANNESS TRAP: Java and QUuid::fromRfc4122 both read the digest big-endian throughout, but
    /// .NET's Guid(byte[]) constructor reads the first three fields LITTLE-endian. Handing the digest
    /// straight to it produces a byte-swapped UUID that looks entirely plausible and is wrong — the
    /// player would be a stranger in their own single-player world, with someone else's inventory and
    /// position. The three leading fields are reversed here to compensate.
    /// </remarks>
    public static Guid UuidFromUsername(string username)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{username}"));

        digest[6] &= 0x0f; // clear version
        digest[6] |= 0x30; // set to version 3
        digest[8] &= 0x3f; // clear variant
        digest[8] |= 0x80; // set to IETF variant

        Array.Reverse(digest, 0, 4);
        Array.Reverse(digest, 4, 2);
        Array.Reverse(digest, 6, 2);

        return new Guid(digest);
    }

    /// <summary>A UUID with no braces or dashes, which is how ids are stored and passed to the game.</summary>
    public static string StrippedUuid(Guid uuid) => uuid.ToString("N");

    public override string ToString() => $"{ProfileName} ({TypeString})";
}
