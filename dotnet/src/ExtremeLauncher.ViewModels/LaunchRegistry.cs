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
 * WHICH INSTANCES ARE RUNNING. One LaunchCoordinator per instance, made on demand and kept.
 *
 * This exists because "one at a time" was scoped wrongly. LaunchCoordinator refuses a second launch
 * while one is running, and its own comment gives the reason: "both would write to the same instance
 * directory, and the second would be extracting natives into a folder the first is reading."
 *
 * That reasoning is about ONE INSTANCE. With a single coordinator on the main window it applied to the
 * whole launcher, so starting a second, unrelated instance was refused for a conflict that could not
 * happen. Upstream has no such limit -- BaseInstance::isRunning() is per instance, and running two
 * packs at once is an ordinary thing to do.
 *
 * KEPT AFTER THE GAME EXITS, deliberately. The coordinator holds the log of the run that just
 * finished, and the log page is most wanted in the ten seconds after a crash. They are small: a
 * status string and a bounded list.
 */

using System.Collections.Concurrent;

namespace ExtremeLauncher.ViewModels;

public sealed class LaunchRegistry
{
    private readonly ConcurrentDictionary<string, LaunchCoordinator> _coordinators =
        new(StringComparer.Ordinal);

    private readonly IInstanceLauncher _launcher;

    public LaunchRegistry(IInstanceLauncher? launcher = null)
        => _launcher = launcher ?? new UnavailableLauncher();

    /// <summary>The coordinator for an instance, made if this is the first time it is asked for.</summary>
    public LaunchCoordinator For(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        return _coordinators.GetOrAdd(instanceId, _ => new LaunchCoordinator(_launcher));
    }

    /// <summary>Whether a particular instance is running.</summary>
    /// <remarks>
    /// Does NOT create a coordinator. An instance nobody has launched has no state worth keeping, and
    /// asking about one in a loop over the whole list must not fill the dictionary with empties.
    /// </remarks>
    public bool IsRunning(string instanceId)
        => _coordinators.TryGetValue(instanceId, out var coordinator) && coordinator.IsBusy;

    /// <summary>Every instance currently running.</summary>
    public IReadOnlyList<string> Running
        => [.. _coordinators.Where(pair => pair.Value.IsBusy).Select(pair => pair.Key)];

    /// <summary>Whether anything at all is running, for a "quit anyway?" question at shutdown.</summary>
    public bool AnythingRunning => _coordinators.Values.Any(c => c.IsBusy);

    /// <summary>Forgets an instance's coordinator, for one that has been deleted.</summary>
    /// <remarks>
    /// A running instance is NOT forgotten: dropping the coordinator would leave the game running with
    /// nothing holding its log or its cancellation.
    /// </remarks>
    public bool Forget(string instanceId)
    {
        if (!_coordinators.TryGetValue(instanceId, out var coordinator) || coordinator.IsBusy)
        {
            return false;
        }

        return _coordinators.TryRemove(instanceId, out _);
    }
}
