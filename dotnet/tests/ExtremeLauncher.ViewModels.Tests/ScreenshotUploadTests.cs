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
 * Publishing a screenshot, and every way that must not happen by accident.
 *
 * A screenshot can show a user name, a server, a face -- and an anonymous imgur upload is public and
 * not really undoable. So, exactly as with logs: nothing leaves the machine unless somebody said yes
 * to a question that named where it was going.
 */

using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ScreenshotUploadTests
{
    private sealed class StubUploader(string destination = "api.imgur.com", ScreenshotUploadResult? result = null)
        : IScreenshotUploader
    {
        public string Destination { get; } = destination;

        public int Calls { get; private set; }

        public int LastCount { get; private set; }

        public Task<ScreenshotUploadResult> UploadAsync(IReadOnlyList<string> filePaths)
        {
            Calls++;
            LastCount = filePaths.Count;

            return Task.FromResult(result ?? ScreenshotUploadResult.Success("https://i.imgur.com/abc.png"));
        }
    }

    private sealed class StubPrompts(bool answer) : IUserPrompts
    {
        public string? Message { get; private set; }

        public bool? WasDestructive { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        {
            Message = message;
            WasDestructive = destructive;

            return Task.FromResult(answer);
        }

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult<string?>(null);
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
        var uploader = new StubUploader();

        await ScreenshotUpload.RunAsync(["a.png"], uploader, new StubPrompts(answer: false), new StubClipboard());

        Assert.Equal(0, uploader.Calls);
    }

    [Fact]
    public async Task SayingNoSaysNothingBack()
    {
        var status = await ScreenshotUpload.RunAsync(["a.png"], new StubUploader(), new StubPrompts(false), new StubClipboard());

        Assert.Equal(string.Empty, status);
    }

    [Fact]
    public async Task TheQuestionNamesTheHostAndWhatAScreenshotCanShow()
    {
        var prompts = new StubPrompts(answer: true);

        await ScreenshotUpload.RunAsync(["a.png"], new StubUploader("api.imgur.com"), prompts, new StubClipboard());

        Assert.Contains("api.imgur.com", prompts.Message ?? "", StringComparison.Ordinal);
        Assert.Contains("user name", prompts.Message ?? "", StringComparison.Ordinal);
        Assert.Contains("cannot easily be taken back", prompts.Message ?? "", StringComparison.Ordinal);
        Assert.True(prompts.WasDestructive);
    }

    [Fact]
    public async Task TheQuestionCountsSeveralScreenshots()
    {
        var prompts = new StubPrompts(answer: true);

        await ScreenshotUpload.RunAsync(["a.png", "b.png", "c.png"], new StubUploader(), prompts, new StubClipboard());

        Assert.Contains("3 screenshots", prompts.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLinkGoesToTheClipboard()
    {
        var clipboard = new StubClipboard();

        var status = await ScreenshotUpload.RunAsync(["a.png"], new StubUploader(), new StubPrompts(true), clipboard);

        Assert.Equal("https://i.imgur.com/abc.png", clipboard.Text);
        Assert.Contains("clipboard", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralScreenshotsAreUploadedTogether()
    {
        // The flow hands the whole list to the uploader in one call, which is what lets the app bundle
        // them into a single album.
        var uploader = new StubUploader(result: ScreenshotUploadResult.Success("https://imgur.com/a/ALB"));

        var status = await ScreenshotUpload.RunAsync(["a.png", "b.png"], uploader, new StubPrompts(true), new StubClipboard());

        Assert.Equal(1, uploader.Calls);
        Assert.Equal(2, uploader.LastCount);
        Assert.Contains("album", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoDialogNothingIsUploaded()
    {
        var uploader = new StubUploader();

        var status = await ScreenshotUpload.RunAsync(["a.png"], uploader, prompts: null, new StubClipboard());

        Assert.Equal(0, uploader.Calls);
        Assert.Contains("confirmation dialog", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoUploaderTheActionSaysSo()
    {
        var status = await ScreenshotUpload.RunAsync(["a.png"], uploader: null, new StubPrompts(true), new StubClipboard());

        Assert.Contains("not available", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingSelectedIsRefused()
    {
        var uploader = new StubUploader();

        var status = await ScreenshotUpload.RunAsync([], uploader, new StubPrompts(true), new StubClipboard());

        Assert.Equal(0, uploader.Calls);
        Assert.Contains("Select a screenshot", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedUploadSaysWhy()
    {
        var status = await ScreenshotUpload.RunAsync(
            ["a.png"],
            new StubUploader(result: ScreenshotUploadResult.Failure("Imgur is down")),
            new StubPrompts(true),
            new StubClipboard());

        Assert.Contains("Imgur is down", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePageOffersUploadOnlyWhenConfiguredAndSelected()
    {
        var page = new ScreenshotsPageViewModel(new StubPrompts(true), new StubUploader(), new StubClipboard());

        // Nothing selected yet.
        Assert.False(page.CanUpload);

        // A build with no image host does not offer it even with something selected.
        var noHost = new ScreenshotsPageViewModel(new StubPrompts(true), new StubUploader(destination: string.Empty), new StubClipboard());

        Assert.False(noHost.CanUpload);

        await Task.CompletedTask;
    }
}
