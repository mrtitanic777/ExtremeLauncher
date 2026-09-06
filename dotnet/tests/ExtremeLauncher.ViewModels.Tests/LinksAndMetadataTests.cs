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
 * The help links and the metadata cache.
 *
 * The interesting half is what is NOT offered: most of these URLs are empty by default, and an entry
 * that opens nothing looks like the launcher is broken rather than like the fork has no Discord.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class LinksAndMetadataTests : IDisposable
{
    private readonly BuildConfig _original = BuildConfig.Instance;

    /// <remarks>
    /// BuildConfig.Instance is a settable static, so a test that changes it has to put it back --
    /// otherwise it leaks into every test that runs afterwards in the same assembly.
    /// </remarks>
    public void Dispose() => BuildConfig.Instance = _original;

    private sealed class StubLinks : ILinkOpener
    {
        public List<string> Opened { get; } = [];

        public Task OpenAsync(string url)
        {
            Opened.Add(url);

            return Task.CompletedTask;
        }
    }

    private sealed class StubCache(string message = "Cleared 3 cached metadata files.") : IMetadataCache
    {
        public int Calls { get; private set; }

        public Task<string> ClearAsync()
        {
            Calls++;

            return Task.FromResult(message);
        }
    }

    private sealed class StubPrompts(bool answer) : IUserPrompts
    {
        public string? AskedTitle { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        {
            AskedTitle = title;

            return Task.FromResult(answer);
        }

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult<string?>(null);
    }

    [Fact]
    public void OnlyTheLinksThisBuildHasAreOffered()
    {
        /*
         * Upstream's CMakeLists leaves the Discord, Matrix and subreddit URLs empty for a fork to
         * fill in, and this fork has not. Offering them anyway is offering a link to nowhere.
         */
        BuildConfig.Instance = _original with
        {
            HelpUrl = string.Empty,
            DiscordUrl = string.Empty,
            MatrixUrl = string.Empty,
            SubredditUrl = string.Empty,
        };

        var vm = new MainWindowViewModel(links: new StubLinks());

        Assert.DoesNotContain(vm.Links, l => l.Label == "Discord");
        Assert.DoesNotContain(vm.Links, l => l.Label == "Help");

        // The ones this fork does configure are there.
        Assert.Contains(vm.Links, l => l.Label == "Report a bug");
        Assert.Contains(vm.Links, l => l.Label == "Source code");
    }

    [Fact]
    public void TheHelpLinkIsOfferedByDefaultAndPointsAtTheHelpEndpoint()
    {
        /*
         * Help was silently absent from the menu because HelpUrl was empty, even though the fork
         * configures a help endpoint and serves it. With the default build config it is offered and
         * points at the launcher's help endpoint -- upstream's on_actionOpenWiki opens exactly this,
         * with an empty page argument.
         */
        var vm = new MainWindowViewModel(links: new StubLinks());

        var help = Assert.Single(vm.Links, l => l.Label == "Help");

        Assert.StartsWith("https://extremelauncher.net/_api/help", help.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslatePointsAtTheForksWeblateRatherThanAnInventedRepo()
    {
        // It used to point at github.com/ExtremeLauncher/Translations -- wrong org, and a 404.
        var vm = new MainWindowViewModel(links: new StubLinks());

        var translate = Assert.Single(vm.Links, l => l.Label == "Translate");

        Assert.Contains("weblate.org", translate.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com/ExtremeLauncher/", translate.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredLinkAppears()
    {
        BuildConfig.Instance = _original with { DiscordUrl = "https://discord.gg/example" };

        var vm = new MainWindowViewModel(links: new StubLinks());

        Assert.Contains(vm.Links, l => l.Label == "Discord" && l.Url == "https://discord.gg/example");
    }

    [Fact]
    public void EveryOfferedLinkHasAUrl()
    {
        // The whole filter, stated as the property that matters.
        var vm = new MainWindowViewModel(links: new StubLinks());

        Assert.All(vm.Links, l => Assert.NotEqual(string.Empty, l.Url));
    }

    [Fact]
    public async Task OpeningALinkPassesItThrough()
    {
        var links = new StubLinks();

        var vm = new MainWindowViewModel(links: links);

        await vm.OpenLinkAsync("https://example.invalid/page");

        Assert.Equal(["https://example.invalid/page"], links.Opened);
    }

    [Fact]
    public async Task OpeningNothingDoesNothing()
    {
        var links = new StubLinks();

        var vm = new MainWindowViewModel(links: links);

        await vm.OpenLinkAsync(null);
        await vm.OpenLinkAsync(string.Empty);

        Assert.Empty(links.Opened);
    }

    [Fact]
    public void WithNoOpenerTheMenuIsDisabled()
    {
        var vm = new MainWindowViewModel();

        Assert.False(vm.CanOpenLinks);
    }

    [Fact]
    public async Task ClearingTheMetadataCacheAsksFirst()
    {
        /*
         * Nothing is lost -- every byte is re-downloadable -- but it is still a delete, and a menu
         * item that silently throws something away is one people learn to be afraid of.
         */
        var prompts = new StubPrompts(answer: true);
        var cache = new StubCache();

        var vm = new MainWindowViewModel(prompts: prompts, metadata: cache);

        await vm.ClearMetadataAsync();

        Assert.Equal("Clear the metadata cache?", prompts.AskedTitle);
        Assert.Equal(1, cache.Calls);
    }

    [Fact]
    public async Task SayingNoClearsNothing()
    {
        var cache = new StubCache();

        var vm = new MainWindowViewModel(prompts: new StubPrompts(answer: false), metadata: cache);

        await vm.ClearMetadataAsync();

        Assert.Equal(0, cache.Calls);
    }

    [Fact]
    public async Task WhatWasClearedIsReported()
    {
        // "Done" tells somebody nothing about whether the thing they were trying to fix was even
        // cached in the first place.
        var vm = new MainWindowViewModel(
            prompts: new StubPrompts(answer: true),
            metadata: new StubCache("Cleared 42 cached metadata files."));

        await vm.ClearMetadataAsync();

        Assert.Contains(vm.Launch.LogLines, l => l.Text.Contains("42 cached", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoCacheServiceTheEntryIsDisabled()
    {
        var vm = new MainWindowViewModel();

        Assert.False(vm.CanClearMetadata);
    }
}
