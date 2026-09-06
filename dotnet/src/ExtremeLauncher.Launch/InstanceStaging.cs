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
 * Ported from the InstanceStaging class and the staging half of launcher/InstanceList.cpp, plus
 * launcher/ExponentialSeries.h.
 *
 * WHERE EVERY IMPORTER ENDS. Creating an instance -- from a pack, a copy, or nothing -- builds it in a
 * staging directory first and only then moves it into place. Nothing half-built is ever visible in the
 * instance list, and a failure anywhere leaves nothing behind to clean up by hand.
 *
 * THE RETRY LOOP IS NOT DEFENSIVE PROGRAMMING. Upstream's comment names the cause exactly: "the whole
 * reason why this uses an exponential backoff retry scheme is antivirus on Windows. Basically, it
 * starts messing things up while the launcher is extracting/creating instances and causes that
 * horrible failure that is NTFS to lock files in place because they are open."
 *
 * A scanner opens the files an importer just wrote, the move fails because they are open, and waiting
 * is the only remedy -- there is nothing to fix and nothing to ask the user. Backing off exponentially
 * is how the launcher outlasts a scan without spinning.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>A sequence that multiplies until it reaches a ceiling, then stays there.</summary>
/// <remarks>
/// Ported from ExponentialSeries. Note it returns the value BEFORE growing, so the first call yields
/// the minimum -- the first retry is immediate-ish rather than already delayed.
/// </remarks>
public sealed class ExponentialSeries
{
    private readonly uint _min;
    private readonly uint _max;
    private readonly uint _exponent;

    private uint _current;

    public ExponentialSeries(uint min, uint max, uint exponent = 2)
    {
        _min = min;
        _max = max;
        _exponent = exponent;
        _current = min;
    }

    /// <summary>The value the series has reached, without advancing it.</summary>
    public uint Current => _current;

    public void Reset() => _current = _min;

    /// <summary>Returns the current value and advances the series.</summary>
    public uint Next()
    {
        var value = _current;

        _current = Math.Clamp(_current * _exponent, _min, _max);

        return value;
    }
}

/// <summary>What a task must tell the staging wrapper about the instance it is building.</summary>
public interface IInstanceTask
{
    /// <summary>Where to build. Set by the wrapper before the task runs.</summary>
    string StagingPath { get; set; }

    /// <summary>The instance's name, which becomes its directory name.</summary>
    string Name { get; }

    /// <summary>The group to file it under, or empty.</summary>
    string Group { get; }

    /// <summary>Whether this replaces an existing instance rather than creating one.</summary>
    bool ShouldOverride { get; }

    /// <summary>Which instance to replace, when overriding.</summary>
    string OriginalInstanceId { get; }
}

/// <summary>Runs an instance-creating task in a staging directory and commits the result.</summary>
public sealed class InstanceStagingTask : LauncherTask
{
    /// <summary>How long to wait between commit attempts, in half-seconds.</summary>
    /// <remarks>Upstream's numbers: 1, 2, 4, 8, 16 half-seconds, then 16 forever.</remarks>
    private const uint MinBackoff = 1;

    private const uint MaxBackoff = 16;

    private readonly InstanceList _instances;
    private readonly LauncherTask _child;
    private readonly IInstanceTask _description;
    private readonly Func<uint, CancellationToken, Task> _delay;

    public InstanceStagingTask(
        InstanceList instances,
        LauncherTask child,
        IInstanceTask description,
        Func<uint, CancellationToken, Task>? delay = null)
        : base($"Creating instance {description.Name}")
    {
        _instances = instances;
        _child = child;
        _description = description;

        // Injected so the retry loop can be tested without waiting eight seconds for it.
        _delay = delay ?? ((halfSeconds, token) =>
            Task.Delay(TimeSpan.FromMilliseconds(halfSeconds * 500), token));
    }

    public override bool CanAbort => _child.CanAbort;

    /// <summary>Where the instance was built. Gone by the time the task succeeds.</summary>
    public string StagingPath { get; private set; } = string.Empty;

    /// <summary>How many commit attempts it took. One means it worked first time.</summary>
    public int CommitAttempts { get; private set; }

    /// <summary>The id the instance was committed under. Empty until it succeeds.</summary>
    /// <remarks>
    /// Not the same as the name when something already occupied the directory. A caller that wants to
    /// select what it just made needs this rather than a guess from the name.
    /// </remarks>
    public string CommittedId { get; private set; } = string.Empty;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        StagingPath = _instances.CreateStagingPath();
        _description.StagingPath = StagingPath;

        try
        {
            _child.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);
            _child.StatusChanged += (_, status) => SetStatus(status);

            if (!await _child.RunAsync(cancellationToken).ConfigureAwait(false))
            {
                /*
                 * A failed build leaves nothing behind. Upstream destroys the staging path on both
                 * failure and abort, and it matters: a half-extracted pack in the instances folder
                 * would otherwise be picked up as an instance on the next scan.
                 */
                _instances.DestroyStagingPath(StagingPath);

                /*
                 * A CANCELLATION IS NOT A FAILURE, and the child reports both by returning false.
                 * Without this the person who pressed Cancel is shown the child's failure message in
                 * an error dialog, which tells them something went wrong when nothing did.
                 */
                cancellationToken.ThrowIfCancellationRequested();

                throw new TaskFailedException(_child.FailReason);
            }
        }
        catch (OperationCanceledException)
        {
            _instances.DestroyStagingPath(StagingPath);

            throw;
        }

        await CommitWithBackoffAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the staged instance into place, retrying while something holds the files open.
    /// </summary>
    /// <remarks>
    /// The staging path is NOT destroyed when every attempt fails. Upstream leaves it too, and that is
    /// the right call: the instance is fully built and only the move failed, so throwing it away would
    /// discard a completed download because a virus scanner was slow.
    /// </remarks>
    private async Task CommitWithBackoffAsync(CancellationToken cancellationToken)
    {
        var backoff = new ExponentialSeries(MinBackoff, MaxBackoff);

        while (true)
        {
            CommitAttempts++;

            SetStatus(CommitAttempts == 1
                ? "Committing instance"
                : $"Committing instance (attempt {CommitAttempts})");

            if (_instances.CommitStagedInstance(StagingPath, _description, out var committedId))
            {
                CommittedId = committedId;

                return;
            }

            var wait = backoff.Next();

            // The ceiling is the giving-up point, not a plateau: upstream stops once it is reached.
            if (wait >= MaxBackoff)
            {
                throw new TaskFailedException(
                    "Failed to commit instance, even after multiple retries. It is being blocked by something.");
            }

            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
