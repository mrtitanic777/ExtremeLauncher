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
 * Editing the game's own options.txt.
 *
 * ASSERTED ON THE FILE. options.txt is written by the game and read by the game; what this launcher
 * thinks it contains is not the question.
 */

using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class GameOptionsPageViewModelTests : IDisposable
{
    private readonly string _gameRoot = Path.Combine(
        Path.GetTempPath(),
        "el-gameopts-" + Guid.NewGuid().ToString("N"));

    public GameOptionsPageViewModelTests() => Directory.CreateDirectory(_gameRoot);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_gameRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string OptionsPath => Path.Combine(_gameRoot, "options.txt");

    private void WriteOptions(string contents) => File.WriteAllText(OptionsPath, contents);

    private const string Sample = """
        version:3465
        fov:0.5
        gamma:1.0
        renderDistance:12
        key_key.attack:key.mouse.left
        soundCategory_master:1.0
        """;

    [Fact]
    public void TheOptionsAreListed()
    {
        WriteOptions(Sample);

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);

        Assert.True(page.HasOptions);
        Assert.False(page.IsMissing);
        Assert.Contains(page.Options, o => o.Key == "fov" && o.Value == "0.5");
        Assert.Contains(page.Options, o => o.Key == "renderDistance");
    }

    [Fact]
    public void TheFormatVersionIsRead()
    {
        // Which the game writes, and which tells somebody whether these keys mean what they expect.
        WriteOptions(Sample);

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);

        Assert.Equal(3465, page.FormatVersion);
    }

    [Fact]
    public void AnInstanceThatHasNeverBeenLaunchedSaysSoRatherThanLookingBroken()
    {
        // The ordinary state of a new instance, not an error.
        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);

        Assert.True(page.IsMissing);
        Assert.False(page.HasOptions);
    }

    [Fact]
    public void EditingAValueWritesItToTheFile()
    {
        WriteOptions(Sample);

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);

        page.Options.Single(o => o.Key == "renderDistance").Value = "32";

        Assert.True(page.HasUnsavedChanges);
        Assert.True(page.Save());
        Assert.False(page.HasUnsavedChanges);

        // The FILE, which is what the game reads.
        var written = File.ReadAllText(OptionsPath);

        Assert.Contains("renderDistance:32", written, StringComparison.Ordinal);
        Assert.DoesNotContain("renderDistance:12", written, StringComparison.Ordinal);
    }

    [Fact]
    public void KeysTheLauncherDoesNotUnderstandSurviveASave()
    {
        /*
         * options.txt is full of keys this launcher has never heard of -- every mod that stores a
         * setting puts one here -- and a page that rewrote the file from its own model would delete
         * all of them. Only what changed is written back.
         */
        WriteOptions(Sample + "\nsomeModSetting:whatever\n");

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);
        page.Options.Single(o => o.Key == "fov").Value = "0.8";
        page.Save();

        Assert.Contains("someModSetting:whatever", File.ReadAllText(OptionsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyBindingIsShownAsItIsRatherThanInterpreted()
    {
        /*
         * Two hundred keys of wildly different kinds live here. Pretending to understand them is how a
         * launcher corrupts somebody's settings; showing them as they are is both honest and safe.
         */
        WriteOptions(Sample);

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);

        Assert.Equal("key.mouse.left", page.Options.Single(o => o.Key == "key_key.attack").Value);
    }

    [Fact]
    public void SearchingNarrowsTheList()
    {
        WriteOptions(Sample);

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);

        page.SearchText = "key_";

        Assert.Single(page.Options);
        Assert.Equal("key_key.attack", page.Options[0].Key);
    }

    [Fact]
    public void AnEditSurvivesBeingFilteredAway()
    {
        /*
         * The view is filtered, not the list -- the same objects go back in when the filter clears.
         * Losing an edit because you typed in the search box is a genuinely infuriating bug.
         */
        WriteOptions(Sample);

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);
        page.Options.Single(o => o.Key == "fov").Value = "0.9";

        page.SearchText = "renderDistance";

        Assert.DoesNotContain(page.Options, o => o.Key == "fov");
        Assert.True(page.HasUnsavedChanges);

        page.SearchText = string.Empty;

        Assert.Equal("0.9", page.Options.Single(o => o.Key == "fov").Value);
    }

    [Fact]
    public void ReloadingThrowsAwayUnsavedEdits()
    {
        WriteOptions(Sample);

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);
        page.Options.Single(o => o.Key == "fov").Value = "0.9";

        page.Reload();

        Assert.False(page.HasUnsavedChanges);
        Assert.Equal("0.5", page.Options.Single(o => o.Key == "fov").Value);
    }

    [Fact]
    public void SettingAValueBackToWhatItWasIsNotAChange()
    {
        WriteOptions(Sample);

        var page = new GameOptionsPageViewModel();

        page.Load(_gameRoot);

        var fov = page.Options.Single(o => o.Key == "fov");

        fov.Value = "0.9";

        Assert.True(page.HasUnsavedChanges);

        fov.Value = "0.5";

        Assert.False(page.HasUnsavedChanges);
    }
}
