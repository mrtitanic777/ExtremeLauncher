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
 * Choosing how to write out a mod list.
 *
 * The rendering itself is tested in ExportToModListTests. What is tested here is the live preview,
 * which is the reason the dialog exists at all: five formats and four optional fields is twenty
 * combinations, and nobody can picture "markdown without authors" without seeing it.
 */

using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class ModListExportViewModelTests
{
    private static readonly ModListEntry[] Mods =
    [
        new("Sodium", "https://modrinth.com/mod/sodium", "0.5.13", ["jellysquid3"], "sodium.jar"),
        new("Lithium", "https://modrinth.com/mod/lithium", "0.11.2", ["jellysquid3"], "lithium.jar"),
    ];

    private sealed class StubTarget(bool clipboardWorks = true) : IModListExportTarget
    {
        public string? Copied { get; private set; }

        public string? Saved { get; private set; }

        public string SuggestedSeen { get; private set; } = string.Empty;

        public string SavePath { get; set; } = "C:/out/mods.md";

        public Task<bool> CopyAsync(string text)
        {
            Copied = text;

            return Task.FromResult(clipboardWorks);
        }

        public Task<string> SaveAsync(string text, string suggestedFileName)
        {
            Saved = text;
            SuggestedSeen = suggestedFileName;

            return Task.FromResult(SavePath);
        }
    }

    private static ModListExportViewModel New(IModListExportTarget? target = null)
        => new(Mods, "My Instance", target ?? new StubTarget());

    [Fact]
    public void ThePreviewIsReadyBeforeAnythingIsTouched()
    {
        // Opening a preview pane that is blank until you change something is a wasted step.
        var vm = New();

        Assert.NotEqual(string.Empty, vm.Preview);
        Assert.Contains("Sodium", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownIsTheDefault()
    {
        /*
         * DIVERGES FROM UPSTREAM, which defaults to HTML. HTML is the format whose output is least
         * readable in a preview pane, and the places people paste a mod list -- issue trackers,
         * wikis, chat -- take markdown.
         */
        var vm = New();

        Assert.Equal(ModListFormat.Markdown, vm.Format);
        Assert.StartsWith("- [Sodium]", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheFormatRerendersImmediately()
    {
        var vm = New();

        vm.Format = ModListFormat.PlainText;

        Assert.DoesNotContain("- [", vm.Preview, StringComparison.Ordinal);
        Assert.StartsWith("Sodium (https://", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void UntickingAFieldRerendersImmediately()
    {
        // The whole reason for a live preview: the effect of a checkbox is not guessable.
        var vm = New();

        vm.IncludeAuthors = false;

        Assert.DoesNotContain("jellysquid3", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileNameFieldIsOffToBeginWith()
    {
        // It is the one field about your disk rather than about the mod.
        var vm = New();

        vm.Format = ModListFormat.PlainText;

        Assert.False(vm.IncludeFileName);
        Assert.DoesNotContain("sodium.jar", vm.Preview, StringComparison.Ordinal);

        vm.IncludeFileName = true;

        Assert.Contains("sodium.jar", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileNameIsMarkdownEscapedLikeEverythingElse()
    {
        /*
         * My first version of the test above asserted "sodium.jar" appeared in the MARKDOWN preview
         * and failed -- because a dot is one of the eighteen characters upstream escapes, so it
         * renders as "sodium\.jar". The escaping is right and the assertion was naive; this pins the
         * real behaviour rather than working around it.
         */
        var vm = New();

        vm.IncludeFileName = true;

        Assert.Contains(@"sodium\.jar", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomFormatUsesTheTypedTemplate()
    {
        var vm = New();

        vm.Format = ModListFormat.Custom;
        vm.CustomTemplate = "{name} -- {version}";

        Assert.Contains("Sodium -- 0.5.13", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFieldCheckboxesAreHiddenForACustomTemplate()
    {
        // They mean nothing when the user writes the line themselves.
        var vm = New();

        Assert.True(vm.ShowsFieldOptions);

        vm.Format = ModListFormat.Custom;

        Assert.False(vm.ShowsFieldOptions);
        Assert.True(vm.IsCustom);
    }

    [Fact]
    public void TheSuggestedFileNameFollowsTheFormat()
    {
        var vm = New();

        Assert.Equal("My Instance mods.md", vm.SuggestedFileName);

        vm.Format = ModListFormat.Csv;

        Assert.Equal("My Instance mods.csv", vm.SuggestedFileName);
    }

    [Fact]
    public void AnInstanceNameThatIsNotAValidFileNameIsMadeIntoOne()
    {
        // "1.20.1 / Fabric" is an ordinary instance name and not an acceptable file name.
        var vm = new ModListExportViewModel(Mods, "1.20.1 / Fabric", new StubTarget());

        Assert.DoesNotContain('/', vm.SuggestedFileName);
    }

    [Fact]
    public async Task CopyingPutsThePreviewOnTheClipboard()
    {
        var target = new StubTarget();

        var vm = New(target);

        await vm.CopyAsync();

        Assert.Equal(vm.Preview, target.Copied);
        Assert.Contains("Copied", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClipboardThatIsNotThereIsReportedRatherThanIgnored()
    {
        // A copy button that does nothing visible leaves somebody pressing it repeatedly.
        var vm = New(new StubTarget(clipboardWorks: false));

        await vm.CopyAsync();

        Assert.Contains("Could not", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingSaysWhereItWent()
    {
        // The next thing somebody does is go and find it.
        var target = new StubTarget { SavePath = "C:/notes/pack.md" };

        var vm = New(target);

        await vm.SaveAsync();

        Assert.Equal(vm.Preview, target.Saved);
        Assert.Equal("My Instance mods.md", target.SuggestedSeen);
        Assert.Contains("C:/notes/pack.md", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheSaveDialogSaysNothing()
    {
        var vm = New(new StubTarget { SavePath = string.Empty });

        await vm.SaveAsync();

        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public void AnEmptyModsFolderOffersNothingToExport()
    {
        var vm = new ModListExportViewModel([], "Empty", new StubTarget());

        Assert.Equal(0, vm.ModCount);
        Assert.False(vm.CanExport);
    }

    [Fact]
    public void WithNoTargetNothingCanBeExported()
    {
        var vm = new ModListExportViewModel(Mods, "My Instance");

        Assert.False(vm.CanExport);

        // The preview still renders: looking at the list is useful even where nothing can be done
        // with it.
        Assert.NotEqual(string.Empty, vm.Preview);
    }

    [Fact]
    public void EveryFormatIsOffered()
    {
        var vm = New();

        Assert.Equal(6, vm.Formats.Count);
        Assert.Contains(ModListFormat.Custom, vm.Formats);
    }

    [Fact]
    public void TheExampleLineFollowsTheFormat()
    {
        // So the choice is made by reading rather than by trying each one.
        var vm = New();

        vm.Format = ModListFormat.Csv;

        Assert.Equal(ExportToModList.ExampleLine(ModListFormat.Csv), vm.ExampleLine);
    }
}
