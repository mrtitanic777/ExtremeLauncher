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
 * THE WINDOW'S END OF THE LAUNCH. LauncherService does the work and the CLI calls exactly the same
 * method; this only bridges it to a UI toolkit.
 *
 * Nearly all of that bridge is BatchingProgressReporter, which lives in the view models where it can be
 * tested without starting a window. What is left here is the one thing that genuinely needs Avalonia:
 * the launch runs OFF the UI thread, because it waits on a child process for as long as someone plays
 * Minecraft, and on the UI thread that is a frozen window for the length of the session.
 */

using Avalonia.Threading;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

/// <summary>Runs <see cref="LauncherService"/> for the window.</summary>
public sealed class AppLauncher : IInstanceLauncher
{
    private readonly LauncherService _service;

    private readonly string _metaUrl;

    private readonly string _server;

    private readonly string _world;

    private readonly LauncherLog? _log;

    private readonly Func<MinecraftAccount?>? _account;

    private readonly HttpClient? _client;

    private readonly string _clientId;

    /// <param name="server">A server to join, from the command line. Empty for none.</param>
    /// <param name="world">A world to open, from the command line. Empty for none.</param>
    /// <param name="log">The launcher's own log, so a launch is recorded as well as shown.</param>
    /// <param name="account">
    /// The account to play as, read at launch time rather than captured, so choosing a different
    /// default in the accounts window takes effect on the next launch without rebuilding anything.
    /// </param>
    public AppLauncher(
        LauncherService service,
        string metaUrl,
        string server = "",
        string world = "",
        LauncherLog? log = null,
        Func<MinecraftAccount?>? account = null,
        HttpClient? client = null,
        string clientId = "")
    {
        ArgumentNullException.ThrowIfNull(service);

        _service = service;
        _metaUrl = metaUrl;
        _server = server;
        _world = world;
        _log = log;
        _account = account;
        _client = client;
        _clientId = clientId;
    }

    public async Task LaunchAsync(
        string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
    {
        ArgumentNullException.ThrowIfNull(progress);

        /*
         * THE ACCOUNT, REFRESHED FIRST. The stored access token is usually stale -- it lasts about a
         * day -- so launching with it unexamined means being bounced by the first online-mode server.
         *
         * A refresh that cannot be done does NOT stop the launch. Offline, or a sign-in that has
         * genuinely lapsed, still leaves a perfectly good single-player game to play; the reason is
         * reported and the launch carries on with whatever the account holds. Refusing to start here
         * would make a lapsed sign-in look like a broken launcher.
         */
        var account = _account?.Invoke();

        if (account is not null && _client is not null)
        {
            var readiness = await AccountRefresh
                .PrepareAsync(account, _client, _clientId, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            if (!readiness.CanPlayOnline)
            {
                progress.Log($"Playing as {account.ProfileName}: {readiness.Message}", isError: true);
                _log?.Warning($"Account not refreshed: {readiness.Message}");
            }
        }

        var request = new LaunchRequest
        {
            InstanceId = instanceId,
            MetaUrl = _metaUrl,

            // A per-launch server (the servers page's Join) wins over the command-line one; the
            // command-line server is the fallback for a plain launch.
            Server = string.IsNullOrEmpty(server) ? _server : server,
            World = _world,
            Account = account,

            // Only reached with no account selected at all, which is the first-run state.
            PlayerName = "Player",
        };

        /*
         * Background priority: the launch's chatter must never outrank input or rendering. A user
         * pressing Cancel while Forge floods the log is the case this protects.
         */
        using var reporter = new BatchingProgressReporter(
            progress,
            drain => Dispatcher.UIThread.Post(drain, DispatcherPriority.Background));

        /*
         * Written to the log as well as shown. The window's copy dies with the process; the file is
         * what somebody can send afterwards, and a launch that failed is exactly when they need to.
         */
        ILaunchReporter sink = _log is null ? reporter : new LoggingLaunchReporter(reporter, _log);

        try
        {
            /*
             * Task.Run, not a bare await. LauncherService is async but not async all the way down --
             * the process wait and several file operations block -- and the one thread that must never
             * block is the one drawing the window.
             */
            await Task.Run(
                () => _service.LaunchAsync(request, sink, cancellationToken),
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            /*
             * Whatever is still buffered, delivered before the caller decides the launch is over --
             * including on the failure path, where the last few lines are the ones that say why. This
             * runs on the UI thread: the await above resumes there, and so does an exception.
             */
            reporter.Flush();
        }
    }
}
