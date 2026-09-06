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
 * Uploading a screenshot to imgur.
 *
 * Driven through a stub handler: uploading real images to imgur on every test run would be rude, and
 * the shapes are pinned from upstream's parser. The cases that matter are the ones that go wrong while
 * the transport looks fine -- a success:false envelope, a missing Client-ID -- because those are how
 * an upload hands back an empty link and calls it done.
 */

using System.Net;
using System.Text;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;
using Xunit;

namespace ExtremeLauncher.Net.Tests;

public sealed class ImgurUploadTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-imgur-" + Guid.NewGuid().ToString("N"));

    public ImgurUploadTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Authorization { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Authorization = request.Headers.TryGetValues("Authorization", out var v) ? string.Join("", v) : null;

            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        }
    }

    private string WriteImage(string name = "shot.png")
    {
        var path = Path.Combine(_temp, name);

        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]); // a stub PNG signature; contents do not matter here

        return path;
    }

    private static ImgurClient Client(StubHandler handler, string clientId = "test-client")
        => new(new HttpClient(handler), clientId, "https://api.imgur.com/3/");

    [Fact]
    public async Task AnUploadReturnsTheLinkFromTheSuccessEnvelope()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"success":true,"status":200,"data":{"id":"abc","link":"https://i.imgur.com/abc.png","deletehash":"xyz"}}""");

        var result = await Client(handler).UploadAsync(WriteImage());

        Assert.Equal("abc", result.Id);
        Assert.Equal("https://i.imgur.com/abc.png", result.Link);
        Assert.Equal("xyz", result.DeleteHash);
    }

    [Fact]
    public async Task TheClientIdIsSentAsAClientIdAuthorizationHeader()
    {
        // Not a Bearer token: anonymous imgur uploads authenticate with "Client-ID <id>", and getting
        // the scheme wrong is a 403 that looks like a dead key.
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"success":true,"data":{"id":"a","link":"l","deletehash":"d"}}""");

        await Client(handler, "my-key").UploadAsync(WriteImage());

        Assert.Equal("Client-ID my-key", handler.Authorization);
    }

    [Fact]
    public async Task TheImageGoesToTheImageEndpointAsMultipart()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"success":true,"data":{"id":"a","link":"l","deletehash":"d"}}""");

        await Client(handler).UploadAsync(WriteImage());

        Assert.EndsWith("/image", handler.Request?.RequestUri?.AbsolutePath ?? string.Empty, StringComparison.Ordinal);
        Assert.StartsWith(
            "multipart/form-data",
            handler.Request?.Content?.Headers.ContentType?.MediaType ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessFalseEnvelopeIsAFailureNotAnEmptyLink()
    {
        /*
         * THE CASE THAT MATTERS. Imgur can answer 200 with success:false -- a rejected image, a rate
         * limit -- and trusting the status code would hand back link:"" and report success. The flag
         * is what's checked.
         */
        var handler = new StubHandler(HttpStatusCode.OK, """{"success":false,"status":400,"data":{"error":"Bad image"}}""");

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Client(handler).UploadAsync(WriteImage()));

        Assert.Contains("failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnHtmlErrorPageIsReportedNotCrashed()
    {
        // A proxy or captive portal answering with HTML. JsonNode.Parse throws on it; the client turns
        // that into a readable failure rather than letting it escape mid-upload.
        var handler = new StubHandler(HttpStatusCode.OK, "<html>nope</html>");

        await Assert.ThrowsAsync<LauncherException>(() => Client(handler).UploadAsync(WriteImage()));
    }

    [Fact]
    public async Task AnAlbumIsBuiltFromDeleteHashesAndGivesAPageLink()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"success":true,"data":{"id":"ALB","deletehash":"adh"}}""");

        var link = await Client(handler).CreateAlbumAsync(["h1", "h2"]);

        // The shareable page, not the API URL.
        Assert.Equal("https://imgur.com/a/ALB", link);

        // The body carries the delete hashes upstream's way: comma-joined, form-encoded.
        Assert.Contains("deletehashes=h1,h2", handler.RequestBody, StringComparison.Ordinal);
        Assert.EndsWith("/album", handler.Request?.RequestUri?.AbsolutePath ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoClientIdNothingIsUploaded()
    {
        // The shipping default. The client refuses before making a request that would only 401.
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        var client = Client(handler, clientId: string.Empty);

        Assert.False(client.IsAvailable);
        await Assert.ThrowsAsync<LauncherException>(() => client.UploadAsync(WriteImage()));
        Assert.Null(handler.Request); // never sent
    }

    [Fact]
    public async Task AMissingFileIsReportedBeforeAnyRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<LauncherException>(
            () => Client(handler).UploadAsync(Path.Combine(_temp, "not-there.png")));

        Assert.Null(handler.Request);
    }
}
