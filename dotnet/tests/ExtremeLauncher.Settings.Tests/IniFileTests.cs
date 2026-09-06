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
 * Ported from tests/INIFile_test.cpp.
 *
 * The tests that embed literal file content are real inherited ground truth for the READER -- that
 * content is what QSettings actually wrote. The upstream test that compares against a live QSettings
 * instance cannot be ported directly (no Qt here), so its values are reproduced as literal fixtures
 * instead, and flagged in PORTING.md as needing a byte-for-byte diff against a real Qt build.
 */

using Xunit;

namespace ExtremeLauncher.Settings.Tests;

public sealed class IniFileTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-ini-" + Guid.NewGuid().ToString("N"));

    public IniFileTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string At(string name) => Path.Combine(_temp, name);

    // ================================================================== round trips

    [Fact]
    public void SaveLoadPreservesAwkwardStrings()
    {
        const string a = "a";
        const string b = "a\nb\t\n\\\\\\C:\\Program files\\terrible\\name\\of something\\#thisIsNotAComment";

        var path = At("test_SaveLoad.ini");

        var f = new IniFile();
        f.Set("a", a);
        f.Set("b", b);
        Assert.True(f.SaveFile(path));

        var f2 = new IniFile();
        Assert.True(f2.LoadFile(path));

        Assert.Equal(a, f2.GetString("a", "NOT SET"));
        Assert.Equal(b, f2.GetString("b", "NOT SET"));
    }

    [Fact]
    public void SaveLoadPreservesLists()
    {
        var strings = new List<string> { "a", "b", "c" };
        var numbers = new List<string> { "1", "2", "3", "10" };

        var path = At("test_SaveLoadLists.ini");

        var f = new IniFile();
        f.Set("list_strings", strings);
        f.Set("list_numbers", numbers);
        Assert.True(f.SaveFile(path));

        var f2 = new IniFile();
        Assert.True(f2.LoadFile(path));

        Assert.Equal(strings, Assert.IsType<List<string>>(f2.Get("list_strings")));
        Assert.Equal(numbers, Assert.IsType<List<string>>(f2.Get("list_numbers")));
    }

    [Theory]
    [InlineData("/abc/def/ghi/jkl")]
    [InlineData(@"C:\Program files\terrible\name\of something\")]
    [InlineData("Lorem ipsum dolor sit amet.")]
    [InlineData("Lorem\n\t\n\\n\\tAAZ\nipsum dolor\n\nsit amet.")]
    [InlineData("\"\n\n\"")]
    [InlineData("some data#something")]
    public void EscapeRoundTrips(string through)
    {
        // The upstream test_Escape_data cases, exercised through a real save/load cycle.
        var path = At($"escape-{through.GetHashCode(StringComparison.Ordinal)}.ini");

        var f = new IniFile();
        f.Set("value", through);
        Assert.True(f.SaveFile(path));

        var f2 = new IniFile();
        Assert.True(f2.LoadFile(path));

        Assert.Equal(through, f2.GetString("value", "NOT SET"));
    }

    // ================================================================== inherited ground truth

    [Fact]
    public void ReadsAnExistingUnversionedFileAndUpgradesIt()
    {
        // Literal content from upstream test_SaveAlreadyExistingFile -- real QSettings output.
        const string content = """
            InstanceType=OneSix
            iconKey=vanillia_icon
            name=Minecraft Vanillia
            OverrideCommands=true
            PreLaunchCommand="$INST_JAVA" -jar packwiz-installer-bootstrap.jar link
            Wrapperommand="\"$INST_JAVA\" -jar packwiz-installer-bootstrap.jar link ="
            """;

        var path = At("existing.ini");
        File.WriteAllText(path, content);

        var f1 = new IniFile();
        Assert.True(f1.LoadFile(path));

        Assert.Equal(
            "\"$INST_JAVA\" -jar packwiz-installer-bootstrap.jar link",
            f1.GetString("PreLaunchCommand", "NOT SET"));

        Assert.Equal(
            "\"$INST_JAVA\" -jar packwiz-installer-bootstrap.jar link =",
            f1.GetString("Wrapperommand", "NOT SET"));

        // Saving upgrades it, and the values must survive the upgrade.
        Assert.True(f1.SaveFile(path));

        var f2 = new IniFile();
        Assert.True(f2.LoadFile(path));

        Assert.Equal(
            "\"$INST_JAVA\" -jar packwiz-installer-bootstrap.jar link",
            f2.GetString("PreLaunchCommand", "NOT SET"));

        Assert.Equal(
            "\"$INST_JAVA\" -jar packwiz-installer-bootstrap.jar link =",
            f2.GetString("Wrapperommand", "NOT SET"));

        Assert.Equal(IniFile.CurrentConfigVersion, f2.GetString(IniFile.ConfigVersionKey, "NOT SET"));
    }

    [Fact]
    public void MigratesAConfigVersionOnePointOneFile()
    {
        // Literal content from upstream test_SaveAlreadyExistingFileWithSpecialCharsV1. The 1.1 format
        // double-quoted values, so one layer has to come off.
        const string content = """
            InstanceType=OneSix
            ConfigVersion=1.1
            iconKey=vanillia_icon
            name=Minecraft Vanillia
            OverrideCommands=true
            PreLaunchCommand="\"env mesa=true\""
            """;

        var path = At("v1.ini");
        File.WriteAllText(path, content);

        var f = new IniFile();
        Assert.True(f.LoadFile(path));

        Assert.Equal("env mesa=true", f.GetString("PreLaunchCommand", "NOT SET"));
        Assert.Equal(IniFile.CurrentConfigVersion, f.GetString(IniFile.ConfigVersionKey, "NOT SET"));
    }

    [Fact]
    public void ReadsQSettingsQuotingOfSpecialCharacters()
    {
        // Values from upstream test_SaveAlreadyExistingFileWithSpecialChars, written the way QSettings
        // writes them. Reproduced as a literal because there is no Qt here to generate it live.
        const string content = """
            simple=value1
            withQuotes="\"value2\" with quotes"
            withSpecialCharacters="env mesa=true"
            withSpecialCharacters2="1;2;3;4"
            withAll="val=\"$INST_JAVA\" -jar; ls "
            ConfigVersion=1.2
            """;

        var path = At("special.ini");
        File.WriteAllText(path, content);

        var f1 = new IniFile();
        Assert.True(f1.LoadFile(path));

        Assert.Equal("value1", f1.GetString("simple", "NOT SET"));
        Assert.Equal("\"value2\" with quotes", f1.GetString("withQuotes", "NOT SET"));
        Assert.Equal("env mesa=true", f1.GetString("withSpecialCharacters", "NOT SET"));
        Assert.Equal("1;2;3;4", f1.GetString("withSpecialCharacters2", "NOT SET"));
        Assert.Equal("val=\"$INST_JAVA\" -jar; ls ", f1.GetString("withAll", "NOT SET"));

        // ...and survive our own write cycle.
        Assert.True(f1.SaveFile(path));

        var f2 = new IniFile();
        Assert.True(f2.LoadFile(path));

        Assert.Equal("value1", f2.GetString("simple", "NOT SET"));
        Assert.Equal("\"value2\" with quotes", f2.GetString("withQuotes", "NOT SET"));
        Assert.Equal("env mesa=true", f2.GetString("withSpecialCharacters", "NOT SET"));
        Assert.Equal("1;2;3;4", f2.GetString("withSpecialCharacters2", "NOT SET"));
        Assert.Equal("val=\"$INST_JAVA\" -jar; ls ", f2.GetString("withAll", "NOT SET"));
    }

    // ================================================================== dialect specifics

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has=equals", "\"has=equals\"")]
    [InlineData("has;semicolon", "\"has;semicolon\"")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData(" leading", "\" leading\"")]
    [InlineData("trailing ", "\"trailing \"")]
    // Not wrapped: Qt's "starts with a quote" check inspects the *escaped* buffer, where the leading
    // '"' has already become '\"', so the condition never fires. Escaping alone is sufficient.
    [InlineData("\"already quoted\"", "\\\"already quoted\\\"")]
    [InlineData("line\nbreak", "line\\nbreak")]
    [InlineData("tab\there", "tab\\there")]
    [InlineData("back\\slash", "back\\\\slash")]
    public void QuotesExactlyWhenQtWould(string input, string expected)
        => Assert.Equal(expected, IniFile.EscapeValue(input));

    [Fact]
    public void ReadsQuotedAndUnquotedFormsIdentically()
    {
        // Whether QSettings wraps a quote-containing value in an outer pair is the one part of the
        // dialect reconstructed from Qt's source rather than verified against real output, so the
        // reader is deliberately tolerant of both spellings.
        Assert.Equal("\"value2\" with quotes", IniFile.UnescapeValue("\\\"value2\\\" with quotes"));
        Assert.Equal("\"value2\" with quotes", IniFile.UnescapeValue("\"\\\"value2\\\" with quotes\""));
    }

    [Fact]
    public void HashIsNotAValueDelimiter()
    {
        // ';' and '#' start whole-line comments only; inside a value a '#' is literal.
        Assert.Equal("some data#something", IniFile.EscapeValue("some data#something"));
        Assert.Equal("some data#something", IniFile.UnescapeValue("some data#something"));
    }

    [Fact]
    public void EscapesLowControlCharactersAsHex()
    {
        Assert.Equal("\\x1", IniFile.EscapeValue("\u0001"));

        // The digit after a hex escape gets escaped too, so "\x1" + "2" cannot be re-read as "\x12".
        Assert.Equal("\\x1\\x32", IniFile.EscapeValue("\u00012"));
        Assert.Equal("\u00012", IniFile.UnescapeValue("\\x1\\x32"));
    }

    [Fact]
    public void UnquotedCommasProduceAList()
    {
        Assert.Equal(new List<string> { "a", "b", "c" }, Assert.IsType<List<string>>(IniFile.UnescapeValue("a, b, c")));

        // Quoted commas stay inside the value.
        Assert.Equal("a, b, c", Assert.IsType<string>(IniFile.UnescapeValue("\"a, b, c\"")));
    }

    [Fact]
    public void WholeLineCommentsAreIgnored()
    {
        var f = new IniFile();

        Assert.True(f.LoadFromText("""
            ; a comment
            # another comment
            real=value
            ConfigVersion=1.2
            """));

        Assert.Equal("value", f.GetString("real", "NOT SET"));
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void SavingStampsTheCurrentConfigVersion()
    {
        var path = At("stamped.ini");

        var f = new IniFile();
        f.Set("key", "value");
        Assert.True(f.SaveFile(path));

        Assert.Contains($"{IniFile.ConfigVersionKey}={IniFile.CurrentConfigVersion}", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingKeysFallBackToTheDefault()
    {
        var f = new IniFile();

        Assert.Equal("NOT SET", f.GetString("absent", "NOT SET"));
        Assert.Null(f.Get("absent"));
        Assert.False(f.Contains("absent"));
    }

    [Fact]
    public void LegacyFormatUnescapesHashAndNewlines()
    {
        // The pre-QSettings format, with its own \n \t \# escapes and real '#' comments.
        var parsed = IniFile.ParseLegacyFormat("""
            name=hello\nworld
            escapedHash=a\#b
            withComment=value # this is a comment
            """);

        Assert.Equal("hello\nworld", parsed["name"]);
        Assert.Equal("a#b", parsed["escapedHash"]);
        Assert.Equal("value", parsed["withComment"]);
    }

    [Fact]
    public void AnEmptyLegacyFileIsNotACrash()
    {
        // REGRESSION: the comment scan started at index 1, and IndexOf(char, 1) on a zero-length
        // string throws ArgumentOutOfRangeException. Found by ModUtils.ReadForgeInfo, which hands
        // this an empty forgeversion.properties when a Forge jar ships one.
        Assert.Empty(IniFile.ParseLegacyFormat(string.Empty));
        Assert.Empty(IniFile.ParseLegacyFormat("\n\n\n"));

        var file = new IniFile();

        Assert.True(file.LoadFromBytes([]));
    }

    [Fact]
    public void AWholeLineCommentIsNotASetting()
    {
        // REGRESSION: the same off-by-one meant a '#' at position 0 was never found, so a comment
        // line containing '=' was parsed as a key.
        var parsed = IniFile.ParseLegacyFormat("#" + " this=is a comment\nreal=value");

        Assert.False(parsed.ContainsKey("# this"));
        Assert.Equal("value", parsed["real"]);
        Assert.Single(parsed);
    }
}
