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
 * Ported from GuiUtil::uploadPaste in launcher/ui/GuiUtil.cpp.
 *
 * THE FLOW AROUND THE UPLOAD, which matters more than the upload. A Minecraft log is not neutral
 * text: it carries the player's username, their home directory, the mods they run, sometimes a
 * server address, and on a bad day a session token. Sending it to a public paste bin is IRREVERSIBLE
 * -- the link is public, and "delete" is not a thing most of these services offer.
 *
 * So the shape upstream uses is kept exactly: ASK FIRST, NAME THE HOST, and default to No.
 */

namespace ExtremeLauncher.ViewModels;

/// <summary>What came of an upload.</summary>
public sealed record LogUploadResult(bool Ok, string Link, string Error)
{
    public static LogUploadResult Success(string link) => new(true, link, string.Empty);

    public static LogUploadResult Failure(string error) => new(false, string.Empty, error);
}

/// <summary>Uploads text to the configured paste service. Implemented by the app.</summary>
public interface ILogUploader
{
    /// <summary>The host a paste would go to, for the confirmation. Empty when none is configured.</summary>
    string Destination { get; }

    Task<LogUploadResult> UploadAsync(string text);
}

/// <summary>The shared "upload this text, carefully" flow.</summary>
/// <remarks>
/// Shared because there are two places to do it from -- the instance's log files and the launch
/// console -- and the careful part must not be reimplemented in the second one.
/// </remarks>
public static class LogUpload
{
    /// <summary>Confirms, uploads, and puts the link on the clipboard.</summary>
    /// <returns>A sentence for the status line. Empty when the user said no.</returns>
    public static async Task<string> RunAsync(
        string text,
        string name,
        ILogUploader? uploader,
        IUserPrompts? prompts,
        IClipboard? clipboard)
    {
        if (uploader is null || uploader.Destination.Length == 0)
        {
            return "Uploading is not available in this build.";
        }

        if (text.Length == 0)
        {
            return "There is nothing to upload.";
        }

        if (prompts is null)
        {
            /*
             * NO PROMPT MEANS NO UPLOAD. Every other action in this port treats a missing dialog
             * service as "cannot ask, so do not do it", and this is the one where that matters most:
             * silently publishing somebody's log because a dialog was unavailable would be the worst
             * possible failure mode.
             */
            return "Uploading needs a confirmation dialog, and this build has none.";
        }

        var confirmed = await prompts.ConfirmAsync(
            "Upload this log?",
            $"You are about to upload \"{name}\" to {uploader.Destination}.\n\n"
            + "A log can contain your user name, your file paths, the servers you have joined and "
            + "other personal information. Anyone with the link will be able to read it, and it "
            + "cannot be taken back.\n\n"
            + "Please check it before uploading.",
            "Upload",
            // Not "destructive" in the delete sense, but it is the same class of irreversible, and
            // the affirmative button should not be the one a stray Return lands on.
            destructive: true).ConfigureAwait(true);

        if (!confirmed)
        {
            return string.Empty;
        }

        var result = await uploader.UploadAsync(text).ConfigureAwait(true);

        if (!result.Ok)
        {
            return "Upload failed: " + result.Error;
        }

        if (clipboard is not null && await clipboard.SetTextAsync(result.Link).ConfigureAwait(true))
        {
            // Upstream copies the link and says so. The link is useless in a window nobody can copy
            // from -- getting it to the clipboard IS the feature.
            return $"Uploaded. The link is on your clipboard: {result.Link}";
        }

        return "Uploaded: " + result.Link;
    }
}
