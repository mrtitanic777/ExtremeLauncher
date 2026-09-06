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
 * The news button on the toolbar.
 *
 * HALF OF THESE ARE SUBSCRIPTION TESTS rather than value tests, and that is deliberate: the news
 * arrives long after the window opens, so every one of these properties is read once at startup and
 * then only ever updated by an announcement. A value assertion passes whether or not the announcement
 * happens -- reading recomputes -- and the control stays dead on screen. It is the same bug this port
 * has now shipped four times.
 */

using System.ComponentModel;
using System.Net;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class NewsViewModelTests
{
    private const string Feed = """
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry><title>Newest</title><id>https://example.invalid/2</id><content type="html">&lt;p&gt;hi&lt;/p&gt;</content></entry>
          <entry><title>Older</title><id>https://example.invalid/1</id><content type="html">&lt;p&gt;ho&lt;/p&gt;</content></entry>
        </feed>
        """;

    private sealed class Stub(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        public TaskCompletionSource? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Hold is not null)
            {
                await Hold.Task.ConfigureAwait(false);
            }

            return reply();
        }
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static NewsChecker Checker(HttpMessageHandler handler, string url = "https://example.invalid/news.xml")
        => new(new HttpClient(handler), url);

    private sealed class RecordingUi : INewsUi
    {
        public int Calls { get; private set; }

        public bool LastHidden { get; private set; }

        public IReadOnlyList<NewsEntry> LastEntries { get; private set; } = [];

        public Task ShowAsync(IReadOnlyList<NewsEntry> entries, bool startWithListHidden)
        {
            Calls++;
            LastEntries = entries;
            LastHidden = startWithListHidden;

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TheNewestHeadlineIsWhatTheToolbarShows()
    {
        var vm = new NewsViewModel(Checker(new Stub(() => Ok(Feed))));

        await vm.LoadAsync();

        Assert.Equal("Newest", vm.Label);
        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public async Task ItSaysSoWhileItIsLoading()
    {
        // Upstream's exact string, and its exact behaviour: not clickable until it has arrived.
        var stub = new Stub(() => Ok(Feed)) { Hold = new TaskCompletionSource() };

        var vm = new NewsViewModel(Checker(stub));

        var loading = vm.LoadAsync();

        Assert.Equal("Loading news...", vm.Label);
        Assert.False(vm.CanShow);

        stub.Hold.SetResult();

        await loading;

        Assert.Equal("Newest", vm.Label);
        Assert.True(vm.CanShow);
    }

    [Fact]
    public async Task TheLabelIsAnnouncedRatherThanOnlyChanged()
    {
        /*
         * THE BUG CLASS. The news arrives after the window is on screen, so if nothing raises
         * PropertyChanged the toolbar keeps saying "No news available." forever -- and every value
         * assertion above still passes, because reading the property recomputes it.
         */
        var vm = new NewsViewModel(Checker(new Stub(() => Ok(Feed))));

        var announced = new List<string>();

        vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        await vm.LoadAsync();

        Assert.Contains(nameof(NewsViewModel.Label), announced);
        Assert.Contains(nameof(NewsViewModel.CanShow), announced);
    }

    [Fact]
    public async Task AFeedThatIsDownSaysWhySomewhere()
    {
        /*
         * Upstream's updateNewsLabel IGNORES the error entirely, so a feed that is down and a feed
         * that is empty look identical on the toolbar. They are not the same problem and only one of
         * them is worth waiting out.
         */
        var vm = new NewsViewModel(Checker(new Stub(() => new HttpResponseMessage(HttpStatusCode.BadGateway))));

        await vm.LoadAsync();

        Assert.Equal("No news available.", vm.Label);
        Assert.NotEqual(string.Empty, vm.Tooltip);
        Assert.False(vm.CanShow);
    }

    [Fact]
    public async Task AStaleHeadlineAdmitsItIsStale()
    {
        // A failed refresh leaves the old headline up, which otherwise looks exactly like a fresh one.
        var fail = false;

        var vm = new NewsViewModel(Checker(new Stub(() => fail
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Ok(Feed))));

        await vm.LoadAsync();

        fail = true;

        await vm.LoadAsync();

        Assert.Equal("Newest", vm.Label);
        Assert.Contains("last news that loaded", vm.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoCheckerThereIsNoNews()
    {
        // A build with the feed URL unset, and the constructor used by every other test in the suite.
        var vm = new NewsViewModel();

        Assert.False(vm.IsAvailable);
        Assert.False(vm.CanShow);
        Assert.Equal("No news available.", vm.Label);
    }

    [Fact]
    public async Task ABuildWithNoFeedUrlNeverSaysItIsLoading()
    {
        var vm = new NewsViewModel(Checker(new Stub(() => Ok(Feed)), string.Empty));

        await vm.LoadAsync();

        Assert.False(vm.IsAvailable);
        Assert.False(vm.CanShow);
    }

    [Fact]
    public async Task TheMainWindowOffersNewsOnlyWhenThereIsSomeAndSomewhereToShowIt()
    {
        var ui = new RecordingUi();

        var checker = Checker(new Stub(() => Ok(Feed)));

        var vm = new MainWindowViewModel(news: ui, newsChecker: checker);

        Assert.False(vm.CanShowNews);

        await vm.News.LoadAsync();

        Assert.True(vm.CanShowNews);
    }

    [Fact]
    public async Task TheMainWindowAnnouncesThatNewsBecameAvailable()
    {
        // Same bug class again, one level up: the toolbar button's IsEnabled binds to this.
        var vm = new MainWindowViewModel(news: new RecordingUi(), newsChecker: Checker(new Stub(() => Ok(Feed))));

        var announced = new List<string>();

        vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        await vm.News.LoadAsync();

        Assert.Contains(nameof(MainWindowViewModel.CanShowNews), announced);
    }

    [Fact]
    public async Task ClickingTheHeadlineOpensTheStoryRatherThanTheIndex()
    {
        /*
         * Upstream's newsButtonClicked calls toggleArticleList before exec, so clicking the headline
         * opens with the list collapsed, while More News opens with it showing. Small, and the sort
         * of thing a port drops without noticing.
         */
        var ui = new RecordingUi();

        var vm = new MainWindowViewModel(news: ui, newsChecker: Checker(new Stub(() => Ok(Feed))));

        await vm.News.LoadAsync();

        await vm.ShowNewsAsync(fromHeadline: true);

        Assert.True(ui.LastHidden);

        await vm.ShowNewsAsync();

        Assert.False(ui.LastHidden);
        Assert.Equal(2, ui.Calls);
    }

    [Fact]
    public async Task WithNothingLoadedTheWindowIsNotOpened()
    {
        // An empty news window is worse than a disabled button.
        var ui = new RecordingUi();

        var vm = new MainWindowViewModel(news: ui, newsChecker: Checker(new Stub(() => new HttpResponseMessage(HttpStatusCode.NotFound))));

        await vm.News.LoadAsync();

        await vm.ShowNewsAsync();

        Assert.Equal(0, ui.Calls);
    }

    [Fact]
    public async Task WhatTheWindowGetsIsWhatTheToolbarHas()
    {
        var ui = new RecordingUi();

        var vm = new MainWindowViewModel(news: ui, newsChecker: Checker(new Stub(() => Ok(Feed))));

        await vm.News.LoadAsync();
        await vm.ShowNewsAsync();

        Assert.Equal(["Newest", "Older"], ui.LastEntries.Select(e => e.Title));
    }
}
