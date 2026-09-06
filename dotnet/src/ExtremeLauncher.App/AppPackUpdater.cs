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
 * The app's half of updating a modpack: work out what would change, ASK, then do it.
 *
 * THIS IS THE JOIN THE SPLIT EXISTED FOR. PackUpdateTask separates working out a plan from carrying
 * it out precisely so that something could put the plan in front of a person in between, and until
 * this file nothing did.
 */

using Avalonia.Controls;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;
using ExtremeLauncher.ViewModels;

// Avalonia.Controls has a ResourceProvider of its own; this is the mod-platform one.
using ResourceProvider = ExtremeLauncher.ModPlatform.ResourceProvider;

namespace ExtremeLauncher.App;

public sealed class AppPackUpdater(
    Func<Window?> owner,
    HttpClient client,
    IUserPrompts prompts,
    InstancePaths paths,
    SettingsObject instanceConfig,
    InstanceSettings settings,
    LauncherLog? log = null) : IPackUpdater
{
    /// <summary>How many paths to name before saying "and N more".</summary>
    /// <remarks>
    /// A CONFIRMATION NOBODY READS IS NOT A CONFIRMATION. Twenty-seven file paths in a message box
    /// is a wall somebody clicks past; half a dozen and a count is a thing they can actually check.
    /// </remarks>
    private const int PathsToShow = 8;

    public async Task<PackUpdateOutcome> UpdateAsync(IndexedVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var (plan, packFile) = await PackUpdateTask
            .PrepareAsync(client, paths.InstanceRoot, version)
            .ConfigureAwait(true);

        try
        {
            if (!plan.Possible)
            {
                // Not a question -- there is nothing to agree to. Shown as a statement with one
                // button, because the reason is the useful part.
                await prompts.ConfirmAsync("This pack cannot be updated", plan.Blocker, "OK").ConfigureAwait(true);

                return new PackUpdateOutcome(false, plan.Blocker);
            }

            if (!await AskAsync(version, plan).ConfigureAwait(true))
            {
                return new PackUpdateOutcome(false, string.Empty);
            }
        }
        finally
        {
            /*
             * The prepared pack is thrown away rather than handed to the task. It is one extra
             * download of a file that is usually a couple of hundred kilobytes, and the alternative
             * is a temp file whose lifetime spans a dialog somebody may leave open for an hour.
             */
            try
            {
                if (File.Exists(packFile))
                {
                    File.Delete(packFile);
                }
            }
            catch (IOException)
            {
            }
        }

        var pack = new IndexedPack
        {
            Provider = ResourceProvider.Modrinth,
            AddonId = settings.ManagedPackId,
            Name = settings.ManagedPackName,
        };

        var task = new PackUpdateTask(client, paths.InstanceRoot, paths.GameRoot, pack, version, instanceConfig);

        log?.Info($"Updating {settings.ManagedPackName} to {version.Version}: {plan.Summary}");

        // Behind a progress window: this downloads the pack again and then all of its changed mods,
        // and it is the one of the four where a killed launcher does the most damage.
        if (!await ProgressWindow.RunAsync(owner(), task, $"Updating {settings.ManagedPackName}").ConfigureAwait(true))
        {
            // A cancellation is not an error. It matters more here than anywhere: the update
            // stops before it has touched the instance, so there is genuinely nothing to report.
            if (task.State == TaskState.AbortedByUser)
            {
                log?.Info("Pack update cancelled.");

                return new PackUpdateOutcome(false, "Cancelled. Nothing was changed.");
            }

            log?.Error($"Pack update failed: {task.FailReason}");

            await prompts.ConfirmAsync("Could not update the pack", task.FailReason, "OK").ConfigureAwait(true);

            return new PackUpdateOutcome(false, task.FailReason);
        }

        var done = $"Updated to {version.Version}. "
                   + $"{task.Downloaded} file(s) downloaded, {task.Removed} removed or refreshed.";

        log?.Info(done);

        return new PackUpdateOutcome(true, done);
    }

    private Task<bool> AskAsync(IndexedVersion version, PackUpdatePlan plan)
    {
        var message = new System.Text.StringBuilder();

        message.Append("Updating ").Append(settings.ManagedPackName).Append(" from ")
            .Append(settings.ManagedPackVersionName.Length != 0 ? settings.ManagedPackVersionName : "the installed version")
            .Append(" to ").Append(version.Version).AppendLine(".");

        message.AppendLine();
        message.AppendLine(plan.Summary);

        if (plan.ToRemove.Count != 0)
        {
            message.AppendLine();
            message.AppendLine("These files will be deleted:");

            Append(message, plan.ToRemove);
        }

        if (plan.ToReplace.Count != 0)
        {
            message.AppendLine();
            message.AppendLine(
                "The pack's own config files will be replaced with the new version's. If you have "
                + "edited any of these, your changes will be lost:");

            Append(message, plan.ToReplace);
        }

        message.AppendLine();

        // The reassurance that is actually true, and the one people most want: their own additions
        // are not in either list because the pack never knew about them.
        message.Append("Anything you added yourself will be left alone.");

        return prompts.ConfirmAsync(
            "Update this modpack?",
            message.ToString(),
            "Update",
            // Files are deleted. The affirmative must not be the button a stray Return lands on.
            destructive: true);
    }

    private static void Append(System.Text.StringBuilder message, IReadOnlyList<string> paths)
    {
        foreach (var path in paths.Take(PathsToShow))
        {
            message.Append("    ").AppendLine(path);
        }

        if (paths.Count > PathsToShow)
        {
            message.Append("    … and ").Append(paths.Count - PathsToShow).AppendLine(" more");
        }
    }
}
