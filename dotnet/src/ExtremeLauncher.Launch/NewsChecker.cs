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
 * Ported from launcher/news/NewsChecker.cpp -- the fetching half; the parsing half is
 * Core/NewsFeed.cs.
 *
 * FETCHES THE FEED ONCE AT STARTUP and holds the result. Upstream's shape is kept: a single in-flight
 * request (a second reload while one is running is IGNORED, not queued), a "loading" state the
 * toolbar shows while it runs, and a last-error string rather than an exception -- because news
 * failing must never be something the user has to dismiss.
 *
 * NEWS IS THE LEAST IMPORTANT THING THE LAUNCHER DOES, and it is the first thing that touches the
 * network on startup. So every failure here is soft: no throw reaches the caller, the toolbar says
 * what happened, and everything else carries on. A launcher that will not open because a blog is down
 * would be a bad joke.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Launch;

public sealed class NewsChecker(HttpClient client, string feedUrl)
{
    private readonly object _gate = new();

    private Task? _running;

    /// <summary>Raised on the calling context when a load finishes, succeeded or failed.</summary>
    public event EventHandler? NewsLoaded;

    public IReadOnlyList<NewsEntry> Entries { get; private set; } = [];

    /// <summary>The fork's own server entries out of the feed. Nothing consumes these yet.</summary>
    public IReadOnlyList<string> ServerList { get; private set; } = [];

    public bool IsLoading
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    /// <summary>Empty when the last load worked, or when none has run.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>True when this build has a feed to fetch at all.</summary>
    /// <remarks>
    /// A fork can leave the URL empty, in which case the toolbar should have no news on it rather
    /// than a permanent "failed to load".
    /// </remarks>
    public bool IsConfigured => feedUrl.Length != 0;

    /// <summary>Fetches the feed. A second call while one is running is ignored, as upstream does.</summary>
    public Task ReloadAsync()
    {
        lock (_gate)
        {
            if (_running is not null)
            {
                // Upstream logs "Ignored request to reload news. Currently reloading already." and
                // returns. Handing back the running task means a caller that awaits still waits for
                // the right thing.
                return _running;
            }

            /*
             * THE TASK IS PUBLISHED BEFORE THE WORK STARTS, through a completion source, rather than
             * by assigning the result of LoadAsync(). An async method runs synchronously up to its
             * first await, so if the fetch ever completed without yielding, its finally would clear
             * _running before this assignment set it -- leaving the checker permanently "loading"
             * and refusing every later reload.
             */
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _running = signal.Task;

            _ = LoadAsync(signal);

            /*
             * THE LOCAL, NOT THE FIELD, and this is not hypothetical: LoadAsync runs synchronously
             * until its first await, and an HttpClient over a handler that answers immediately never
             * yields at all. The whole method -- fetch, parse, finally -- therefore finishes inside
             * the line above, nulling _running (the lock is reentrant, so it does not even block),
             * and "return _running" hands back null. The caller then awaits null and gets a
             * NullReferenceException with no frames in it, which is exactly what happened here.
             */
            return signal.Task;
        }
    }

    private async Task LoadAsync(TaskCompletionSource signal)
    {
        try
        {
            if (!IsConfigured)
            {
                Fail("This build has no news feed configured.");

                return;
            }

            var xml = await client.GetStringAsync(feedUrl).ConfigureAwait(false);

            var feed = NewsFeed.Parse(xml);

            Entries = feed.Entries;
            ServerList = feed.ServerList;
            LastError = string.Empty;
        }
        catch (System.Xml.XmlException ex)
        {
            /*
             * Upstream reports the parse error WITH its line and column, and this keeps that: a feed
             * that has gone malformed is a different problem from a feed that is unreachable, and the
             * two are indistinguishable from "failed to load news".
             *
             * EXCEPT WHEN THERE IS NOWHERE TO LOOK. Probing the real server with a wrong path came
             * back as 200 with a body that is not XML at all, and the message read "Root element is
             * missing. at line 0, column 0." -- a position that does not exist, offered to somebody
             * who has no file to open. That case says what actually happened instead.
             */
            Fail(ex.LineNumber > 0
                ? $"The news feed could not be read: {ex.Message} at line {ex.LineNumber}, column {ex.LinePosition}."
                : "The news feed did not come back as a feed. The server answered with something else.");
        }
        catch (HttpRequestException ex)
        {
            Fail($"The news feed could not be fetched: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            // A timeout. Says so plainly rather than as "A task was canceled", which tells nobody
            // anything.
            Fail("The news feed took too long to answer.");
        }
        catch (Exception ex)
        {
            // Anything else at all. News is the least important thing here and must not take the
            // startup with it.
            Fail("The news feed could not be loaded: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }

            // Fired on whatever thread the fetch finished on, so a view model listening to this has
            // to get itself back onto the UI thread. Said here because the alternative is a crash
            // that only happens when the network is slow.
            NewsLoaded?.Invoke(this, EventArgs.Empty);

            signal.TrySetResult();
        }
    }

    private void Fail(string message)
    {
        LastError = message;

        // The previously loaded entries are DELIBERATELY KEPT. A failed refresh should leave what was
        // on the toolbar there rather than replacing real news with nothing.
    }
}
