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
 * Ported from launcher/minecraft/auth/AuthSession.{h,cpp}.
 *
 * What the game is told about the player. This is the handoff between the auth subsystem and the
 * launch pipeline: the account fills one of these in, and the argument builder reads it.
 */

namespace ExtremeLauncher.Minecraft.Auth;

public enum SessionStatus
{
    Undetermined,
    RequiresOAuth,
    RequiresPassword,
    RequiresProfileSetup,
    PlayableOffline,
    PlayableOnline,
    GoneOrMigrated,
}

public sealed class AuthSession
{
    public SessionStatus Status { get; set; } = SessionStatus.Undetermined;

    /// <summary>The combined session id, in the form <c>token:&lt;access token&gt;:&lt;profile id&gt;</c>.</summary>
    public string Session { get; set; } = string.Empty;

    /// <summary>The volatile auth token.</summary>
    public string AccessToken { get; set; } = string.Empty;

    public string PlayerName { get; set; } = string.Empty;

    /// <summary>The profile id.</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>"msa" or "offline", depending on the account type.</summary>
    /// <remarks>
    /// Upstream's comment says "legacy or mojang"; it is stale. The value actually comes from
    /// MinecraftAccount's type string, which has only ever produced "msa" and "offline" since the
    /// Mojang account types were removed.
    /// </remarks>
    public string UserType { get; set; } = string.Empty;

    /// <summary>Whether the auth server replied.</summary>
    public bool AuthServerOnline { get; set; }

    /// <summary>Whether the user asked to play online.</summary>
    public bool WantsOnline { get; set; } = true;

    public bool Demo { get; set; }

    /// <summary>
    /// The <c>${user_properties}</c> token's value.
    /// </summary>
    /// <remarks>
    /// Always an empty object. The body that filled it in from the account's properties was commented
    /// out upstream when Mojang accounts went away; the token itself still has to be substituted,
    /// because old versions of the game pass it to a JSON parser that rejects an empty string.
    /// </remarks>
    public static string SerializeUserProperties() => "{}";

    /// <summary>Downgrades a playable session to offline play under a different name.</summary>
    /// <returns><see langword="false"/> if the session was not playable to begin with.</returns>
    public bool MakeOffline(string offlinePlayerName)
    {
        if (Status is not (SessionStatus.PlayableOffline or SessionStatus.PlayableOnline))
        {
            return false;
        }

        Session = "-";
        AccessToken = "0";
        PlayerName = offlinePlayerName;
        Status = SessionStatus.PlayableOffline;

        return true;
    }

    /// <summary>Turns the session into a demo one.</summary>
    /// <remarks>
    /// Ends up PlayableOnline despite WantsOnline being false — inherited and deliberate: the demo
    /// still needs the network to download its assets.
    /// </remarks>
    public void MakeDemo(string name, string uuid)
    {
        WantsOnline = false;
        Demo = true;
        Uuid = uuid;
        Session = "-";
        AccessToken = "0";
        PlayerName = name;
        Status = SessionStatus.PlayableOnline;
    }
}
