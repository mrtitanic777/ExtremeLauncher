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
 * Fetching the news feed.
 *
 * NEWS IS THE LEAST IMPORTANT THING THE LAUNCHER DOES and the first thing it asks the network for, so
 * most of these are about failing quietly: nothing here may throw at the startup path, and nothing
 * here may leave the toolbar stuck.
 */

using System.Net;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class NewsCheckerTests
{
    private const string Feed = """
        <feed xmlns="http://www.w3.org/2005/Atom">
          <serverlistentry>play.example.invalid</serverlistentry>
          <entry><title>Newest</title><id>https://example.invalid/2</id><content type="html">&lt;p&gt;hi&lt;/p&gt;</content></entry>
          <entry><title>Older</title><id>https://example.invalid/1</id><content type="html">&lt;p&gt;ho&lt;/p&gt;</content></entry>
        </feed>
        """;

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public TaskCompletionSource? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;

            if (Hold is not null)
            {
                await Hold.Task.ConfigureAwait(false);
            }

            return reply(request);
        }
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static NewsChecker Checker(Stub stub, string url = "https://example.invalid/news.xml")
        => new(new HttpClient(stub), url);

    [Fact]
    public async Task AFeedIsFetchedAndParsed()
    {
        var checker = Checker(new Stub(_ => Ok(Feed)));

        await checker.ReloadAsync();

        Assert.Equal(["Newest", "Older"], checker.Entries.Select(e => e.Title));
        Assert.Equal(["play.example.invalid"], checker.ServerList);
        Assert.Equal(string.Empty, checker.LastError);
    }

    [Fact]
    public async Task TheUrlItWasGivenIsTheOneItAsksFor()
    {
        var asked = new List<string>();

        var stub = new Stub(r =>
        {
            asked.Add(r.RequestUri!.ToString());

            return Ok(Feed);
        });

        await Checker(stub, "https://extremelauncher.net/_api/news.xml").ReloadAsync();

        Assert.Equal(["https://extremelauncher.net/_api/news.xml"], asked);
    }

    [Fact]
    public async Task AServerErrorIsReportedRatherThanThrown()
    {
        /*
         * THE ONE THAT MATTERS MOST. This runs during startup. A throw here would be an unhandled
         * exception on a background task because a blog was down.
         */
        var checker = Checker(new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await checker.ReloadAsync();

        Assert.NotEqual(string.Empty, checker.LastError);
        Assert.Empty(checker.Entries);
    }

    [Fact]
    public async Task AMalformedFeedSaysWhereItBroke()
    {
        // A feed that has gone malformed is a different problem from one that is unreachable, and
        // "failed to load news" cannot tell them apart. Upstream reports the line and column.
        var checker = Checker(new Stub(_ => Ok("<feed><entry></feed>")));

        await checker.ReloadAsync();

        Assert.Contains("line", checker.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SomethingThatIsNotAFeedAtAllDoesNotGetAMadeUpPosition()
    {
        /*
         * FOUND BY PROBING THE REAL SERVER. Asking extremelauncher.net for a path that does not exist
         * answers 200 with a body that is not XML, and the parser's message for that is "Root element
         * is missing. at line 0, column 0." -- a position in a document nobody has, offered to
         * somebody with nothing to open. Saying what happened is more use than saying where.
         */
        var checker = Checker(new Stub(_ => Ok(string.Empty)));

        await checker.ReloadAsync();

        Assert.DoesNotContain("line 0", checker.LastError, StringComparison.Ordinal);
        Assert.Contains("not come back as a feed", checker.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheNewsThatWasAlreadyThere()
    {
        /*
         * A refresh that fails should leave what is on the toolbar alone. Replacing real news with
         * nothing, because the second fetch of the session timed out, is strictly worse than stale.
         */
        var fail = false;

        var checker = Checker(new Stub(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Ok(Feed)));

        await checker.ReloadAsync();

        fail = true;

        await checker.ReloadAsync();

        Assert.Equal(["Newest", "Older"], checker.Entries.Select(e => e.Title));
        Assert.NotEqual(string.Empty, checker.LastError);
    }

    [Fact]
    public async Task ASecondReloadWhileOneIsRunningIsIgnored()
    {
        // Upstream's behaviour exactly: "Ignored request to reload news. Currently reloading already."
        var stub = new Stub(_ => Ok(Feed)) { Hold = new TaskCompletionSource() };

        var checker = Checker(stub);

        var first = checker.ReloadAsync();
        var second = checker.ReloadAsync();

        Assert.True(checker.IsLoading);

        stub.Hold.SetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task ItStopsSayingItIsLoadingWhenItIsDone()
    {
        /*
         * The toolbar shows "Loading news..." off this flag and disables the button, so a checker
         * that never clears it leaves a permanently dead control. Also the reason the running task is
         * published through a completion source rather than by assigning the async method's result.
         */
        var checker = Checker(new Stub(_ => Ok(Feed)));

        Assert.False(checker.IsLoading);

        await checker.ReloadAsync();

        Assert.False(checker.IsLoading);

        // And a later reload still works, which is what a stuck flag would prevent.
        await checker.ReloadAsync();

        Assert.NotEmpty(checker.Entries);
    }

    [Fact]
    public async Task AFailedLoadStillLetsTheNextOneRun()
    {
        var fail = true;

        var checker = Checker(new Stub(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Ok(Feed)));

        await checker.ReloadAsync();

        Assert.False(checker.IsLoading);

        fail = false;

        await checker.ReloadAsync();

        Assert.NotEmpty(checker.Entries);
        Assert.Equal(string.Empty, checker.LastError);
    }

    [Fact]
    public async Task TheLoadedEventFiresOnFailureToo()
    {
        // The toolbar has to be told to stop saying "Loading news..." either way.
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var checker = Checker(stub);

        var fired = 0;

        checker.NewsLoaded += (_, _) => Interlocked.Increment(ref fired);

        await checker.ReloadAsync();

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task ABuildWithNoFeedUrlDoesNotAskForOne()
    {
        /*
         * A fork can leave the URL empty. Fetching "" would be an exception on every start, and the
         * toolbar would show a permanent failure for a feature this build simply does not have.
         */
        var stub = new Stub(_ => Ok(Feed));

        var checker = Checker(stub, string.Empty);

        Assert.False(checker.IsConfigured);

        await checker.ReloadAsync();

        Assert.Equal(0, stub.Calls);
        Assert.Empty(checker.Entries);
    }
}
