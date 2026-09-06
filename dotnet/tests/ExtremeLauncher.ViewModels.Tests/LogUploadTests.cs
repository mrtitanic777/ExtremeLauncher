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
 * Publishing a log, and every way that must not happen by accident.
 *
 * A MINECRAFT LOG IS NOT NEUTRAL TEXT. It carries the player's user name, their home directory, the
 * mods they run, sometimes a server address, and on a bad day a session token. Uploading it is
 * IRREVERSIBLE -- the link is public and most of these services have no delete.
 *
 * So nearly every test here is a way of asserting that nothing leaves the machine unless somebody
 * said yes to a question that named where it was going.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Tasks;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class LogUploadTests
{
    private sealed class StubUploader(string destination = "api.mclo.gs", LogUploadResult? result = null) : ILogUploader
    {
        public string Destination { get; } = destination;

        public int Calls { get; private set; }

        public string LastText { get; private set; } = string.Empty;

        public Task<LogUploadResult> UploadAsync(string text)
        {
            Calls++;
            LastText = text;

            return Task.FromResult(result ?? LogUploadResult.Success("https://mclo.gs/abc123"));
        }
    }

    private sealed class StubPrompts(bool answer) : IUserPrompts
    {
        public string? Title { get; private set; }

        public string? Message { get; private set; }

        public bool? WasDestructive { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        {
            Title = title;
            Message = message;
            WasDestructive = destructive;

            return Task.FromResult(answer);
        }

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult<string?>(null);
    }

    /// <summary>A launcher that does nothing, so a coordinator can be built to hold log lines.</summary>
    private sealed class IdleLauncher : IInstanceLauncher
    {
        public Task LaunchAsync(string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
            => Task.CompletedTask;
    }

    private static LaunchCoordinator ConsoleWith(params string[] lines)
    {
        var coordinator = new LaunchCoordinator(new IdleLauncher());

        foreach (var line in lines)
        {
            coordinator.LogLines.Add(new LaunchLogLine(line, false));
        }

        return coordinator;
    }

    private sealed class StubClipboard(bool works = true) : IClipboard
    {
        public string? Text { get; private set; }

        public Task<bool> SetTextAsync(string text)
        {
            Text = text;

            return Task.FromResult(works);
        }
    }

    [Fact]
    public async Task NothingIsUploadedUntilSomebodySaysYes()
    {
        // The whole point.
        var uploader = new StubUploader();

        await LogUpload.RunAsync("secret log", "latest.log", uploader, new StubPrompts(answer: false), new StubClipboard());

        Assert.Equal(0, uploader.Calls);
    }

    [Fact]
    public async Task SayingNoSaysNothingBack()
    {
        // A status line after a cancel reads as though something happened.
        var status = await LogUpload.RunAsync("log", "latest.log", new StubUploader(), new StubPrompts(false), new StubClipboard());

        Assert.Equal(string.Empty, status);
    }

    [Fact]
    public async Task TheQuestionNamesTheHostAndTheFile()
    {
        /*
         * A HOST, NOT A URL, and the file's name. "Upload?" is not a question anybody can answer;
         * "upload latest.log to api.mclo.gs?" is.
         */
        var prompts = new StubPrompts(answer: true);

        await LogUpload.RunAsync("log", "latest.log", new StubUploader("api.mclo.gs"), prompts, new StubClipboard());

        Assert.Contains("latest.log", prompts.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("api.mclo.gs", prompts.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQuestionSaysWhatCanBeInALogAndThatItCannotBeUndone()
    {
        /*
         * Upstream says "You should double-check for personal information." This says what that
         * information is, because "personal information" is abstract and "your user name and the
         * servers you have joined" is not -- and it says the link is public and permanent.
         */
        var prompts = new StubPrompts(answer: true);

        await LogUpload.RunAsync("log", "latest.log", new StubUploader(), prompts, new StubClipboard());

        var message = prompts.Message ?? string.Empty;

        Assert.Contains("user name", message, StringComparison.Ordinal);
        Assert.Contains("cannot be taken back", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAffirmativeIsNotTheDefaultButton()
    {
        /*
         * Flagged destructive so the window styles it as such and Return does not land on it. Not a
         * delete, but the same class of irreversible -- and this port has the same rule everywhere
         * else that something cannot be undone.
         */
        var prompts = new StubPrompts(answer: true);

        await LogUpload.RunAsync("log", "latest.log", new StubUploader(), prompts, new StubClipboard());

        Assert.True(prompts.WasDestructive);
    }

    [Fact]
    public async Task WithNoDialogServiceNothingIsUploaded()
    {
        /*
         * THE WORST POSSIBLE FAILURE MODE, guarded explicitly: a build or a test harness with no
         * dialog service must not decide that means "go ahead". Publishing somebody's log because a
         * window was unavailable is not a bug anybody could recover from.
         */
        var uploader = new StubUploader();

        var status = await LogUpload.RunAsync("log", "latest.log", uploader, prompts: null, new StubClipboard());

        Assert.Equal(0, uploader.Calls);
        Assert.Contains("confirmation dialog", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoUploaderTheActionSaysSoRatherThanFailingSilently()
    {
        var status = await LogUpload.RunAsync("log", "latest.log", uploader: null, new StubPrompts(true), new StubClipboard());

        Assert.Contains("not available", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyLogIsNotUploaded()
    {
        // Creating an empty public paste helps nobody and still costs a request.
        var uploader = new StubUploader();

        var status = await LogUpload.RunAsync(string.Empty, "latest.log", uploader, new StubPrompts(true), new StubClipboard());

        Assert.Equal(0, uploader.Calls);
        Assert.Contains("nothing to upload", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLinkGoesToTheClipboard()
    {
        // Getting the link to the clipboard IS the feature -- it is useless in a window nobody can
        // copy out of.
        var clipboard = new StubClipboard();

        var status = await LogUpload.RunAsync("log", "latest.log", new StubUploader(), new StubPrompts(true), clipboard);

        Assert.Equal("https://mclo.gs/abc123", clipboard.Text);
        Assert.Contains("clipboard", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoClipboardTheLinkIsStillShown()
    {
        // Otherwise a successful upload leaves somebody with no way to reach what they just made.
        var status = await LogUpload.RunAsync(
            "log",
            "latest.log",
            new StubUploader(),
            new StubPrompts(true),
            new StubClipboard(works: false));

        Assert.Contains("https://mclo.gs/abc123", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedUploadSaysWhy()
    {
        // "Upload failed" tells nobody whether to retry, wait, or pick another service.
        var status = await LogUpload.RunAsync(
            "log",
            "latest.log",
            new StubUploader(result: LogUploadResult.Failure("Log is too large")),
            new StubPrompts(true),
            new StubClipboard());

        Assert.Contains("Log is too large", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIsUploadedIsWhatWasShown()
    {
        // Sounds obvious; it is the thing that makes the confirmation meaningful. Asking about
        // "latest.log" and sending something else would make the whole flow a lie.
        var uploader = new StubUploader();

        await LogUpload.RunAsync("the exact contents", "latest.log", uploader, new StubPrompts(true), new StubClipboard());

        Assert.Equal("the exact contents", uploader.LastText);
    }

    [Fact]
    public async Task TheConsoleUploadsTheLogItIsShowing()
    {
        /*
         * THE MORE USEFUL OF THE TWO BUTTONS: this is the log of the launch that just went wrong, and
         * the one somebody has open when they decide to go and ask for help.
         *
         * What is sent has to be what is on screen -- asking about "this launch's log" and sending
         * something else would make the confirmation a lie.
         */
        var launch = ConsoleWith("first line", "second line");

        var uploader = new StubUploader();

        var page = new LogPageViewModel(launch, new StubClipboard(), uploader, new StubPrompts(answer: true));

        Assert.True(page.CanUpload);

        await page.UploadAsync();

        Assert.Equal("first line" + Environment.NewLine + "second line", uploader.LastText);
        Assert.Contains("clipboard", page.CopyStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConsoleAsksBeforeUploadingToo()
    {
        // The careful part is shared rather than written twice, and this is what asserts the second
        // caller actually went through it.
        var launch = ConsoleWith("a line");

        var uploader = new StubUploader();

        var page = new LogPageViewModel(launch, new StubClipboard(), uploader, new StubPrompts(answer: false));

        await page.UploadAsync();

        Assert.Equal(0, uploader.Calls);
    }

    [Fact]
    public void TheConsoleHidesTheButtonWhenThereIsNoUploader()
    {
        Assert.False(new LogPageViewModel(ConsoleWith("a line")).CanUpload);
    }

    [Fact]
    public async Task TheLogsPageOffersItOnlyWhenItWouldWork()
    {
        var page = new OtherLogsPageViewModel();

        Assert.False(page.CanUpload);

        var wired = new OtherLogsPageViewModel(
            uploader: new StubUploader(),
            prompts: new StubPrompts(true));

        Assert.True(wired.CanUpload);

        // And a build whose uploader has nowhere to send anything does not offer it either.
        Assert.False(new OtherLogsPageViewModel(
            uploader: new StubUploader(destination: string.Empty),
            prompts: new StubPrompts(true)).CanUpload);

        await Task.CompletedTask;
    }
}
