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
 * Ported from ui/pages/instance/ServersPage.cpp.
 *
 * THE MULTIPLAYER LIST, now editable: add, remove, reorder and change a server, which is what
 * upstream offers and what this page did not have until servers.dat could be written.
 *
 * TWO RULES MAKE THIS SAFE, and neither is optional:
 *
 *   LOCKED WHILE THE GAME IS RUNNING. servers.dat is a file THE GAME OWNS: it reads it at startup and
 *   REWRITES IT WHOLE on exit. An edit made while the game is up is not merged -- it is overwritten
 *   the moment the player quits, silently. Upstream locks the model for exactly this reason
 *   (m_locked, set from runningStateChanged), and so does this.
 *
 *   NEVER WRITE A LIST THAT WAS NOT READ. ServerList.Load returns an empty list for a file that will
 *   not parse, which is right for showing a page and catastrophic for saving one: it would turn "I
 *   could not read your 40 servers" into "you now have none". So the page remembers whether the file
 *   was loaded and refuses to save when it was not -- upstream's m_loaded check, which its
 *   scheduleSave() logs about and which is the single most important line in that file.
 */

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.ViewModels;

/// <summary>One of the three answers to "does this server's resource pack get accepted".</summary>
public sealed record TextureChoice(AcceptsTextures Value, string Label);

/// <summary>One row: a server in the instance's multiplayer list.</summary>
public sealed partial class ServerViewModel : ObservableObject
{
    /// <remarks>
    /// THREE, not two, and the order is the useful one: "ask each time" is the normal state and the
    /// one somebody would want to go back to. Writing the other two into servers.dat answers, on the
    /// player's behalf, a question the game would otherwise put to them.
    /// </remarks>
    private static readonly TextureChoice[] AllTextureChoices =
    [
        new(AcceptsTextures.Prompt, "Ask each time"),
        new(AcceptsTextures.Always, "Always accept"),
        new(AcceptsTextures.Never, "Never accept"),
    ];

    /// <summary>The three choices, as an instance property so a row can bind to them.</summary>
    public IReadOnlyList<TextureChoice> TextureChoices => AllTextureChoices;

    /// <summary>The chosen option, for a picker. Kept in step with <see cref="AcceptsTextures"/>.</summary>
    public TextureChoice SelectedTextureChoice
    {
        get => AllTextureChoices.First(c => c.Value == AcceptsTextures);
        set => AcceptsTextures = value?.Value ?? AcceptsTextures.Prompt;
    }

    [ObservableProperty]
    private string _name = "Minecraft Server";

    [ObservableProperty]
    private string _address = string.Empty;

    /// <summary>Whether the server's resource pack is accepted, refused, or asked about.</summary>
    [ObservableProperty]
    private AcceptsTextures _acceptsTextures = AcceptsTextures.Prompt;

    /// <summary>The server's icon, kept so that editing a row does not throw it away.</summary>
    /// <remarks>
    /// Not shown and not editable -- the game writes it after it has connected once. It is carried
    /// through the view model purely so a save does not silently drop every icon in the list.
    /// </remarks>
    public byte[] Icon { get; init; } = [];

    /// <summary>What the row says about resource packs, or empty when the game will ask.</summary>
    /// <remarks>
    /// Nothing is shown for "ask me", which is the normal state -- a row saying "will ask" on every
    /// server is noise that hides the two that are actually configured.
    /// </remarks>
    public string TexturesDescription => AcceptsTextures switch
    {
        AcceptsTextures.Always => "accepts resource packs",
        AcceptsTextures.Never => "refuses resource packs",
        _ => string.Empty,
    };

    public bool HasTexturesDescription => TexturesDescription.Length != 0;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnAcceptsTexturesChanged(AcceptsTextures value)
    {
        OnPropertyChanged(nameof(TexturesDescription));
        OnPropertyChanged(nameof(HasTexturesDescription));
        OnPropertyChanged(nameof(SelectedTextureChoice));
    }

    internal MinecraftServer ToRecord() => new()
    {
        Name = Name,
        Address = Address,
        Icon = Icon,
        AcceptsTextures = AcceptsTextures,
    };
}

public sealed partial class ServersPageViewModel : ObservableObject, IInstancePage, ILocksWhileRunning
{
    private readonly IUserPrompts? _prompts;

    private string _gameRoot = string.Empty;

    /// <summary>Whether the file was actually read. Nothing may be saved until it was.</summary>
    private bool _loaded;

    private bool _suppressDirty;

    private readonly IServerJoiner? _joiner;

    public ServersPageViewModel(IUserPrompts? prompts = null, IServerJoiner? joiner = null)
    {
        _prompts = prompts;
        _joiner = joiner;
    }

    public string Title => "Servers";

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>
    /// True while the game is running, when nothing here may be changed.
    /// </summary>
    /// <remarks>
    /// Set by the instance window from its launch coordinator, the way upstream's runningStateChanged
    /// sets m_locked.
    /// </remarks>
    [ObservableProperty]
    private bool _isLocked;

    /// <summary>Why the page is refusing to be edited, or empty.</summary>
    public string LockReason
    {
        get
        {
            if (IsLocked)
            {
                return "The game is running. It rewrites this file when it exits, so changes made now "
                    + "would be lost.";
            }

            if (_gameRoot.Length != 0 && !_loaded)
            {
                /*
                 * The dangerous state, and the reason it is stated rather than merely handled: the
                 * list on screen is empty because the file could not be read, NOT because there are
                 * no servers. Saving it would replace whatever is really in there with nothing.
                 */
                return "This instance's server list could not be read, so it is not being shown and "
                    + "cannot be edited. Saving now would replace it.";
            }

            return string.Empty;
        }
    }

    public bool IsEditable => !IsLocked && _loaded;

    public ObservableCollection<ServerViewModel> Servers { get; } = [];

    /// <summary>Reads the multiplayer list out of an instance's game directory.</summary>
    public void Load(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);

        _gameRoot = gameRoot;

        Rebuild();
    }

    public ServerViewModel? Selected => Servers.FirstOrDefault(s => s.IsSelected);

    [RelayCommand]
    public void Select(string? address)
    {
        var match = false;

        foreach (var server in Servers)
        {
            /*
             * FIRST MATCH ONLY. Two rows can hold the same address -- an empty one, most obviously,
             * since Add makes one and a second Add makes another -- and selecting both would leave
             * Selected returning one row while the other is highlighted.
             */
            server.IsSelected = !match && address is not null && server.Address == address;

            match |= server.IsSelected;
        }

        RaiseSelectionDependent();
    }

    /// <summary>Selects a row directly, which is what the list control does.</summary>
    public void Select(ServerViewModel? server)
    {
        foreach (var candidate in Servers)
        {
            candidate.IsSelected = ReferenceEquals(candidate, server);
        }

        RaiseSelectionDependent();
    }

    public bool HasSelection => Selected is not null;

    public bool CanAdd => IsEditable;

    public bool CanRemove => IsEditable && HasSelection;

    public bool CanMoveUp => IsEditable && Selected is { } selected && Servers.IndexOf(selected) > 0;

    public bool CanMoveDown => IsEditable
        && Selected is { } selected
        && Servers.IndexOf(selected) is var index
        && index >= 0
        && index < Servers.Count - 1;

    /// <summary>Whether the selected server can be joined.</summary>
    /// <remarks>
    /// Gated like editing -- not while the game runs, and only once the list is read -- because joining
    /// starts a launch, and a second one over a running instance is exactly what the lock prevents. A
    /// row with a blank address is caught when Join runs rather than here, since the address is edited
    /// in place and this does not re-evaluate on every keystroke.
    /// </remarks>
    public bool CanJoin => _joiner is not null && IsEditable && HasSelection;

    /// <summary>
    /// Whether the list is empty because there are no servers.
    /// </summary>
    /// <remarks>
    /// The page cannot tell "no servers" from "servers.dat would not parse" — ServerList returns an
    /// empty list for both, which is upstream's behaviour. LockReason is what separates them on
    /// screen: an unreadable file says so and refuses to be edited.
    /// </remarks>
    public bool IsEmpty => Servers.Count == 0;

    /// <summary>Adds an empty row after the selection and selects it.</summary>
    /// <remarks>
    /// Upstream's addEmptyRow, including WHERE it goes: after the current row rather than at the end,
    /// so somebody grouping servers can put one next to its neighbours.
    /// </remarks>
    [RelayCommand]
    public void Add()
    {
        if (!CanAdd)
        {
            return;
        }

        var at = Selected is { } selected ? Servers.IndexOf(selected) + 1 : Servers.Count;

        var added = new ServerViewModel();

        Track(added);

        Servers.Insert(at, added);

        Select(added);

        MarkDirty();
    }

    /// <summary>Removes the selected server, after asking.</summary>
    [RelayCommand]
    public async Task RemoveAsync()
    {
        if (!CanRemove || Selected is not { } selected)
        {
            return;
        }

        /*
         * ASKED FIRST, as upstream does, and its wording is right about why: this is permanent, and
         * an address nobody wrote down anywhere else is gone for good. Nothing else in an instance is
         * as unrecoverable -- a world can be restored, a mod re-downloaded.
         */
        if (_prompts is not null)
        {
            var name = selected.Name.Length != 0 ? selected.Name : selected.Address;

            var confirmed = await _prompts.ConfirmAsync(
                "Remove this server?",
                $"\"{name}\" will be removed from this instance's multiplayer list. There is no undo, "
                + "and the address is not recorded anywhere else.",
                "Remove",
                destructive: true).ConfigureAwait(true);

            if (!confirmed)
            {
                return;
            }
        }

        var index = Servers.IndexOf(selected);

        Servers.Remove(selected);

        // The row that took its place, so a run of removals does not need a click between each.
        Select(Servers.Count == 0 ? null : Servers[Math.Min(index, Servers.Count - 1)]);

        MarkDirty();
    }

    [RelayCommand]
    public void MoveUp()
    {
        if (!CanMoveUp || Selected is not { } selected)
        {
            return;
        }

        var index = Servers.IndexOf(selected);

        Servers.Move(index, index - 1);

        RaiseSelectionDependent();
        MarkDirty();
    }

    [RelayCommand]
    public void MoveDown()
    {
        if (!CanMoveDown || Selected is not { } selected)
        {
            return;
        }

        var index = Servers.IndexOf(selected);

        Servers.Move(index, index + 1);

        RaiseSelectionDependent();
        MarkDirty();
    }

    /// <summary>Launches the instance straight into the selected server.</summary>
    /// <remarks>
    /// Upstream's actionJoin. The launch runs through the same coordinator as the window's own Launch
    /// button, so the instance window locks its pages and jumps to the log the same way. A row with no
    /// address is ignored -- an empty server is a normal launch, and joining nothing is not what the
    /// button says it does.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanJoin))]
    public async Task JoinSelectedAsync()
    {
        if (_joiner is null || Selected is not { } selected || selected.Address.Trim().Length == 0)
        {
            return;
        }

        await _joiner.JoinAsync(selected.Address.Trim()).ConfigureAwait(true);
    }

    /// <summary>Writes the list back to servers.dat.</summary>
    public bool Save()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        if (!IsEditable)
        {
            /*
             * REFUSED, NOT SILENTLY SKIPPED. The window treats false as a failure and says so, which
             * is what should happen: the alternative is a page that looks saved and is not, and the
             * two cases that get here -- a running game and an unreadable file -- are precisely the
             * two where writing would destroy something.
             */
            return false;
        }

        try
        {
            ServerList.Save(_gameRoot, Servers.Select(s => s.ToRecord()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        HasUnsavedChanges = false;

        return true;
    }

    /// <summary>Re-reads servers.dat, which the game rewrites whenever it exits.</summary>
    [RelayCommand]
    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        var selected = Selected?.Address;

        _suppressDirty = true;

        Servers.Clear();

        _loaded = false;

        if (_gameRoot.Length != 0)
        {
            /*
             * TryLoad, not Load, because the difference matters here in a way it did not when this
             * page was read-only: an unreadable file and an empty one look identical to Load, and
             * only one of them may be written back over.
             */
            if (ServerList.TryLoad(_gameRoot, out var servers))
            {
                _loaded = true;

                // In the game's own order: this is the order the multiplayer screen shows, and someone
                // matching the two lists up is the reason to look here at all.
                foreach (var server in servers)
                {
                    var row = new ServerViewModel
                    {
                        Name = server.Name,
                        Address = server.Address,
                        Icon = server.Icon,
                        AcceptsTextures = server.AcceptsTextures,
                    };

                    Track(row);

                    Servers.Add(row);
                }
            }
        }

        _suppressDirty = false;

        HasUnsavedChanges = false;

        if (selected is not null)
        {
            Select(selected);
        }

        RaiseSelectionDependent();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(LockReason));
    }

    /// <summary>Watches a row so that typing in it marks the page dirty.</summary>
    private void Track(ServerViewModel server)
        => server.PropertyChanged += (_, e) =>
        {
            // The selection is not an edit: clicking through the list must not make the page dirty.
            if (e.PropertyName == nameof(ServerViewModel.IsSelected))
            {
                return;
            }

            MarkDirty();
        };

    private void MarkDirty()
    {
        if (_suppressDirty)
        {
            return;
        }

        HasUnsavedChanges = true;

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RaiseSelectionDependent()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        OnPropertyChanged(nameof(CanJoin));

        RemoveCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        JoinSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(LockReason));
        OnPropertyChanged(nameof(CanAdd));

        RaiseSelectionDependent();
    }
}
