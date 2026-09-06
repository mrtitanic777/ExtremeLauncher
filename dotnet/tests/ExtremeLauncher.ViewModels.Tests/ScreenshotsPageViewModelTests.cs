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
 * A screenshot is not a world, but it is not a mod either: it is the only copy of a moment somebody
 * wanted to keep. So the tests that matter are the same two -- that "no" means no, and that a rename
 * cannot land on top of another file.
 */

using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ScreenshotsPageViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-shot-" + Guid.NewGuid().ToString("N"));

    private readonly string _folder;

    public ScreenshotsPageViewModelTests()
    {
        _folder = Path.Combine(_temp, "screenshots");
        Directory.CreateDirectory(_folder);
    }

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

    private string Make(string name, DateTime? written = null)
    {
        var path = Path.Combine(_folder, name);

        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G']);

        if (written is { } when)
        {
            File.SetLastWriteTimeUtc(path, when);
        }

        return path;
    }

    private sealed class ScriptedPrompts(bool confirm = false, string? text = null) : IUserPrompts
    {
        public int Confirmations { get; private set; }

        public string LastMessage { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false)
        {
            Confirmations++;
            LastMessage = message;

            return Task.FromResult(confirm);
        }

        public Task<string?> PromptForTextAsync(string title, string message, string initialValue)
            => Task.FromResult(text);
    }

    private ScreenshotsPageViewModel Load(IUserPrompts? prompts = null)
    {
        var page = new ScreenshotsPageViewModel(prompts);

        page.Load(_temp);

        return page;
    }

    // ================================================================== what it shows

    /// <summary>Names are shown without the extension, which is what upstream shows and edits.</summary>
    [Fact]
    public void ScreenshotsAreListedWithoutTheirExtension()
    {
        Make("2024-01-01_12.00.00.png");

        Assert.Equal("2024-01-01_12.00.00", Assert.Single(Load().Screenshots).Name);
    }

    /*
     * PNG ONLY, which is upstream's filter and the game's own output format. Showing other files would
     * invite renaming one, and the rename appends ".png" to whatever is typed.
     */
    [Fact]
    public void OnlyPngsAreListed()
    {
        Make("shot.png");
        Make("notes.txt");
        Make("shot.jpg");

        Assert.Equal(["shot"], Load().Screenshots.Select(s => s.Name));
    }

    /// <summary>Newest first: someone opening this page has usually just taken one.</summary>
    [Fact]
    public void NewestScreenshotsComeFirst()
    {
        Make("old.png", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Make("new.png", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["new", "old"], Load().Screenshots.Select(s => s.Name));
    }

    [Fact]
    public void AnInstanceWithNoScreenshotsFolderListsNothing()
    {
        Directory.Delete(_folder);

        var page = Load();

        Assert.Empty(page.Screenshots);
        Assert.True(page.IsEmpty);
    }

    // ================================================================== deleting

    [Fact]
    public async Task DecliningLeavesTheScreenshotOnDisk()
    {
        var path = Make("shot.png");

        var page = Load(new ScriptedPrompts(confirm: false));

        page.Select(page.Screenshots[0].Path);
        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task WithNoPromptsWiredUpNothingIsDeleted()
    {
        var path = Make("shot.png");

        var page = Load();

        page.Select(page.Screenshots[0].Path);
        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AgreeingDeletesIt()
    {
        var doomed = Make("shot.png");
        Make("keep.png");

        var page = Load(new ScriptedPrompts(confirm: true));

        page.Select(page.Screenshots.Single(s => s.Name == "shot").Path);
        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.False(File.Exists(doomed));
        Assert.Equal(["keep"], page.Screenshots.Select(s => s.Name));
    }

    [Fact]
    public async Task DeletingWithNothingSelectedAsksNothing()
    {
        Make("shot.png");

        var prompts = new ScriptedPrompts(confirm: true);
        var page = Load(prompts);

        await page.DeleteSelectedAsync().ConfigureAwait(true);

        Assert.Equal(0, prompts.Confirmations);
        Assert.Single(page.Screenshots);
    }

    // ================================================================== renaming

    [Fact]
    public async Task RenamingKeepsTheExtension()
    {
        Make("shot.png");

        var page = Load(new ScriptedPrompts(text: "The good one"));

        page.Select(page.Screenshots[0].Path);
        await page.RenameSelectedAsync().ConfigureAwait(true);

        Assert.True(File.Exists(Path.Combine(_folder, "The good one.png")));
        Assert.Equal("The good one", Assert.Single(page.Screenshots).Name);
    }

    /*
     * A RENAME MUST NOT LAND ON TOP OF ANOTHER FILE. The other one is also the only copy of something,
     * and File.Move would overwrite it without a word.
     */
    [Fact]
    public async Task RenamingOntoAnExistingNameIsRefused()
    {
        Make("shot.png");
        Make("taken.png");

        var page = Load(new ScriptedPrompts(text: "taken"));

        page.Select(page.Screenshots.Single(s => s.Name == "shot").Path);
        await page.RenameSelectedAsync().ConfigureAwait(true);

        // Both are still there, with their own contents.
        Assert.True(File.Exists(Path.Combine(_folder, "shot.png")));
        Assert.True(File.Exists(Path.Combine(_folder, "taken.png")));
        Assert.Contains("already", page.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellingARenameChangesNothing()
    {
        Make("shot.png");

        var page = Load(new ScriptedPrompts(text: null));

        page.Select(page.Screenshots[0].Path);
        await page.RenameSelectedAsync().ConfigureAwait(true);

        Assert.Equal("shot", Assert.Single(page.Screenshots).Name);
    }

    [Fact]
    public async Task AnEmptyNameIsRefused()
    {
        Make("shot.png");

        var page = Load(new ScriptedPrompts(text: "  "));

        page.Select(page.Screenshots[0].Path);
        await page.RenameSelectedAsync().ConfigureAwait(true);

        Assert.Equal("shot", Assert.Single(page.Screenshots).Name);
        Assert.Contains("name", page.Status, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== the page contract

    [Fact]
    public void ThePageNeverHasAnythingToSave()
    {
        Make("shot.png");

        var page = Load();

        Assert.False(page.HasUnsavedChanges);
        Assert.True(page.Save());
    }

    // ================================================================== copy path and view folder

    private sealed class StubClipboard(bool present = true) : IClipboard
    {
        public string? LastCopied { get; private set; }

        public Task<bool> SetTextAsync(string text)
        {
            if (!present)
            {
                return Task.FromResult(false);
            }

            LastCopied = text;

            return Task.FromResult(true);
        }
    }

    private sealed class StubFolderOpener : IFolderOpener
    {
        public string? Opened { get; private set; }

        public Task OpenAsync(string path)
        {
            Opened = path;

            return Task.CompletedTask;
        }

        public Task OpenKnownAsync(string kind) => Task.CompletedTask;
    }

    [Fact]
    public void CopyPathNeedsASelection()
    {
        Make("shot.png");

        var page = Load();

        Assert.False(page.CanCopyPath);

        page.Select(page.Screenshots.Single().Path);
        Assert.True(page.CanCopyPath);
    }

    [Fact]
    public async Task CopyingThePathPutsTheFilePathOnTheClipboard()
    {
        var path = Make("shot.png");

        var clipboard = new StubClipboard();
        var page = new ScreenshotsPageViewModel(clipboard: clipboard);
        page.Load(_temp);
        page.Select(page.Screenshots.Single().Path);

        await page.CopyPathSelectedCommand.ExecuteAsync(null);

        Assert.Equal(page.Screenshots.Single().Path, clipboard.LastCopied);

        // The full file path, not the display name -- it ends in .png even though the row hides that.
        Assert.EndsWith(".png", clipboard.LastCopied!, StringComparison.Ordinal);
        Assert.Contains("Copied the path", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoClipboardCopyPathReportsItPlainly()
    {
        Make("shot.png");

        var page = new ScreenshotsPageViewModel(clipboard: new StubClipboard(present: false));
        page.Load(_temp);
        page.Select(page.Screenshots.Single().Path);

        await page.CopyPathSelectedCommand.ExecuteAsync(null);

        Assert.Contains("no clipboard", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewFolderIsDisabledWithoutAnOpener()
    {
        Assert.False(Load().CanOpenFolder);
    }

    [Fact]
    public async Task ViewFolderOpensTheScreenshotsFolder()
    {
        // Remove the folder to prove it is created before opening.
        Directory.Delete(_folder);

        var opener = new StubFolderOpener();
        var page = new ScreenshotsPageViewModel(folders: opener);
        page.Load(_temp);

        await page.OpenFolderCommand.ExecuteAsync(null);

        Assert.EndsWith("screenshots", opener.Opened!.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.True(Directory.Exists(page.FolderPath));
    }
}
