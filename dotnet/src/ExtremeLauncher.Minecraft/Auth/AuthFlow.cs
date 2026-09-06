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
 * Ported from launcher/minecraft/auth/AuthFlow.{h,cpp}.
 *
 * Runs the authentication steps in order and stops at the first one that does not report Working. The
 * resting state of the account is a side effect of that stop: which failure it was decides whether the
 * account ends up Offline, Errored, Expired, Disabled or Gone, and the account list shows the
 * difference.
 *
 * THE ORDER IS NOT ARBITRARY. Each step reads what the ones before it wrote — the Xbox token is built
 * from the Microsoft one, the XSTS tokens from the Xbox one, the Minecraft token from the Mojang-scoped
 * XSTS token, and everything after that from the Minecraft token.
 */

using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Minecraft.Auth;

public sealed class AuthFlow : LauncherTask
{
    public enum Action
    {
        /// <summary>Silent: use the stored refresh token, and fail rather than prompting.</summary>
        Refresh,

        /// <summary>Interactive: send the user to a browser.</summary>
        Login,

        /// <summary>Interactive, but with a code the user types in themselves.</summary>
        DeviceCode,
    }

    private readonly AccountData _data;
    private readonly List<AuthStep> _steps = [];

    public AuthFlow(AccountData data, IEnumerable<AuthStep> steps, string name = "Authenticate") : base(name)
    {
        _data = data;
        _steps.AddRange(steps);

        ChangeState(AccountTaskState.Created);
    }

    /// <summary>The state the flow stopped in.</summary>
    public AccountTaskState TaskState { get; private set; } = AccountTaskState.Created;

    public IReadOnlyList<AuthStep> Steps => _steps;

    public override bool CanAbort => true;

    /// <summary>
    /// Builds the standard MSA chain.
    /// </summary>
    /// <remarks>
    /// An OFFLINE ACCOUNT GETS NO STEPS AT ALL, and upstream's behaviour follows from that: an empty
    /// list runs to the end without incident, so the flow succeeds, marks the account Certain and
    /// leaves its state Online. Surprising to read and correct in effect — there is nothing to check.
    /// </remarks>
    public static AuthFlow CreateMsaFlow(
        AccountData data,
        HttpClient client,
        AuthStep oauthStep,
        string name = "Authenticate")
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Type != AccountType.Msa)
        {
            return new AuthFlow(data, [], name);
        }

        return new AuthFlow(
            data,
            [
                oauthStep,
                new XboxUserStep(data, client),

                // Two audiences, two tokens: Xbox's own services, then Mojang's.
                new XboxAuthorizationStep(data, client, d => d.XboxApiToken, "http://xboxlive.com", "Xbox"),
                new XboxAuthorizationStep(data, client, d => d.MojangservicesToken, "rp://api.minecraftservices.com/", "Mojang"),

                new LauncherLoginStep(data, client),
                new XboxProfileStep(data, client),
                new EntitlementsStep(data, client),
                new MinecraftProfileStep(data, client),
                new GetSkinStep(data, client),
            ],
            name);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ChangeState(AccountTaskState.Working, "Initializing");

        for (var i = 0; i < _steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = _steps[i];

            SetStatus(step.Describe);
            SetProgress(i, _steps.Count);

            var result = await step.PerformAsync(cancellationToken).ConfigureAwait(false);

            // Anything but Working is terminal, whether it is a failure or not.
            if (!ChangeState(result.State, result.Message))
            {
                throw new TaskFailedException(result.Message);
            }
        }

        // Got to the end without incident. Assume this is all.
        SetProgress(_steps.Count, _steps.Count);
        Succeed();
    }

    private void Succeed()
    {
        _data.Validity = Validity.Certain;
        ChangeState(AccountTaskState.Succeeded, "Finished all authentication steps");
    }

    /// <returns><see langword="true"/> when the flow should continue.</returns>
    private bool ChangeState(AccountTaskState newState, string reason = "")
    {
        TaskState = newState;
        SetDetails(reason);

        switch (newState)
        {
            case AccountTaskState.Created:
                SetStatus("Waiting...");
                _data.ErrorString = string.Empty;
                return true;

            case AccountTaskState.Working:
                _data.AccountState = AccountState.Working;
                return true;

            case AccountTaskState.Succeeded:
                SetStatus("Authentication task succeeded.");
                _data.AccountState = AccountState.Online;
                return false;

            case AccountTaskState.Offline:
                return Fail("Failed to contact the authentication server.", AccountState.Offline, reason);

            case AccountTaskState.Disabled:
                return Fail("Client ID has changed. New session needs to be created.", AccountState.Disabled, reason);

            case AccountTaskState.FailedSoft:
                return Fail("Encountered an error during authentication.", AccountState.Errored, reason);

            case AccountTaskState.FailedHard:
                return Fail("Failed to authenticate. The session has expired.", AccountState.Expired, reason);

            case AccountTaskState.FailedGone:
                return Fail("Failed to authenticate. The account no longer exists.", AccountState.Gone, reason);

            default:
                return Fail("...", AccountState.Errored, $"Unknown account task state: {newState}");
        }
    }

    private bool Fail(string status, AccountState accountState, string reason)
    {
        SetStatus(status);

        _data.ErrorString = reason;
        _data.AccountState = accountState;

        return false;
    }
}
