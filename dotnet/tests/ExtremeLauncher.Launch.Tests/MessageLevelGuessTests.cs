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
 * Guessing a log line's level from its own text -- ported from MinecraftInstance::guessLevel. This is
 * the parser that colours the game's plain output, so its regexes are exactly where a mistake hides.
 */

using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class MessageLevelGuessTests
{
    private static MessageLevel Guess(string line) => MessageLevels.Guess(line, MessageLevel.Message);

    [Theory]
    [InlineData("[12:34:56] [Render thread/INFO]: Setting user", MessageLevel.Message)]
    [InlineData("[12:34:56] [Render thread/WARN]: Missing sound", MessageLevel.Warning)]
    [InlineData("[12:34:56] [Server thread/ERROR]: Encountered", MessageLevel.Error)]
    [InlineData("[12:34:56] [main/FATAL]: Unreported exception", MessageLevel.Fatal)]
    [InlineData("[12:34:56] [main/DEBUG]: registered", MessageLevel.Debug)]
    [InlineData("[12:34:56] [main/TRACE]: deep detail", MessageLevel.Debug)]
    public void TheLog4jPrefixLevelIsRead(string line, MessageLevel expected)
        => Assert.Equal(expected, Guess(line));

    [Theory]
    [InlineData("2024-01-01 [INFO] Loading", MessageLevel.Message)]
    [InlineData("2024-01-01 [WARNING] deprecated", MessageLevel.Warning)]
    [InlineData("2024-01-01 [SEVERE] boom", MessageLevel.Error)]
    [InlineData("[STDERR] a native warning", MessageLevel.Error)]
    [InlineData("2024-01-01 [DEBUG] verbose", MessageLevel.Debug)]
    public void TheOldForgeStyleLevelsAreRead(string line, MessageLevel expected)
        => Assert.Equal(expected, Guess(line));

    [Fact]
    public void OverwritingExistingIsFatal()
        => Assert.Equal(MessageLevel.Fatal, Guess("overwriting existing item registration"));

    [Theory]
    [InlineData("Exception in thread \"main\" java.lang.NullPointerException")]
    [InlineData("\tat net.minecraft.client.Main.main(Main.java:12)")]
    [InlineData("Caused by: java.io.IOException: disk full")]
    [InlineData("java.lang.IllegalStateException: bad")]
    [InlineData("\t... 42 more")]
    public void JavaStackTraceLinesAreErrors(string line)
        => Assert.Equal(MessageLevel.Error, Guess(line));

    [Fact]
    public void APlainLineKeepsThePreviousLevel()
    {
        // A continuation line names no level, so it stays what the line before it was -- which is how a
        // multi-line message keeps one colour.
        Assert.Equal(MessageLevel.Warning, MessageLevels.Guess("    ...continued", MessageLevel.Warning));
        Assert.Equal(MessageLevel.Message, MessageLevels.Guess("just some text", MessageLevel.Message));
    }

    [Fact]
    public void AnExceptionOutranksThePrefixLevel()
    {
        // A line that both carries an INFO prefix AND matches an exception is an error: upstream checks
        // the stack-trace patterns last and lets them win.
        Assert.Equal(
            MessageLevel.Error,
            MessageLevels.Guess("[12:00:00] [main/INFO]: net.minecraft.CrashException: ouch", MessageLevel.Message));
    }
}
