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
 * Thin as it is, this is a trust boundary: what arrives is bytes from another process and the
 * receiver acts on it. So the tests are mostly about what a malformed message yields -- an empty
 * command, which does nothing, rather than a half-populated one that gets acted on anyway.
 */

using System.Text;
using Xunit;

namespace ExtremeLauncher.Core.Tests;

public sealed class ApplicationMessageTests
{
    private static ApplicationMessage Parse(string json)
        => ApplicationMessage.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void AMessageRoundTrips()
    {
        var message = new ApplicationMessage { Command = "launch" };
        message.Args["id"] = "1.20.1";
        message.Args["server"] = "example.invalid";

        var parsed = ApplicationMessage.Parse(message.Serialize());

        Assert.Equal("launch", parsed.Command);
        Assert.Equal("1.20.1", parsed.Args["id"]);
        Assert.Equal("example.invalid", parsed.Args["server"]);
    }

    [Fact]
    public void AMessageWithNoArgumentsRoundTrips()
    {
        var parsed = ApplicationMessage.Parse(new ApplicationMessage { Command = "activate" }.Serialize());

        Assert.Equal("activate", parsed.Command);
        Assert.Empty(parsed.Args);
    }

    /// <summary>An absent command yields an empty one, which does nothing.</summary>
    [Fact]
    public void AMessageWithNoCommandIsInert()
        => Assert.Equal(string.Empty, Parse("""{ "args": { "id": "x" } }""").Command);

    /*
     * A sender on a different version may include fields this one does not know, and refusing the
     * whole message over one would break the handover entirely. Upstream's toString() yields an empty
     * string for anything that is not a string, and that is kept.
     */
    [Theory]
    [InlineData("""{ "command": 42 }""")]
    [InlineData("""{ "command": ["launch"] }""")]
    [InlineData("""{ "command": null }""")]
    public void ACommandOfTheWrongTypeIsEmptyRatherThanFatal(string json)
        => Assert.Equal(string.Empty, Parse(json).Command);

    [Fact]
    public void AnArgumentOfTheWrongTypeBecomesEmptyRatherThanLosingTheMessage()
    {
        var parsed = Parse("""{ "command": "launch", "args": { "id": "x", "count": 3, "flag": true } }""");

        Assert.Equal("launch", parsed.Command);
        Assert.Equal("x", parsed.Args["id"]);
        Assert.Equal(string.Empty, parsed.Args["count"]);
        Assert.Equal(string.Empty, parsed.Args["flag"]);
    }

    /// <summary>Args of the wrong shape entirely is treated as no args, not as a failure.</summary>
    [Fact]
    public void AnArgsFieldThatIsNotAnObjectIsIgnored()
    {
        var parsed = Parse("""{ "command": "launch", "args": "nonsense" }""");

        Assert.Equal("launch", parsed.Command);
        Assert.Empty(parsed.Args);
    }

    /// <summary>Bytes that are not a JSON object at all cannot be interpreted, and are refused.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void SomethingThatIsNotAMessageIsRefused(string text)
        => Assert.ThrowsAny<LauncherException>(() => Parse(text));
}
