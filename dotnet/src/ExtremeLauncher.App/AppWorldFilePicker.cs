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
 * The desktop half of the worlds page's Add button: asks for a world zip and hands back its path.
 */

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

/// <summary>Asks the desktop for a Minecraft world zip to import.</summary>
public sealed class AppWorldFilePicker(Func<Window?> owner) : IWorldFilePicker
{
    public async Task<string> PickAsync()
    {
        if (owner() is not { StorageProvider: { } storage })
        {
            return string.Empty;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a world",
            AllowMultiple = false,

            // .mcworld is a zip under another name -- Bedrock exports and some tools write it -- so it
            // is offered alongside .zip. Both go through the same importer.
            FileTypeFilter =
            [
                new FilePickerFileType("Minecraft world") { Patterns = ["*.zip", "*.mcworld"] },
            ],
        }).ConfigureAwait(true);

        return files.Count == 0 ? string.Empty : files[0].TryGetLocalPath() ?? string.Empty;
    }
}
