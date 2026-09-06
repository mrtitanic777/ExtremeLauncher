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
 * The window-shaped half of exporting a mod list.
 */

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ExtremeLauncher.Core;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppModListExporter(Func<Window?> owner, LauncherLog? log = null) : IModListExporter
{
    public async Task ExportAsync(string instanceName, IReadOnlyList<ModListEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var window = new ModListExportWindow(
            new ModListExportViewModel(mods, instanceName, new Target(owner, log)));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }

    private sealed class Target(Func<Window?> owner, LauncherLog? log) : IModListExportTarget
    {
        public Task<bool> CopyAsync(string text) => new AppClipboard(owner).SetTextAsync(text);

        public async Task<string> SaveAsync(string text, string suggestedFileName)
        {
            if (owner() is not { StorageProvider: { } storage })
            {
                return string.Empty;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save mod list",
                SuggestedFileName = suggestedFileName,

                // Taken from the suggested name rather than the format, so the two cannot disagree.
                DefaultExtension = Path.GetExtension(suggestedFileName).TrimStart('.'),
            }).ConfigureAwait(true);

            if (file?.TryGetLocalPath() is not { Length: > 0 } path)
            {
                return string.Empty;
            }

            try
            {
                await File.WriteAllTextAsync(path, text).ConfigureAwait(true);

                log?.Info($"Wrote a mod list to {path}");

                return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Reported through the dialog's status line rather than thrown: the user is standing
                // in front of it and the text is still on screen to try again with.
                log?.Warning($"Could not write the mod list: {e.Message}");

                return string.Empty;
            }
        }
    }
}
