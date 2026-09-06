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
 * Ported in behaviour from launcher/ui/dialogs/ExportToModListDialog.cpp.
 *
 * WRITING DOWN WHAT IS INSTALLED. Not a modpack -- a list, for a forum post, a README, or the friend
 * asking what you are running. The rendering is ExportToModList; this is the choosing.
 *
 * THE PREVIEW IS THE POINT. Five formats and four optional fields is twenty combinations, and nobody
 * can picture what "markdown without authors" looks like. Upstream shows the result live and so does
 * this: every change re-renders, so the choice is made by looking rather than guessing.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.ModPlatform;

namespace ExtremeLauncher.ViewModels;

/// <summary>Saving or copying the rendered list. Implemented by the app.</summary>
public interface IModListExportTarget
{
    /// <summary>Puts the text on the clipboard. False when there is no clipboard.</summary>
    Task<bool> CopyAsync(string text);

    /// <summary>Asks where to save and writes it. Returns the path, or empty when cancelled.</summary>
    Task<string> SaveAsync(string text, string suggestedFileName);
}

public sealed partial class ModListExportViewModel : ObservableObject
{
    private readonly IReadOnlyList<ModListEntry> _mods;

    private readonly IModListExportTarget? _target;

    public ModListExportViewModel(
        IEnumerable<ModListEntry> mods,
        string instanceName = "",
        IModListExportTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(mods);

        _mods = mods.ToArray();
        _target = target;

        InstanceName = instanceName;

        foreach (var format in new[]
                 {
                     ModListFormat.Markdown,
                     ModListFormat.Html,
                     ModListFormat.PlainText,
                     ModListFormat.Json,
                     ModListFormat.Csv,
                     ModListFormat.Custom,
                 })
        {
            Formats.Add(format);
        }

        Render();
    }

    public string InstanceName { get; }

    public ObservableCollection<ModListFormat> Formats { get; } = [];

    /// <summary>
    /// Markdown first, because it is what a forum post or a README wants.
    /// </summary>
    /// <remarks>
    /// Upstream defaults to HTML. Diverged deliberately: HTML is the format whose output is least
    /// readable in the preview pane, and the places people actually paste a mod list -- issue
    /// trackers, wikis, chat -- take markdown.
    /// </remarks>
    [ObservableProperty]
    private ModListFormat _format = ModListFormat.Markdown;

    [ObservableProperty]
    private bool _includeAuthors = true;

    [ObservableProperty]
    private bool _includeUrl = true;

    [ObservableProperty]
    private bool _includeVersion = true;

    /// <summary>Off by default: it is the one field about your disk rather than about the mod.</summary>
    [ObservableProperty]
    private bool _includeFileName;

    /// <summary>The template used when the format is Custom.</summary>
    [ObservableProperty]
    private string _customTemplate = "{name} {version}";

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    public int ModCount => _mods.Count;

    public bool IsCustom => Format == ModListFormat.Custom;

    /// <summary>The optional-field boxes mean nothing when the user writes the line themselves.</summary>
    public bool ShowsFieldOptions => !IsCustom;

    public bool CanExport => _target is not null && _mods.Count != 0;

    /// <summary>What the example line looks like, so the format choice is not a guess.</summary>
    public string ExampleLine => ExportToModList.ExampleLine(Format);

    public string SuggestedFileName
    {
        get
        {
            var stem = InstanceName.Length != 0 ? InstanceName : "mods";

            foreach (var bad in Path.GetInvalidFileNameChars())
            {
                stem = stem.Replace(bad, '-');
            }

            return stem + " mods" + Format switch
            {
                ModListFormat.Html => ".html",
                ModListFormat.Markdown => ".md",
                ModListFormat.Json => ".json",
                ModListFormat.Csv => ".csv",
                _ => ".txt",
            };
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task CopyAsync()
    {
        if (_target is null)
        {
            return;
        }

        // Said out loud either way: a copy button that does nothing visible leaves somebody pressing
        // it repeatedly.
        Status = await _target.CopyAsync(Preview).ConfigureAwait(true)
            ? "Copied to the clipboard."
            : "Could not reach the clipboard.";
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task SaveAsync()
    {
        if (_target is null)
        {
            return;
        }

        var path = await _target.SaveAsync(Preview, SuggestedFileName).ConfigureAwait(true);

        if (path.Length != 0)
        {
            // Where it went, not just that it went: the next thing somebody does is go and find it.
            Status = $"Saved to {path}";
        }
    }

    private void Render()
    {
        Preview = IsCustom
            ? ExportToModList.Render(_mods, CustomTemplate)
            : ExportToModList.Render(_mods, Format, Fields());

        OnPropertyChanged(nameof(ExampleLine));
        OnPropertyChanged(nameof(SuggestedFileName));
    }

    private ModListFields Fields()
    {
        var fields = ModListFields.None;

        if (IncludeAuthors)
        {
            fields |= ModListFields.Authors;
        }

        if (IncludeUrl)
        {
            fields |= ModListFields.Url;
        }

        if (IncludeVersion)
        {
            fields |= ModListFields.Version;
        }

        if (IncludeFileName)
        {
            fields |= ModListFields.FileName;
        }

        return fields;
    }

    partial void OnFormatChanged(ModListFormat value)
    {
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(ShowsFieldOptions));

        Render();
    }

    partial void OnIncludeAuthorsChanged(bool value) => Render();

    partial void OnIncludeUrlChanged(bool value) => Render();

    partial void OnIncludeVersionChanged(bool value) => Render();

    partial void OnIncludeFileNameChanged(bool value) => Render();

    partial void OnCustomTemplateChanged(string value) => Render();
}
