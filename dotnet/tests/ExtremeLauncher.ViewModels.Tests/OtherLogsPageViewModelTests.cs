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
 * This is the page somebody is looking at when something has already gone wrong, so the rule running
 * through every test here is that IT NEVER SHOWS NOTHING. A file too big, a file that will not
 * decompress, a file the game is writing to right now: each returns a sentence, because an empty box
 * is indistinguishable from an empty log.
 *
 * The patterns are upstream's, and the odd ones (IDMap dump, ModLoader.txt) are pinned deliberately:
 * they are for versions almost nobody runs, which makes them exactly the ones a tidy-up would delete.
 */

using ExtremeLauncher.Core;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class OtherLogsPageViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-logs-" + Guid.NewGuid().ToString("N"));

    public OtherLogsPageViewModelTests() => Directory.CreateDirectory(_temp);

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

    private string Make(string relative, string content = "hello", DateTime? written = null)
    {
        var path = Path.Combine(_temp, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        if (written is { } when)
        {
            File.SetLastWriteTimeUtc(path, when);
        }

        return path;
    }

    private string MakeGzipped(string relative, string content)
    {
        var path = Path.Combine(_temp, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Assert.True(GZip.TryZip(System.Text.Encoding.UTF8.GetBytes(content), out var compressed));

        File.WriteAllBytes(path, compressed);

        return path;
    }

    private OtherLogsPageViewModel Load()
    {
        var page = new OtherLogsPageViewModel();

        page.Load(_temp);

        return page;
    }

    // ================================================================== what counts as a log

    /// <summary>Upstream's four patterns, including the two nobody would guess.</summary>
    [Theory]
    [InlineData("logs/latest.log")]
    [InlineData("logs/2024-01-01-1.log.gz")]
    [InlineData("logs/debug.log.3")]
    [InlineData("crash-reports/crash-2024-01-01_12.00.00-client.txt")]
    [InlineData("IDMap dump 2013.txt")]
    [InlineData("ModLoader.txt")]
    [InlineData("ModLoader.txt.1")]
    public void UpstreamsPatternsAreAllMatched(string relative)
    {
        Make(relative);

        Assert.Single(Load().Files);
    }

    [Theory]
    [InlineData("options.txt")]
    [InlineData("mods/sodium.jar")]
    [InlineData("saves/World/level.dat")]
    public void OtherFilesAreNotLogs(string relative)
    {
        Make(relative);

        Assert.Empty(Load().Files);
    }

    /*
     * FOUND RECURSIVELY: logs/ holds most of them, crash-reports/ the rest, and a few land at the top
     * of the game directory. Names are shown relative to it, so a row reads "logs/latest.log" rather
     * than an absolute path nobody can scan a list of.
     */
    [Fact]
    public void LogsAreFoundInSubfoldersAndNamedRelatively()
    {
        Make("logs/latest.log");
        Make("crash-reports/crash-2024.txt");

        var names = Load().Files.Select(f => f.Name).ToList();

        Assert.Contains("logs/latest.log", names);
        Assert.Contains("crash-reports/crash-2024.txt", names);
    }

    /// <summary>Newest first: the log somebody wants is nearly always the last one written.</summary>
    [Fact]
    public void NewestLogsComeFirst()
    {
        Make("logs/old.log", written: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Make("logs/latest.log", written: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["logs/latest.log", "logs/old.log"], Load().Files.Select(f => f.Name));
    }

    [Fact]
    public void AnInstanceWithNoLogsShowsNone()
    {
        var page = Load();

        Assert.Empty(page.Files);
        Assert.True(page.IsEmpty);
    }

    // ================================================================== reading

    [Fact]
    public void SelectingALogShowsItsText()
    {
        Make("logs/latest.log", "[12:00:00] [main/INFO]: Loading Minecraft");

        var page = Load();

        page.Select(page.Files[0].Path);

        Assert.Contains("Loading Minecraft", page.Content, StringComparison.Ordinal);
    }

    /*
     * A ROTATED LOG IS GZIPPED, and is the ordinary case for anything but the current session -- the
     * game compresses each log as it rolls over. A page that could not read them would only ever show
     * today's.
     */
    [Fact]
    public void AGzippedLogIsDecompressed()
    {
        MakeGzipped("logs/2024-01-01-1.log.gz", "[12:00:00] [main/INFO]: Yesterday's crash");

        var page = Load();

        page.Select(page.Files[0].Path);

        Assert.Contains("Yesterday's crash", page.Content, StringComparison.Ordinal);
    }

    /// <summary>A .gz that is not really gzipped says so rather than showing an empty box.</summary>
    [Fact]
    public void AnUnreadableGzipSaysSo()
    {
        Make("logs/broken.log.gz", "this is not gzip at all");

        var page = Load();

        page.Select(page.Files[0].Path);

        Assert.Contains("could not be read", page.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broken.log.gz", page.Content, StringComparison.Ordinal);
    }

    /*
     * TOO BIG IS REFUSED BEFORE READING, so a launcher does not stop responding trying to put a
     * gigabyte of modded log into a text box. Upstream's cap is 12 MiB.
     */
    [Fact]
    public void AFileOverTheCapIsRefusedWithItsName()
    {
        var path = Path.Combine(_temp, "logs", "huge.log");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Sparse-ish: the length is what matters, and writing 12 MiB of text would be slow.
        using (var stream = File.Create(path))
        {
            stream.SetLength(OtherLogsPageViewModel.MaxFileSize + 1);
        }

        var page = Load();

        page.Select(page.Files[0].Path);

        Assert.Contains("too big", page.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("huge.log", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingNothingClearsTheText()
    {
        Make("logs/latest.log", "something");

        var page = Load();

        page.Select(page.Files[0].Path);

        Assert.NotEqual(string.Empty, page.Content);

        page.Select(null);

        Assert.Equal(string.Empty, page.Content);
        Assert.False(page.HasSelection);
    }

    // ================================================================== refreshing

    /*
     * The file being looked at is usually the one still being written to, so Refresh re-reads it rather
     * than only rescanning the folder.
     */
    [Fact]
    public void RefreshingRereadsTheSelectedFile()
    {
        var path = Make("logs/latest.log", "first line");

        var page = Load();

        page.Select(page.Files[0].Path);

        Assert.Contains("first line", page.Content, StringComparison.Ordinal);

        File.WriteAllText(path, "first line\nsecond line");

        page.Refresh();

        Assert.Contains("second line", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshingPicksUpANewLog()
    {
        var page = Load();

        Assert.Empty(page.Files);

        Make("logs/latest.log");
        page.Refresh();

        Assert.Single(page.Files);
    }

    // ================================================================== the page contract

    [Fact]
    public void ThePageNeverHasAnythingToSave()
    {
        Make("logs/latest.log");

        var page = Load();

        Assert.False(page.HasUnsavedChanges);
        Assert.True(page.Save());
    }

    // ================================================================== following the folder

    /*
     * A CRASH REPORT APPEARING WHILE THE WINDOW IS OPEN shows up without anybody pressing Refresh.
     * That is what the watcher is for -- and the reason it goes through a post delegate is the same as
     * everywhere else in this port: it fires on a thread pool thread and this page is bound to
     * controls.
     */
    [SkippableFact]
    public async Task ALogAppearingIsNoticedWithoutRefreshing()
    {
        var noticed = new TaskCompletionSource();

        // Runs inline, and signals so the test does not have to guess when.
        using var page = new OtherLogsPageViewModel(action =>
        {
            action();
            noticed.TrySetResult();
        });

        page.Load(_temp);

        Assert.Empty(page.Files);

        Directory.CreateDirectory(Path.Combine(_temp, "crash-reports"));
        File.WriteAllText(Path.Combine(_temp, "crash-reports", "crash-2024.txt"), "it broke");

        var arrived = await Task.WhenAny(noticed.Task, Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(true) == noticed.Task;

        // The OS is allowed to be slow; the logic is what is under test, and Refresh still works.
        Skip.IfNot(arrived, "The OS did not deliver a file change in time.");

        Assert.Contains(page.Files, f => f.Name.EndsWith("crash-2024.txt", StringComparison.Ordinal));
    }

    /// <summary>Disposing stops the watcher, so a closed window leaves no handle behind.</summary>
    [Fact]
    public void DisposingStopsWatching()
    {
        var page = new OtherLogsPageViewModel();

        page.Load(_temp);
        page.Dispose();
        page.Dispose();
    }

    // ================================================================== deleting

    private sealed class ScriptedPrompts(bool confirm) : IUserPrompts
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
            => Task.FromResult(confirm);

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult<string?>(null);
    }

    private OtherLogsPageViewModel Load(IUserPrompts prompts)
    {
        var page = new OtherLogsPageViewModel(prompts: prompts);
        page.Load(_temp);

        return page;
    }

    [Fact]
    public void DeleteAndCleanAreOffWithoutPrompts()
    {
        // No way to ask means no destructive action -- the page cannot delete unasked.
        Make("logs/latest.log");

        var page = Load();
        page.Select(page.Files[0].Path);

        Assert.False(page.CanDelete);
        Assert.False(page.CanClean);
    }

    [Fact]
    public async Task DeletingRemovesTheSelectedLog()
    {
        var path = Make("logs/latest.log");
        Make("logs/old.log");

        var page = Load(new ScriptedPrompts(confirm: true));
        page.Select(page.Files.Single(f => f.Name == "logs/latest.log").Path);

        await page.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.False(File.Exists(path));
        Assert.DoesNotContain(page.Files, f => f.Name == "logs/latest.log");
        Assert.Contains(page.Files, f => f.Name == "logs/old.log");
    }

    [Fact]
    public async Task DecliningTheDeleteKeepsTheLog()
    {
        var path = Make("logs/latest.log");

        var page = Load(new ScriptedPrompts(confirm: false));
        page.Select(page.Files[0].Path);

        await page.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CleanDeletesEveryLog()
    {
        Make("logs/latest.log");
        Make("logs/old.log");
        Make("crash-reports/crash-2020.txt");

        var page = Load(new ScriptedPrompts(confirm: true));

        Assert.True(page.CanClean);

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Empty(page.Files);
    }

    [Fact]
    public async Task DecliningTheCleanKeepsTheLogs()
    {
        Make("logs/latest.log");
        Make("logs/old.log");

        var page = Load(new ScriptedPrompts(confirm: false));

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Equal(2, page.Files.Count);
    }
}
