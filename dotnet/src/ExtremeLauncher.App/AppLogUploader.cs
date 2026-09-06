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
 * The app's half of uploading a log: reads the setting, runs the task.
 *
 * THE SETTING IS READ PER UPLOAD, not once at startup. Somebody who has just discovered their paste
 * service is down should be able to change it in the settings window and press Upload again without
 * restarting the launcher -- unlike the proxy, nothing here is baked into a live client.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Net;
using ExtremeLauncher.Settings;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppLogUploader(HttpClient client, SettingsObject settings) : ILogUploader
{
    public string Destination
        => PasteUpload.HostFor(
            GlobalSettings.ResolvePasteType(settings),
            GlobalSettings.ResolvePasteBase(settings));

    public async Task<LogUploadResult> UploadAsync(string text)
    {
        var task = new PasteUpload(
            client,
            text,
            GlobalSettings.ResolvePasteType(settings),
            GlobalSettings.ResolvePasteBase(settings));

        // RunAsync reports rather than throws, which is the pipeline's contract everywhere in this
        // port -- the failure text is on the task, not in an exception.
        return await task.RunAsync().ConfigureAwait(true)
            ? LogUploadResult.Success(task.PasteLink)
            : LogUploadResult.Failure(task.FailReason);
    }
}
