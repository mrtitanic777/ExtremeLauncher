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
 * Ported from ui/pages/instance/NotesPage.cpp, which is twenty lines: read notes() into a text box on
 * open, write it back on apply().
 *
 * Small, and the reason it is here first alongside Version is that it is the whole save path in
 * miniature -- a page that edits instance.cfg, tracks whether it is dirty, and can fail to write. It
 * proves the container works with something whose correctness is obvious, next to something whose
 * correctness is not.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

public sealed partial class NotesPageViewModel : ObservableObject, IInstancePage
{
    private InstanceSettings? _settings;

    private string _saved = string.Empty;

    public string Title => "Notes";

    /// <summary>What the user has typed.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Reads an instance's notes.</summary>
    public void Load(InstanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _saved = settings.Notes;

        Text = _saved;

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /*
     * COMPARED AGAINST WHAT WAS LOADED, rather than a flag set on every keystroke. Typing a character
     * and deleting it again leaves nothing to save, and a dirty flag would still prompt on close --
     * which trains people to dismiss that prompt without reading it.
     */
    public bool HasUnsavedChanges => !string.Equals(Text, _saved, StringComparison.Ordinal);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasUnsavedChanges));

    public bool Save()
    {
        if (_settings is null)
        {
            return !HasUnsavedChanges;
        }

        try
        {
            _settings.Notes = Text;
        }
        catch (IOException)
        {
            // A read-only instance folder, most often. The window refuses to close on this.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        _saved = Text;

        OnPropertyChanged(nameof(HasUnsavedChanges));

        return true;
    }
}
