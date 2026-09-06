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
 * The app's half of uploading screenshots: one image gives its link, several give an album link.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Net;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppScreenshotUploader(HttpClient client, LauncherLog? log = null) : IScreenshotUploader
{
    private readonly ImgurClient _imgur =
        new(client, BuildConfig.Instance.ImgurClientId, BuildConfig.Instance.ImgurBaseUrl);

    public string Destination => _imgur.IsAvailable ? _imgur.Host : string.Empty;

    public async Task<ScreenshotUploadResult> UploadAsync(IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        if (filePaths.Count == 0)
        {
            return ScreenshotUploadResult.Failure("Nothing to upload.");
        }

        try
        {
            var uploaded = new List<ImgurImage>(filePaths.Count);

            foreach (var path in filePaths)
            {
                uploaded.Add(await _imgur.UploadAsync(path).ConfigureAwait(false));
            }

            // One image: its own link. Several: bundle into an album and hand back one link to all.
            var link = uploaded.Count == 1
                ? uploaded[0].Link
                : await _imgur.CreateAlbumAsync([.. uploaded.Select(u => u.DeleteHash)]).ConfigureAwait(false);

            log?.Info($"Uploaded {uploaded.Count} screenshot(s) to imgur: {link}");

            return ScreenshotUploadResult.Success(link);
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or TaskCanceledException)
        {
            log?.Error($"Screenshot upload failed: {e.Message}");

            return ScreenshotUploadResult.Failure(e.Message);
        }
    }
}
