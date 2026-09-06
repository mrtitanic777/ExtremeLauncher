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
 * Uploading a log to a paste service.
 *
 * FOUR SERVICES THAT AGREE ABOUT NOTHING. Each has its own request body, its own content type and its
 * own place to find the resulting link, so most of these tests are about one service's particular
 * shape -- and about the ways a service can say "no" while returning 200.
 *
 * Driven through a stub handler rather than the real services: uploading test junk to a public paste
 * bin on every test run would be rude, and the shapes are pinned from upstream's parser.
 */

using System.Net;
using System.Text;
using ExtremeLauncher.Net;
using Xunit;

namespace ExtremeLauncher.Net.Tests;

public sealed class PasteUploadTests
{
    private sealed class StubHandler(HttpStatusCode status, string body, string? reason = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;

            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8),
                ReasonPhrase = reason,
            };
        }
    }

    private static async Task<(bool Ok, PasteUpload Task, StubHandler Handler)> UploadAsync(
        PasteType type,
        HttpStatusCode status,
        string body,
        string text = "a log line",
        string customBase = "",
        string? reason = null)
    {
        var handler = new StubHandler(status, body, reason);

        using var client = new HttpClient(handler);

        var task = new PasteUpload(client, text, type, customBase);

        return (await task.RunAsync(), task, handler);
    }

    [Fact]
    public void McLogsIsTheDefaultServiceInTheTable()
    {
        // The only one of the four that understands a Minecraft log: it folds stack traces and
        // highlights known errors, so the person helping gets more than raw text.
        Assert.Equal("mclo.gs", PasteUpload.Info(PasteType.Mclogs).Name);
        Assert.Equal(4, PasteUpload.PasteTypes.Count);
    }

    [Fact]
    public void TheEnumNumbersAreUpstreamsOnDiskFormat()
    {
        /*
         * PastebinType is stored as the RAW INTEGER of upstream's enum, so these numbers are shared
         * format, not an implementation detail. Reordering the enum would silently switch every
         * existing user to a different service.
         */
        Assert.Equal(0, (int)PasteType.NullPointer);
        Assert.Equal(1, (int)PasteType.Hastebin);
        Assert.Equal(2, (int)PasteType.PasteGG);
        Assert.Equal(3, (int)PasteType.Mclogs);
    }

    [Fact]
    public void ADeadServiceSaysSoRatherThanJustFailing()
    {
        /*
         * CHECKED AGAINST THE REAL INTERNET, 2026-08-20: paste.gg's domain does not resolve -- not a
         * 404, no DNS record at all -- while 0x0.st, hst.sh and api.mclo.gs all answer. Upstream
         * still offers it as one of four equal choices, so somebody who picks it gets a DNS failure
         * that reads like their own network being broken.
         *
         * It stays in the list because the enum's numbers are on-disk format, and it stays OFFERED
         * because vanishing from the picker would silently switch anybody who had it selected to a
         * service they never chose. It just says what it is.
         */
        Assert.True(PasteUpload.Info(PasteType.PasteGG).Retired);
        Assert.Contains("no longer available", PasteUpload.Info(PasteType.PasteGG).Label, StringComparison.Ordinal);

        // And nothing else is marked, because the other three answered.
        Assert.Equal(
            [PasteType.PasteGG],
            PasteUpload.PasteTypes.Where(t => t.Retired).Select(t => t.Type));
    }

    [Fact]
    public void AWorkingServiceIsNamedPlainly()
    {
        Assert.Equal("mclo.gs", PasteUpload.Info(PasteType.Mclogs).Label);
    }

    [Fact]
    public async Task McLogsPostsFormEncodedContentAndReadsTheUrl()
    {
        var (ok, task, handler) = await UploadAsync(
            PasteType.Mclogs,
            HttpStatusCode.OK,
            """{"success":true,"id":"abc123","url":"https://mclo.gs/abc123"}""");

        Assert.True(ok);
        Assert.Equal("https://mclo.gs/abc123", task.PasteLink);
        Assert.StartsWith("content=", handler.RequestBody, StringComparison.Ordinal);

        Assert.Equal(
            "application/x-www-form-urlencoded",
            handler.Request?.Content?.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task McLogsSayingNoWithA200IsAFailureNotALink()
    {
        /*
         * THE SHAPE THAT MATTERS MOST. mclo.gs answers 200 with "success": false when it rejects a
         * paste -- too large, rate limited, whatever. Trusting the status code alone would hand
         * somebody a link to nothing and tell them it worked.
         */
        var (ok, task, _) = await UploadAsync(
            PasteType.Mclogs,
            HttpStatusCode.OK,
            """{"success":false,"error":"Log is too large"}""");

        Assert.False(ok);
        Assert.Equal(string.Empty, task.PasteLink);
        Assert.Contains("Log is too large", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McLogsSayingYesWithNoUrlIsAlsoAFailure()
    {
        // success:true and no url is nonsense, and an empty link in the clipboard is worse than an
        // error message.
        var (ok, _, _) = await UploadAsync(PasteType.Mclogs, HttpStatusCode.OK, """{"success":true}""");

        Assert.False(ok);
    }

    [Fact]
    public async Task ZeroXZeroTakesTheBodyAsTheLink()
    {
        // 0x0.st answers with the URL as plain text. Nothing to parse, so nothing to go wrong -- but
        // the trailing newline is real and would end up in somebody's clipboard.
        var (ok, task, handler) = await UploadAsync(PasteType.NullPointer, HttpStatusCode.OK, "https://0x0.st/abc.txt\n");

        Assert.True(ok);
        Assert.Equal("https://0x0.st/abc.txt", task.PasteLink);
        Assert.StartsWith("multipart/form-data", handler.Request?.Content?.Headers.ContentType?.MediaType ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HastebinBuildsTheLinkFromTheKey()
    {
        var (ok, task, _) = await UploadAsync(PasteType.Hastebin, HttpStatusCode.OK, """{"key":"oqekivufoc"}""");

        Assert.True(ok);
        Assert.Equal("https://hst.sh/oqekivufoc", task.PasteLink);
    }

    [Fact]
    public async Task HastebinWithNoKeyIsMalformed()
    {
        var (ok, task, _) = await UploadAsync(PasteType.Hastebin, HttpStatusCode.OK, """{"message":"nope"}""");

        Assert.False(ok);
        Assert.Contains("malformed", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasteGgBuildsAnAnonymousLinkFromTheResultId()
    {
        var (ok, task, handler) = await UploadAsync(
            PasteType.PasteGG,
            HttpStatusCode.Created,
            """{"status":"success","result":{"id":"deadbeef"}}""");

        Assert.True(ok);
        Assert.Equal("https://paste.gg/p/anonymous/deadbeef", task.PasteLink);
        Assert.Contains("\"files\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"expires\"", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasteGgErrorsCarryBothTheCodeAndTheMessage()
    {
        var (ok, task, _) = await UploadAsync(
            PasteType.PasteGG,
            HttpStatusCode.OK,
            """{"status":"error","error":"bad_request","message":"file too big"}""");

        Assert.False(ok);
        Assert.Contains("bad_request", task.FailReason, StringComparison.Ordinal);
        Assert.Contains("file too big", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public void PasteGgGoesToItsApiHostRatherThanThePathItDocuments()
    {
        /*
         * HACK, upstream's, comment and all: paste.gg documents /api/v1 on the main host but the
         * official instance does not serve it there. Only the DEFAULT base gets the substitution --
         * somebody running their own instance presumably followed the documentation.
         */
        using var client = new HttpClient();

        Assert.Equal(
            "https://api.paste.gg/v1/pastes",
            new PasteUpload(client, "x", PasteType.PasteGG).UploadUrl);

        Assert.Equal(
            "https://paste.example.invalid/api/v1/pastes",
            new PasteUpload(client, "x", PasteType.PasteGG, "https://paste.example.invalid").UploadUrl);
    }

    [Fact]
    public void ACustomBaseReplacesTheServicesOwn()
    {
        using var client = new HttpClient();

        Assert.Equal(
            "https://paste.example.invalid/1/log",
            new PasteUpload(client, "x", PasteType.Mclogs, "https://paste.example.invalid").UploadUrl);
    }

    [Fact]
    public void ATrailingSlashOnACustomBaseDoesNotDoubleUp()
    {
        // What somebody actually types. "https://host//1/log" is a 404 on most services.
        using var client = new HttpClient();

        Assert.Equal(
            "https://paste.example.invalid/1/log",
            new PasteUpload(client, "x", PasteType.Mclogs, "https://paste.example.invalid/").UploadUrl);
    }

    [Fact]
    public async Task AnUnexpectedStatusCodeSaysWhichOne()
    {
        // "Upload failed" tells nobody whether the service is down, rate limiting them, or gone.
        var (ok, task, _) = await UploadAsync(
            PasteType.Mclogs,
            HttpStatusCode.ServiceUnavailable,
            "<html>nope</html>",
            reason: "Service Unavailable");

        Assert.False(ok);
        Assert.Contains("503", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHtmlErrorPageIsAMalformedBodyRatherThanACrash()
    {
        // A proxy or a captive portal answering 200 with HTML. JsonNode.Parse throws on it, and an
        // unhandled exception mid-upload would take the window with it.
        var (ok, task, _) = await UploadAsync(PasteType.Mclogs, HttpStatusCode.OK, "<html><body>hello</body></html>");

        Assert.False(ok);
        Assert.Contains("malformed", task.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABigLogStillEncodes()
    {
        /*
         * FormUrlEncodedContent would throw here: it escapes through a Uri helper with a length
         * limit that a few megabytes walks straight past. And a log big enough to hit it is exactly
         * the log somebody most needs to share.
         */
        var big = new string('x', 3 * 1024 * 1024);

        var (ok, task, _) = await UploadAsync(
            PasteType.Mclogs,
            HttpStatusCode.OK,
            """{"success":true,"url":"https://mclo.gs/big"}""",
            text: big);

        Assert.True(ok);
        Assert.Equal("https://mclo.gs/big", task.PasteLink);
    }

    [Fact]
    public void TheHostIsWhatTheConfirmationShows()
    {
        /*
         * A HOST, NOT A URL. "Upload to https://api.mclo.gs/1/log?" invites nobody to think about
         * where their log is going; "upload to api.mclo.gs?" does.
         */
        Assert.Equal("api.mclo.gs", PasteUpload.HostFor(PasteType.Mclogs, string.Empty));
        Assert.Equal("0x0.st", PasteUpload.HostFor(PasteType.NullPointer, string.Empty));
        Assert.Equal("paste.example.invalid", PasteUpload.HostFor(PasteType.Mclogs, "https://paste.example.invalid"));
    }

    [Fact]
    public void AnUnparseableCustomBaseStillShowsSomething()
    {
        // A blank confirmation dialog is worse than an ugly one.
        Assert.Equal("not a url", PasteUpload.HostFor(PasteType.Mclogs, "not a url"));
    }
}
