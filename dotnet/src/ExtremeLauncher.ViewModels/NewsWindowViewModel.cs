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
 * Ported from launcher/ui/dialogs/NewsDialog.cpp.
 *
 * THE NEWS WINDOW: a list of articles on the left, the selected one on the right, and a button that
 * collapses the list so one article gets the whole window.
 *
 * ONE DIVERGENCE, deliberate. Upstream keeps its entries in a QMap KEYED BY TITLE and looks the
 * selected article up by the text of the list row -- so two posts with the same title collide, one of
 * them becomes unreachable, and clicking the second shows the first. Here the selection IS the entry,
 * which cannot go wrong that way. Titles repeat more often than you would think: "Hotfix" twice in a
 * year is enough.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ViewModels;

public sealed partial class NewsWindowViewModel : ObservableObject
{
    private readonly ILinkOpener? _links;

    public NewsWindowViewModel(
        IReadOnlyList<NewsEntry> entries,
        bool startWithListHidden = false,
        ILinkOpener? links = null)
    {
        _links = links;
        _isListVisible = !startWithListHidden;

        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        // Upstream selects the first row in its constructor, so the window never opens blank.
        Selected = Entries.FirstOrDefault();
    }

    public ObservableCollection<NewsEntry> Entries { get; } = [];

    [ObservableProperty]
    private NewsEntry? _selected;

    [ObservableProperty]
    private bool _isListVisible = true;

    /// <summary>The selected article, laid out.</summary>
    public ObservableCollection<NewsBlock> Blocks { get; } = [];

    public string Title => Selected?.Title ?? string.Empty;

    /// <summary>Where the "read the full post" link goes.</summary>
    public string Link => Selected?.BestLink ?? string.Empty;

    public bool CanOpenLink => _links is not null && Link.Length != 0;

    public string ToggleListLabel => IsListVisible ? "Hide article list" : "Show article list";

    /// <summary>Whether the list is worth showing at all.</summary>
    /// <remarks>
    /// With one article the list is a column containing one row, taking a third of the window to say
    /// what the heading already says. Upstream shows it regardless.
    /// </remarks>
    public bool CanToggleList => Entries.Count > 1;

    [RelayCommand]
    public void ToggleList() => IsListVisible = !IsListVisible;

    [RelayCommand]
    public async Task OpenLinkAsync()
    {
        // The URL comes from the feed -- off the network -- so the opener's scheme check is doing
        // real work here, unlike with the BuildConfig links where it is only a belt-and-braces one.
        if (_links is not null && Link.Length != 0)
        {
            await _links.OpenAsync(Link).ConfigureAwait(true);
        }
    }

    partial void OnSelectedChanged(NewsEntry? value)
    {
        Blocks.Clear();

        foreach (var block in NewsHtml.ToBlocks(value?.Content))
        {
            Blocks.Add(block);
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Link));
        OnPropertyChanged(nameof(CanOpenLink));
    }

    partial void OnIsListVisibleChanged(bool value) => OnPropertyChanged(nameof(ToggleListLabel));
}
