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
 * Ported from MainWindow::updateNewsLabel in launcher/ui/MainWindow.cpp, and the article half of
 * launcher/ui/dialogs/NewsDialog.cpp.
 *
 * THE NEWS BUTTON ON THE TOOLBAR. Three states, upstream's: loading, showing the newest headline, or
 * saying there is nothing. The button is only clickable in the middle one -- which is the whole
 * reason this is a view model rather than a label, because "enabled" here has to be ANNOUNCED when
 * the fetch finishes on a background thread, and a computed property nobody raises for is a control
 * that never comes back to life.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

/// <summary>Opens the news window.</summary>
public interface INewsUi
{
    /// <param name="startWithListHidden">
    /// True when the headline itself was clicked, which upstream opens with the article list
    /// collapsed -- clicking one story means "show me that story", not "show me the index".
    /// </param>
    Task ShowAsync(IReadOnlyList<NewsEntry> entries, bool startWithListHidden);
}

public sealed partial class NewsViewModel : ObservableObject
{
    private readonly NewsChecker? _checker;

    private readonly Action<Action> _post;

    /// <param name="post">
    /// Marshals back onto the UI thread. NewsChecker raises its event on whatever thread the fetch
    /// finished on, and raising PropertyChanged from there is a crash that only shows up when the
    /// network is slow -- which is to say, never on the machine it was written on.
    /// </param>
    public NewsViewModel(NewsChecker? checker = null, Action<Action>? post = null)
    {
        _checker = checker;
        _post = post ?? (action => action());

        if (_checker is not null)
        {
            _checker.NewsLoaded += (_, _) => _post(Refresh);
        }
    }

    public ObservableCollection<NewsEntry> Entries { get; } = [];

    /// <summary>True when this build has a feed at all, so the toolbar can leave the news off.</summary>
    public bool IsAvailable => _checker?.IsConfigured == true;

    [ObservableProperty]
    private string _label = "No news available.";

    [ObservableProperty]
    private string _tooltip = string.Empty;

    /// <summary>Upstream disables the button while loading and when there is nothing to show.</summary>
    public bool CanShow => Entries.Count != 0;

    /// <summary>Fetches the feed, if there is one. Never throws.</summary>
    public async Task LoadAsync()
    {
        if (_checker is null)
        {
            return;
        }

        /*
         * STARTED FIRST, THEN REFRESHED. The other order reads better and is wrong: IsLoading is not
         * true until ReloadAsync has been called, so refreshing first would show "No news available."
         * for the length of the fetch and only then switch to "Loading news..." -- backwards, and on
         * a slow connection visibly so.
         */
        var running = _checker.ReloadAsync();

        Refresh();

        await running.ConfigureAwait(true);

        Refresh();
    }

    private void Refresh()
    {
        if (_checker is null)
        {
            return;
        }

        Entries.Clear();

        foreach (var entry in _checker.Entries)
        {
            Entries.Add(entry);
        }

        if (_checker.IsLoading)
        {
            Label = "Loading news...";
            Tooltip = string.Empty;
        }
        else if (Entries.Count != 0)
        {
            // Upstream shows the newest headline and nothing else.
            Label = Entries[0].Title;

            /*
             * The error is kept on the TOOLTIP even when there is news, because a stale headline with
             * a failed refresh behind it looks exactly like a fresh one. Upstream drops the error
             * entirely once anything has loaded.
             */
            Tooltip = _checker.LastError.Length != 0
                ? "Showing the last news that loaded. " + _checker.LastError
                : string.Empty;
        }
        else
        {
            Label = "No news available.";

            /*
             * WHY there is no news, which upstream never says. Its updateNewsLabel ignores the error
             * string completely, so a feed that is down and a feed that is empty look identical --
             * and the one thing a user could do about the first (wait, or check their proxy) depends
             * on telling them apart.
             */
            Tooltip = _checker.LastError;
        }

        OnPropertyChanged(nameof(CanShow));
    }
}
