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
 * Ported in behaviour from upstream's ApplicationTheme setting and its ThemeManager.
 *
 * ONE SETTING, NOT A THEME ENGINE. Upstream carries a whole ThemeManager with widget themes, icon
 * themes and cat packs read from disk; none of that is ported and this is not it. What this does is
 * the part that every user notices on the first run: whether the window is light or dark.
 *
 * APPLIED LIVE, not at the next start. Avalonia's RequestedThemeVariant is a property on the
 * application, so changing it repaints every open window -- and a theme picker that needed a restart
 * would be a worse experience than no picker at all.
 */

using Avalonia;
using Avalonia.Styling;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.App;

public static class AppTheme
{
    public const string SettingKey = "ApplicationTheme";

    /// <summary>Reads the setting and applies it to the running application.</summary>
    public static void Apply(Application application, SettingsObject settings)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(settings);

        application.RequestedThemeVariant = VariantFor(settings.GetString(SettingKey, "system"));
    }

    /// <summary>
    /// Follows the setting for as long as the application runs.
    /// </summary>
    /// <remarks>
    /// Subscribed rather than applied once, so the settings window does not need to know a theme
    /// exists: it writes a value like any other setting and the window repaints itself.
    /// </remarks>
    public static void Follow(Application application, SettingsObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Apply(application, settings);

        settings.SettingChanged += (_, e) =>
        {
            if (string.Equals(e.Setting.Id, SettingKey, StringComparison.Ordinal))
            {
                Apply(application, settings);
            }
        };
    }

    /// <summary>
    /// Maps a stored id to a theme.
    /// </summary>
    /// <remarks>
    /// ANYTHING UNRECOGNISED IS "system", including the empty string. A config written by hand, or by
    /// a newer version that has more themes, must still produce a usable window rather than nothing.
    /// </remarks>
    public static ThemeVariant VariantFor(string id) => id switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
