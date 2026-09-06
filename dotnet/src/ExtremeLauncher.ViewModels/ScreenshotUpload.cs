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
 *  You should have received a copy of the flow around uploading screenshots. See ScreenshotsPage.cpp.
 *
 * THE FLOW AROUND THE UPLOAD, which matters more than the upload -- the same shape as LogUpload. A
 * screenshot can show a username, a server, a face on a skin, whatever was on screen; sending it to
 * imgur is public and, for an anonymous upload, not really undoable. So: ask first, name the host,
 * default to no. Uploading several bundles them into one album link, which is what upstream does and
 * what makes "share my session" one link rather than ten.
 */

namespace ExtremeLauncher.ViewModels;

/// <summary>What came of an upload.</summary>
public sealed record ScreenshotUploadResult(bool Ok, string Link, string Error)
{
    public static ScreenshotUploadResult Success(string link) => new(true, link, string.Empty);

    public static ScreenshotUploadResult Failure(string error) => new(false, string.Empty, error);
}

/// <summary>Uploads screenshots to the configured image host. Implemented by the app.</summary>
public interface IScreenshotUploader
{
    /// <summary>The host uploads go to, for the confirmation. Empty when none is configured.</summary>
    string Destination { get; }

    /// <summary>Uploads the given files; more than one is bundled into an album.</summary>
    /// <returns>The single image link, or the album link when there were several.</returns>
    Task<ScreenshotUploadResult> UploadAsync(IReadOnlyList<string> filePaths);
}

public static class ScreenshotUpload
{
    /// <summary>Confirms, uploads, and puts the link on the clipboard.</summary>
    /// <returns>A sentence for the status line. Empty when the user said no.</returns>
    public static async Task<string> RunAsync(
        IReadOnlyList<string> filePaths,
        IScreenshotUploader? uploader,
        IUserPrompts? prompts,
        IClipboard? clipboard)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        if (uploader is null || uploader.Destination.Length == 0)
        {
            return "Uploading is not available in this build.";
        }

        if (filePaths.Count == 0)
        {
            return "Select a screenshot to upload first.";
        }

        if (prompts is null)
        {
            // No dialog means no upload -- the same rule as every other publishing action here.
            // Silently uploading somebody's screenshot because a dialog was unavailable is the worst
            // possible failure mode.
            return "Uploading needs a confirmation dialog, and this build has none.";
        }

        var what = filePaths.Count == 1
            ? "the selected screenshot"
            : $"{filePaths.Count} screenshots";

        var confirmed = await prompts.ConfirmAsync(
            "Upload screenshots?",
            $"You are about to upload {what} to {uploader.Destination}.\n\n"
            + "A screenshot can show your user name, a server address, or anything else that was on "
            + "your screen. Anyone with the link will be able to see it, and an anonymous upload "
            + "cannot easily be taken back.\n\n"
            + "Please check before uploading.",
            "Upload",
            // Public and effectively irreversible: the affirmative should not be the button a stray
            // Return lands on.
            destructive: true).ConfigureAwait(true);

        if (!confirmed)
        {
            return string.Empty;
        }

        var result = await uploader.UploadAsync(filePaths).ConfigureAwait(true);

        if (!result.Ok)
        {
            return "Upload failed: " + result.Error;
        }

        if (clipboard is not null && await clipboard.SetTextAsync(result.Link).ConfigureAwait(true))
        {
            return filePaths.Count == 1
                ? $"Uploaded. The link is on your clipboard: {result.Link}"
                : $"Uploaded {filePaths.Count} screenshots to an album, link on your clipboard: {result.Link}";
        }

        return "Uploaded: " + result.Link;
    }
}
