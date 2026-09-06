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
 * GETTING AN ACCOUNT READY TO PLAY, which is the step between "an account is selected" and "the game
 * can be told who is playing".
 *
 * The access token in accounts.json is usually stale -- it lasts about a day -- so launching with it
 * unexamined means the player is bounced by the first online-mode server they try. This runs the
 * silent chain when it needs to and gets out of the way when it does not.
 *
 * WHAT IT DELIBERATELY WILL NOT DO is prompt. A sign-in needs a human to type a code into a browser,
 * and starting that from inside a launch would put a dialog in front of somebody who pressed Play. It
 * reports NeedsSignIn instead and lets the caller decide -- which, for the launcher, means offering
 * the sign-in dialog and letting the user choose whether to bother.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Auth;

namespace ExtremeLauncher.Launch;

/// <summary>How an account came out of being made ready.</summary>
public enum AccountReadiness
{
    /// <summary>Good to play. The session will carry a real token.</summary>
    Ready,

    /// <summary>The account needs a human to sign in again before it can be used.</summary>
    NeedsSignIn,

    /// <summary>Could not tell, because the network was not reachable.</summary>
    /// <remarks>
    /// Distinct from <see cref="NeedsSignIn"/> on purpose: a stored token may well still be good, and
    /// a player on a train should be offered their single-player world, not a sign-in prompt.
    /// </remarks>
    Offline,
}

public sealed record AccountRefreshResult(AccountReadiness Readiness, string Message = "")
{
    public bool CanPlayOnline => Readiness == AccountReadiness.Ready;
}

public static class AccountRefresh
{
    /// <summary>
    /// How much life a token needs left before it is used as-is.
    /// </summary>
    /// <remarks>
    /// A token that expires in thirty seconds is technically valid and useless: the game may still be
    /// loading when it dies. Refreshing early costs one request and avoids a failure that would look
    /// like a server problem.
    /// </remarks>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Makes an account ready to play, refreshing it silently if that is enough.
    /// </summary>
    /// <param name="clientId">The MSA client id. Empty means Microsoft sign-in is not configured.</param>
    /// <param name="now">Injected so the expiry rule can be tested without waiting a day.</param>
    public static async Task<AccountRefreshResult> PrepareAsync(
        MinecraftAccount account,
        HttpClient client,
        string clientId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        // Nothing to refresh, and nothing that could ever expire.
        if (account.AccountType == AccountType.Offline)
        {
            return new AccountRefreshResult(AccountReadiness.Ready);
        }

        if (clientId.Length == 0)
        {
            return new AccountRefreshResult(
                AccountReadiness.NeedsSignIn,
                "Microsoft sign-in is not configured in this build.");
        }

        if (!NeedsRefresh(account.Data, now ?? DateTimeOffset.UtcNow))
        {
            return new AccountRefreshResult(AccountReadiness.Ready);
        }

        var flow = AuthFlow.CreateMsaFlow(
            account.Data,
            client,
            new MSARefreshStep(account.Data, client, clientId),
            "Refreshing sign-in");

        try
        {
            await flow.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LauncherException)
        {
            // The state below says what actually happened; the exception only says it stopped.
        }

        return flow.TaskState switch
        {
            AccountTaskState.Succeeded => new AccountRefreshResult(AccountReadiness.Ready),

            // Offline is its own answer: the stored token may be fine, we simply could not check.
            AccountTaskState.Offline => new AccountRefreshResult(
                AccountReadiness.Offline,
                "Could not reach Microsoft to check your sign-in."),

            /*
             * Everything else -- Disabled, FailedHard, FailedSoft, FailedGone -- needs a human. Soft
             * failures are lumped in here rather than retried silently because the caller is about to
             * launch a game: one clear "sign in again" beats a retry loop in front of somebody waiting.
             */
            _ => new AccountRefreshResult(
                AccountReadiness.NeedsSignIn,
                flow.FailReason.Length != 0 ? flow.FailReason : "Your sign-in needs renewing."),
        };
    }

    /// <summary>
    /// Whether the stored token is too old, or too nearly old, to launch with.
    /// </summary>
    /// <remarks>
    /// A token with NO recorded expiry is refreshed rather than trusted. Upstream's accounts predating
    /// the field exist, and treating "unknown" as "fine" is how a launcher ends up handing the game a
    /// token that died months ago.
    /// </remarks>
    public static bool NeedsRefresh(AccountData data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.YggdrasilToken.Validity != Validity.Certain || data.YggdrasilToken.Value.Length == 0)
        {
            return true;
        }

        if (data.MsaToken.NotAfter is not { } expiry)
        {
            return true;
        }

        return expiry - now <= RefreshMargin;
    }
}
