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
 * These are MUTATING requests against a real account — an upload replaces the user's skin everywhere,
 * not just in this launcher — so nothing here is sent. The requests are built and inspected, which is
 * both the safe way to test them and the only way without a Mojang account.
 *
 * What matters is exactly what would go on the wire: the URL, the verb, the header, and the body.
 */

using System.Text;
using ExtremeLauncher.Minecraft.Auth;
using Xunit;

namespace ExtremeLauncher.Minecraft.Tests;

public sealed class SkinApiTests
{
    private const string Token = "eyJhbGciOi.fake.token";

    private static async Task<string> BodyOf(HttpRequestMessage request)
        => request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync().ConfigureAwait(true);

    // ================================================================== authorisation

    /// <summary>Every one of these calls needs the bearer token; none of them is anonymous.</summary>
    [Fact]
    public void EveryRequestCarriesTheBearerToken()
    {
        var requests = new[]
        {
            SkinApi.CreateUploadSkinRequest(Token, [1, 2, 3], SkinModel.Classic),
            SkinApi.CreateDeleteSkinRequest(Token),
            SkinApi.CreateChangeCapeRequest(Token, "cape-id"),
            SkinApi.CreateChangeCapeRequest(Token, string.Empty),
        };

        foreach (var request in requests)
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Token, request.Headers.Authorization.Parameter);
        }
    }

    // ================================================================== uploading a skin

    [Fact]
    public async Task UploadingASkinPostsAMultipartForm()
    {
        var request = SkinApi.CreateUploadSkinRequest(Token, Encoding.UTF8.GetBytes("PNG"), SkinModel.Slim);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.minecraftservices.com/minecraft/profile/skins", request.RequestUri!.ToString());

        var body = await BodyOf(request).ConfigureAwait(true);

        Assert.Contains("name=file", body, StringComparison.Ordinal);
        Assert.Contains("name=variant", body, StringComparison.Ordinal);
        Assert.Contains("image/png", body, StringComparison.Ordinal);
        Assert.Contains("PNG", body, StringComparison.Ordinal);
    }

    /*
     * The filename is always "skin.png" whatever the file is called on disk. Mojang requires one and
     * does not care which, so sending the user's own would leak a path fragment for no benefit.
     */
    [Fact]
    public async Task TheUploadedFilenameIsAlwaysSkinPng()
        => Assert.Contains(
            "filename=skin.png",
            await BodyOf(SkinApi.CreateUploadSkinRequest(Token, [1], SkinModel.Classic)).ConfigureAwait(true),
            StringComparison.Ordinal);

    /// <summary>UPPERCASE here, unlike the same two words in a profile response.</summary>
    [Theory]
    [InlineData(SkinModel.Classic, "CLASSIC")]
    [InlineData(SkinModel.Slim, "SLIM")]
    public async Task TheVariantIsSentInMojangsCasing(SkinModel model, string expected)
    {
        Assert.Equal(expected, SkinApi.ToVariant(model));

        var body = await BodyOf(SkinApi.CreateUploadSkinRequest(Token, [1], model)).ConfigureAwait(true);

        Assert.Contains(expected, body, StringComparison.Ordinal);
    }

    // ================================================================== removing a skin

    /*
     * Not "upload a blank skin": the default is derived from the account's UUID and no image
     * reproduces it, so deleting is the only way back.
     */
    [Fact]
    public void RemovingASkinDeletesTheActiveOne()
    {
        var request = SkinApi.CreateDeleteSkinRequest(Token);

        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("https://api.minecraftservices.com/minecraft/profile/skins/active", request.RequestUri!.ToString());
        Assert.Null(request.Content);
    }

    // ================================================================== capes

    [Fact]
    public async Task EquippingACapePutsItsId()
    {
        var request = SkinApi.CreateChangeCapeRequest(Token, "17912791-58d1-4b5a-b2b6-6cbcb5a2d0e0");

        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://api.minecraftservices.com/minecraft/profile/capes/active", request.RequestUri!.ToString());

        Assert.Equal(
            """{"capeId":"17912791-58d1-4b5a-b2b6-6cbcb5a2d0e0"}""",
            await BodyOf(request).ConfigureAwait(true));
    }

    /// <summary>Same endpoint, different verb — there is no "unset" body to get wrong.</summary>
    [Fact]
    public void RemovingACapeDeletesTheActiveOne()
    {
        var request = SkinApi.CreateChangeCapeRequest(Token, string.Empty);

        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("https://api.minecraftservices.com/minecraft/profile/capes/active", request.RequestUri!.ToString());
        Assert.Null(request.Content);
    }

    /*
     * Upstream interpolates the id straight into a JSON string literal, which is fine for the UUIDs
     * Mojang issues and wrong the moment anything else reaches it. Serialising properly costs nothing
     * and cannot produce a malformed body.
     */
    [Fact]
    public async Task ACapeIdWithAQuoteDoesNotBreakTheBody()
    {
        const string Awkward = "he\"llo";

        var body = await BodyOf(SkinApi.CreateChangeCapeRequest(Token, Awkward)).ConfigureAwait(true);

        /*
         * Asserted as a PROPERTY rather than an exact string: the body must parse and give the id
         * back. How System.Text.Json spells the escape is its business -- it writes " rather than
         * \" by default -- and pinning that would test the encoder instead of this code. Upstream's
         * interpolated version produces a body that does not parse at all.
         */
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(body);

        Assert.Equal(Awkward, parsed!["capeId"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheCapeBodyIsSentAsJson()
    {
        var request = SkinApi.CreateChangeCapeRequest(Token, "cape-id");

        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

        // And it really is parseable JSON, not a string that looks like some.
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(await BodyOf(request).ConfigureAwait(true));

        Assert.Equal("cape-id", parsed!["capeId"]!.GetValue<string>());
    }
}
