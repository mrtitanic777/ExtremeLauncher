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
 * Ported in behaviour from launcher/ui/dialogs/AboutDialog.cpp.
 *
 * WHAT VERSION IS THIS, which is the first question anybody answering a bug report asks. Upstream's
 * dialog also carries a credits tab fetched over the network and a patrons list; neither is ported,
 * and the parts that matter for support are.
 *
 * THE LICENCE IS STATED, not linked. This is GPL-3.0 software and the licence requires that the user
 * be told; a dialog that only says "Extreme Launcher 5.1" and a website link does not do that.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.ViewModels;

public sealed partial class AboutViewModel : ObservableObject
{
    private readonly BuildConfig _config;

    /// <param name="config">Null uses the running configuration, which is what the window does.</param>
    public AboutViewModel(BuildConfig? config = null, string? dataDirectory = null)
    {
        _config = config ?? BuildConfig.Instance;

        DataDirectory = dataDirectory ?? string.Empty;
    }

    public string Name => _config.LauncherDisplayName;

    /// <summary>
    /// The version, with the channel where it is not a release.
    /// </summary>
    /// <remarks>
    /// The channel matters in a bug report: "5.1" from a develop build and "5.1" from a release are
    /// different software, and only one of them is what a user could have downloaded.
    /// </remarks>
    public string Version => _config.VersionChannel is { Length: > 0 } channel
        && !string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase)
            ? $"{_config.VersionString} ({channel})"
            : _config.VersionString;

    /// <summary>The commit, or empty when the build did not record one.</summary>
    public string Commit => _config.GitCommit;

    public bool HasCommit => Commit.Length != 0;

    public string Platform => _config.BuildPlatform;

    /// <summary>What the runtime actually is, which the build config cannot know.</summary>
    /// <remarks>
    /// Worth showing separately from BuildPlatform: this port runs one build on three desktops, so
    /// "which OS" is a genuine question that the build string does not answer.
    /// </remarks>
    public string Runtime =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()} "
        + $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}) "
        + $"on .NET {Environment.Version}";

    public string Copyright => _config.Copyright;

    public string SourceUrl => _config.LauncherGit;

    /// <summary>Where the launcher keeps its files, which is the other thing support always asks.</summary>
    public string DataDirectory { get; }

    public bool HasDataDirectory => DataDirectory.Length != 0;

    /// <summary>
    /// The licence, stated rather than linked.
    /// </summary>
    /// <remarks>
    /// GPL-3.0-only, which the source headers say on every file. Three of them are Apache-2.0 by
    /// inheritance from MultiMC and say so individually; the launcher as a whole is GPL.
    /// </remarks>
    public string License =>
        $"{Name} is free software: you can redistribute it and/or modify it under the terms of the "
        + "GNU General Public License as published by the Free Software Foundation, version 3.\n\n"
        + "This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; "
        + "without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.";

    /// <summary>
    /// Everything above as one block, for pasting into a bug report.
    /// </summary>
    /// <remarks>
    /// The reason the dialog has a copy button at all. Asking somebody to retype a commit hash is how
    /// bug reports arrive with the wrong one.
    /// </remarks>
    public string SupportSummary
    {
        get
        {
            var lines = new List<string>
            {
                $"{Name} {Version}",
                $"Platform: {Platform}",
                $"Runtime: {Runtime}",
            };

            if (HasCommit)
            {
                lines.Add($"Commit: {Commit}");
            }

            if (HasDataDirectory)
            {
                lines.Add($"Data directory: {DataDirectory}");
            }

            return string.Join("\n", lines);
        }
    }
}
