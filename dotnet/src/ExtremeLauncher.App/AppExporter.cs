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
 * Opening the export dialog and running the task it settles on.
 */

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppExporter(Func<Window?> owner, LauncherLog? log = null) : IInstanceExporter
{
    public async Task ExportAsync(InstanceRecord instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var window = new ExportWindow(
            new ExportViewModel(instance.Name, new Runner(owner, instance, log)));

        if (owner() is { } parent)
        {
            await window.ShowDialog(parent).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }

    private sealed class Runner(Func<Window?> owner, InstanceRecord instance, LauncherLog? log) : IExportRunner
    {
        public async Task<string> PickTargetAsync(ExportKind kind, string suggestedFileName)
        {
            if (owner() is not { StorageProvider: { } storage })
            {
                return string.Empty;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = kind == ExportKind.ModrinthPack ? "Save modpack" : "Save instance archive",
                SuggestedFileName = suggestedFileName,

                // Named so the desktop appends the right one if the user types a bare name.
                DefaultExtension = kind == ExportKind.ModrinthPack ? "mrpack" : "zip",
                FileTypeChoices =
                [
                    kind == ExportKind.ModrinthPack
                        ? new FilePickerFileType("Modrinth modpack") { Patterns = ["*.mrpack"] }
                        : new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] },
                ],
            }).ConfigureAwait(true);

            return file?.TryGetLocalPath() ?? string.Empty;
        }

        private static FilePickerFileType FileTypeFor(ExportKind kind) => kind switch
        {
            ExportKind.ModrinthPack => new FilePickerFileType("Modrinth modpack") { Patterns = ["*.mrpack"] },
            ExportKind.CurseForgePack => new FilePickerFileType("CurseForge modpack") { Patterns = ["*.zip"] },
            _ => new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] },
        };

        public async Task<string> RunAsync(
            ExportKind kind,
            string targetPath,
            string name,
            string version,
            string summary)
        {
            /*
             * Task.Run, not a bare await. Both tasks are file-bound and block -- zipping a 300 MB
             * instance is seconds of solid work -- and the one thread that must never block is the
             * one drawing the window.
             */
            if (kind.IsPack())
            {
                var pack = new PackExportTask(
                    instance.Paths,
                    targetPath,
                    name,
                    version.Length == 0 ? "1.0.0" : version,
                    summary,
                    format: kind == ExportKind.CurseForgePack
                        ? PackExportFormat.CurseForge
                        : PackExportFormat.Modrinth);

                if (!await Task.Run(() => pack.RunAsync(CancellationToken.None)).ConfigureAwait(true))
                {
                    log?.Warning($"Pack export failed: {pack.FailReason}");

                    return pack.FailReason.Length != 0 ? pack.FailReason : "The export failed.";
                }

                log?.Info($"Exported {instance.Name} to {targetPath}");

                return $"Exported to {Path.GetFileName(targetPath)} — "
                    + $"{pack.LinkedCount} linked mods, {pack.OverrideCount} bundled files, "
                    + $"{Describe(pack.ArchiveSize)}.";
            }

            var zip = new InstanceExportTask(instance.Paths.InstanceRoot, targetPath);

            if (!await Task.Run(() => zip.RunAsync(CancellationToken.None)).ConfigureAwait(true))
            {
                log?.Warning($"Instance export failed: {zip.FailReason}");

                return zip.FailReason.Length != 0 ? zip.FailReason : "The export failed.";
            }

            log?.Info($"Exported {instance.Name} to {targetPath}");

            return $"Exported to {Path.GetFileName(targetPath)} — "
                + $"{zip.FileCount} files, {Describe(zip.ArchiveSize)}.";
        }

        /// <summary>A size somebody can read, rather than a number of bytes.</summary>
        private static string Describe(long bytes) => bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
            >= 1024 => $"{bytes / 1024.0:0.0} KB",
            _ => $"{bytes} bytes",
        };
    }
}
