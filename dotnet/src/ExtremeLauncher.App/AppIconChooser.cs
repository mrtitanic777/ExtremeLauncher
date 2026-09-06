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
 * The window-shaped half of choosing an icon, kept out of the view models so those stay testable.
 */

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppIconChooser(Func<Window?> owner, IconList icons) : IIconChooser
{
    public async Task<string> ChooseAsync(string currentKey)
    {
        var model = new IconPickerViewModel(icons, currentKey, new FilePicker(owner));
        var window = new IconPickerWindow(model);

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }

        return window.ChosenKey;
    }

    /// <summary>Asks the desktop for an image file.</summary>
    private sealed class FilePicker(Func<Window?> owner) : IIconFilePicker
    {
        public async Task<string> PickAsync()
        {
            if (owner() is not { StorageProvider: { } storage })
            {
                return string.Empty;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose an image",
                AllowMultiple = false,

                /*
                 * The filter lists SVG too, which this build cannot draw. Deliberate: the list comes
                 * from IconUtils, which is upstream's set, and an icons folder shared with an upstream
                 * install has them. One that is picked is imported and listed as unrenderable rather
                 * than silently refused, which is the honest outcome.
                 */
                FileTypeFilter =
                [
                    new FilePickerFileType("Images")
                    {
                        Patterns = [.. IconUtils.FilterPatterns],
                    },
                ],
            }).ConfigureAwait(true);

            return files.Count == 0 ? string.Empty : files[0].TryGetLocalPath() ?? string.Empty;
        }
    }
}
