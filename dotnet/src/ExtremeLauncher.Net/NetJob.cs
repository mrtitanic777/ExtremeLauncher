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
 * Rewritten from launcher/net/NetJob.{h,cpp}.
 *
 * A batch of requests run concurrently, with failures retried. Upstream implements the retry by
 * overriding executeNextSubTask() and, when the queue and in-flight set are both empty but failures
 * remain, moving everything from m_failed back into m_queue -- up to three attempts total. That maps
 * onto the TryRefillQueue hook added to ConcurrentTask for exactly this purpose.
 */

using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Net;

public sealed class NetJob : ConcurrentTask
{
    /// <summary>Total attempts, including the first. Upstream's <c>m_try &lt; 3</c>.</summary>
    private const int MaxAttempts = 3;

    private readonly HttpClient _client;

    private int _attempt = 1;

    public NetJob(string jobName, HttpClient client, int maxConcurrent = 6)
        : base(jobName, maxConcurrent > 0 ? maxConcurrent : 6)
        => _client = client;

    /// <summary>How many attempts have been made, counting the first.</summary>
    public int Attempts => _attempt;

    public int Size => TotalSize;

    public bool AddNetAction(NetRequest action)
    {
        ArgumentNullException.ThrowIfNull(action);
        AddTask(action);
        return true;
    }

    /// <summary>Convenience for the common "fetch these URLs to these paths" case.</summary>
    public NetJob AddDownload(Uri url, string path)
    {
        AddNetAction(Download.MakeFile(_client, url, path));
        return this;
    }

    public IReadOnlyList<NetRequest> FailedActions => [.. FailedTasks.OfType<NetRequest>()];

    public IReadOnlyList<string> FailedFiles => [.. FailedTasks.OfType<NetRequest>().Select(r => r.Url.ToString())];

    /// <summary>Retries failed requests, up to <see cref="MaxAttempts"/> passes in total.</summary>
    protected override bool TryRefillQueue()
    {
        if (_attempt >= MaxAttempts || FailedTasks.Count == 0)
        {
            return false;
        }

        _attempt++;
        return RequeueFailed() > 0;
    }

    protected override string GetFailureMessage()
    {
        var failed = FailedFiles;

        return failed.Count switch
        {
            0 => "One or more subtasks failed",
            1 => $"Failed to download {failed[0]}",
            _ => $"Failed to download {failed.Count} files, including {failed[0]}",
        };
    }
}
