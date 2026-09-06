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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from the settings registrations of launcher/BaseInstance.cpp and
 * launcher/minecraft/MinecraftInstance.cpp.
 *
 * WHAT AN instance.cfg CONTAINS. Every key here is one users already have on disk, so the names and
 * defaults are a compatibility surface, not a design choice -- a renamed key silently resets whatever
 * it used to hold.
 *
 * THE OVERRIDE PATTERN IS THE WHOLE DESIGN. Almost nothing here is a plain instance setting. Instead a
 * boolean gate ("OverrideMemory") decides whether the instance's own value or the global one applies,
 * and the gated settings are registered as overrides of the global object's. Turning a gate off
 * restores the global value WITHOUT losing what the instance had, so a user can toggle back and forth.
 * Grouping matters too: one gate covers several settings, so "override memory" means all three of
 * min, max and permgen together.
 *
 * NOT PORTED: the parts of BaseInstance that are not settings -- the launch bookkeeping, the
 * QAbstractListModel plumbing, and the managed-pack fields that only the modplatform wave populates.
 */

using System.Globalization;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;
using ExtremeLauncher.Settings;

namespace ExtremeLauncher.Launch;

/// <summary>The launcher-wide defaults an instance can override.</summary>
public static class GlobalSettings
{
    /// <summary>Registers every global setting, with the defaults a fresh install gets.</summary>
    public static SettingsObject Create(string path)
    {
        var settings = new IniSettingsObject(path);

        Register(settings);
        return settings;
    }

    /// <summary>Registers the globals onto an existing object, so tests can supply their own.</summary>
    public static void Register(SettingsObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Java.
        settings.RegisterSetting("JavaPath", string.Empty);
        settings.RegisterSetting("JvmArgs", string.Empty);
        settings.RegisterSetting("IgnoreJavaCompatibility", false);
        settings.RegisterSetting("AutomaticJavaSwitch", true);
        settings.RegisterSetting("AutomaticJavaDownload", true);

        // Memory. The default maximum is derived from the machine rather than fixed, because 4 GiB on
        // a 4 GiB laptop is a swap storm.
        settings.RegisterSetting("MinMemAlloc", 512);
        settings.RegisterSetting("MaxMemAlloc", SysInfo.SuitableMaxMemory());
        settings.RegisterSetting("PermGen", 128);

        // The game window.
        settings.RegisterSetting("LaunchMaximized", false);
        settings.RegisterSetting("MinecraftWinWidth", 854);
        settings.RegisterSetting("MinecraftWinHeight", 480);

        // Commands run around the game.
        settings.RegisterSetting("PreLaunchCommand", string.Empty);
        settings.RegisterSetting("WrapperCommand", string.Empty);
        settings.RegisterSetting("PostExitCommand", string.Empty);

        // The console window.
        settings.RegisterSetting("ShowConsole", false);
        settings.RegisterSetting("AutoCloseConsole", false);
        settings.RegisterSetting("ShowConsoleOnError", true);
        settings.RegisterSetting("LogPrePostOutput", true);
        settings.RegisterSetting("ConsoleMaxLines", 100_000);
        settings.RegisterSetting("ConsoleOverflowStop", true);

        // Native library workarounds.
        settings.RegisterSetting("UseNativeOpenAL", false);
        settings.RegisterSetting("CustomOpenALPath", string.Empty);
        settings.RegisterSetting("UseNativeGLFW", false);
        settings.RegisterSetting("CustomGLFWPath", string.Empty);

        // Performance tweaks, all Linux-only in practice.
        settings.RegisterSetting("EnableFeralGamemode", false);
        settings.RegisterSetting("EnableMangoHud", false);
        settings.RegisterSetting("UseDiscreteGpu", false);
        settings.RegisterSetting("UseZink", false);

        settings.RegisterSetting("CloseAfterLaunch", false);
        settings.RegisterSetting("QuitAfterGameStop", false);
        settings.RegisterSetting("OnlineFixes", false);
        settings.RegisterSetting("Env", string.Empty);

        settings.RegisterSetting("ShowGameTime", true);
        settings.RegisterSetting("RecordGameTime", true);

        /*
         * The metadata server, overridable. Ported from the "Meta URL" block in Application.cpp, where
         * it is validated at startup and RESET when it is not an http(s) URL rather than left to fail
         * on every download -- a typo in a settings file would otherwise break the launcher in a way
         * that looks like the network being down.
         */
        /*
         * WHAT THE WINDOW LOOKS LIKE. Upstream registers ApplicationTheme alongside IconTheme and
         * BackgroundCat; only this one is ported, because the other two need a theme engine and a
         * cat-pack loader that are not. "system" follows the desktop, which is what somebody who has
         * never opened the settings should get.
         */
        settings.RegisterSetting("ApplicationTheme", "system");

        /*
         * PROXY. The synonyms are upstream's: ProxyAddr was called ProxyHostName once, and an
         * existing config from an older install still spells it that way. Registering both means
         * such a config keeps working rather than silently losing the setting.
         *
         * THE DEFAULT IS "None", NOT "Default", which is upstream's and is worth stating because it
         * is the surprising half: out of the box the launcher connects directly and ignores whatever
         * proxy the machine is configured with. Somebody on a network that requires one has to come
         * here and pick "Default". The four values are spelled exactly as upstream writes them --
         * "Default", "None", "SOCKS5", "HTTP" -- because the config file is shared with it.
         */
        settings.RegisterSetting("ProxyType", "None");
        settings.RegisterSetting(["ProxyAddr", "ProxyHostName"], "127.0.0.1");
        settings.RegisterSetting("ProxyPort", 8080);
        settings.RegisterSetting(["ProxyUser", "ProxyUsername"], string.Empty);
        settings.RegisterSetting(["ProxyPass", "ProxyPassword"], string.Empty);

        settings.RegisterSetting("MetaURLOverride", string.Empty);

        if (!IsUsableMetaUrl(settings.Get("MetaURLOverride") as string))
        {
            settings.Reset("MetaURLOverride");
        }

        RegisterPasteSettings(settings);
    }

    /// <summary>The paste service a log gets uploaded to, and upstream's migration into it.</summary>
    /// <remarks>
    /// PastebinType IS STORED AS A RAW INTEGER -- the numeric value of upstream's enum. That is a
    /// poor way to store a setting and it is not this port's to change: the config file is shared,
    /// so the numbers are format.
    ///
    /// Upstream's own comment on this block is "HACK: This code feels so stupid is there a less
    /// stupid way of doing this?". It is doing two real jobs, though, and both are kept.
    /// </remarks>
    private static void RegisterPasteSettings(SettingsObject settings)
    {
        settings.RegisterSetting("PastebinURL", string.Empty);
        settings.RegisterSetting("PastebinType", (int)PasteType.Mclogs);
        settings.RegisterSetting("PastebinCustomAPIBase", string.Empty);

        /*
         * MIGRATION. PastebinURL is the old single-service setting, from when 0x0.st was the only
         * option. Somebody who had pointed it at their OWN instance gets that carried over as a
         * custom base; somebody who had left it at the default gets the new default instead, which
         * is mclo.gs -- a better place for a Minecraft log than a generic paste bin.
         */
        var legacy = settings.Get("PastebinURL") as string ?? string.Empty;

        if (legacy.Length != 0 && legacy != "https://0x0.st")
        {
            settings.Set("PastebinType", (int)PasteType.NullPointer);
            settings.Set("PastebinCustomAPIBase", legacy);
            settings.Reset("PastebinURL");
        }

        /*
         * SANITISATION. A number outside the enum -- a hand-edited config, or one written by a newer
         * version that knows a fifth service -- resets BOTH settings, because a custom base belongs
         * to a service and is meaningless without one.
         */
        if (!TryReadPasteType(settings, out _))
        {
            settings.Reset("PastebinType");
            settings.Reset("PastebinCustomAPIBase");
        }
    }

    /// <summary>Reads PastebinType, distinguishing "not a number" from "a number out of range".</summary>
    /// <remarks>
    /// UPSTREAM CHECKS BOTH, through toInt(&amp;ok), and the difference is not academic: GetInt falls
    /// back to the DEFAULT when a value will not parse, so "PastebinType=banana" reads as 3, passes a
    /// range check, and leaves a custom API base attached to a service nobody chose. The first version
    /// of this code did exactly that and a test caught it.
    /// </remarks>
    private static bool TryReadPasteType(SettingsObject settings, out PasteType type)
    {
        type = PasteType.Mclogs;

        var raw = settings.Get("PastebinType");

        var value = raw switch
        {
            int i => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (int?)null,
        };

        if (value is not { } number || !IsKnownPasteType(number))
        {
            return false;
        }

        type = (PasteType)number;

        return true;
    }

    /// <summary>Whether a stored PastebinType is one this build knows.</summary>
    public static bool IsKnownPasteType(int value)
        => value >= (int)PasteType.NullPointer && value <= (int)PasteType.Mclogs;

    /// <summary>The paste service to use, falling back to the default for anything unrecognised.</summary>
    public static PasteType ResolvePasteType(SettingsObject? settings)
        => settings is not null && TryReadPasteType(settings, out var type) ? type : PasteType.Mclogs;

    /// <summary>The custom API base, or empty for the service's own.</summary>
    public static string ResolvePasteBase(SettingsObject? settings)
        => settings?.GetString("PastebinCustomAPIBase", string.Empty) ?? string.Empty;

    /// <summary>Whether a meta URL override is one the launcher can actually fetch from.</summary>
    /// <remarks>
    /// http and https only, as upstream: a "file:" or "ftp:" override would be accepted by a general
    /// URL parser and then fail on every request.
    /// </remarks>
    public static bool IsUsableMetaUrl(string? url)
        => url is { Length: > 0 }
           && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
           && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The metadata server to use: the override when there is a usable one, else the build default.
    /// </summary>
    /// <remarks>
    /// Upstream resolves this inside BaseEntity::url(), per request. Done once here instead, because
    /// the launch path threads a meta URL through several tasks and re-reading a setting in each of
    /// them is how two of them end up disagreeing.
    /// </remarks>
    public static string ResolveMetaUrl(SettingsObject? settings)
    {
        var over = settings?.Get("MetaURLOverride") as string;

        return IsUsableMetaUrl(over) ? over! : BuildConfig.Instance.MetaUrl;
    }
}

/// <summary>
/// One instance's settings, as they sit in its instance.cfg.
/// </summary>
public sealed class InstanceSettings
{
    private readonly SettingsObject _settings;

    public InstanceSettings(SettingsObject settings, SettingsObject globalSettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(globalSettings);

        _settings = settings;

        RegisterBase(globalSettings);
        RegisterMinecraft(globalSettings);
    }

    /// <summary>Opens an instance's settings from its directory.</summary>
    public static InstanceSettings Open(InstancePaths paths, SettingsObject globalSettings)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new InstanceSettings(new IniSettingsObject(paths.ConfigPath), globalSettings);
    }

    public SettingsObject Settings => _settings;

    // ================================================================== the plain fields

    public string Name
    {
        get => GetString("name");
        set => _settings.Set("name", value);
    }

    public string IconKey
    {
        get => GetString("iconKey");
        set => _settings.Set("iconKey", value);
    }

    public string Notes
    {
        get => GetString("notes");
        set => _settings.Set("notes", value);
    }

    /// <summary>Which kind of instance this is. Empty for anything the launcher itself made.</summary>
    public string InstanceType => GetString("InstanceType");

    public long TotalTimePlayed => GetLong("totalTimePlayed");

    public long LastLaunchTime => GetLong("lastLaunchTime");

    // ================================================================== the gated ones

    /// <summary>The Java interpreter, or the global one when this instance does not override it.</summary>
    public string JavaPath => GetString("JavaPath");

    public string JvmArgs => GetString("JvmArgs");

    public bool IgnoreJavaCompatibility => GetBool("IgnoreJavaCompatibility");

    /// <summary>
    /// Whether a launch may fetch a Java when it cannot find a compatible one.
    /// </summary>
    /// <remarks>
    /// A GLOBAL in practice: there is no per-instance override gate for it, so this reads through to
    /// the global setting for every instance, which is what upstream does too. Registered and
    /// defaulted since an early wave; nothing acted on it until wave 23.
    /// </remarks>
    public bool AutomaticJavaDownload => GetBool("AutomaticJavaDownload");

    public int MinMemAlloc => GetInt("MinMemAlloc");

    public int MaxMemAlloc => GetInt("MaxMemAlloc");

    public int PermGen => GetInt("PermGen");

    public string WrapperCommand => GetString("WrapperCommand");

    /// <summary>Whether this instance was made from a modpack rather than by hand.</summary>
    public bool IsManagedPack => GetBool("ManagedPack");

    /// <summary>Which platform it came from -- "modrinth", "flame", "atlauncher".</summary>
    public string ManagedPackType => GetString("ManagedPackType");

    /// <summary>The platform's id for the pack, or EMPTY for a pack imported from a file.</summary>
    /// <remarks>
    /// THE FIELD THAT DECIDES WHETHER AN UPDATE IS POSSIBLE. A .mrpack on disk does not carry its own
    /// project id -- only the launcher's own pack browser knows it -- so a file import has a name and
    /// a version and nothing to look up.
    /// </remarks>
    public string ManagedPackId => GetString("ManagedPackID");

    /// <summary>What the pack calls itself.</summary>
    public string ManagedPackName => GetString("ManagedPackName");

    /// <summary>The platform's id for this particular version, or empty for a file import.</summary>
    public string ManagedPackVersionId => GetString("ManagedPackVersionID");

    /// <summary>The version as a human reads it -- "5.9.2".</summary>
    public string ManagedPackVersionName => GetString("ManagedPackVersionName");

    /// <summary>Whether this pack could be checked for a newer version.</summary>
    /// <remarks>
    /// Separate from <see cref="IsManagedPack"/> ON PURPOSE, and a divergence from upstream, which
    /// has only the one flag. Its ModrinthInstanceCreationTask carries the comment "Don't add managed
    /// info to packs without an ID (most likely imported from ZIP)" and then its else branch calls
    /// setManagedPack anyway with empty ids -- so ManagedPack is true, its managed-pack page appears,
    /// and the page has nothing to look the pack up by.
    ///
    /// Knowing where an instance came from and being able to update it are two different questions,
    /// so they are two different properties here.
    /// </remarks>
    public bool CanCheckForPackUpdates => IsManagedPack && ManagedPackId.Length != 0;

    /// <summary>Records where this instance came from. Ported from BaseInstance::setManagedPack.</summary>
    public void SetManagedPack(string type, string id, string name, string versionId, string version)
    {
        _settings.Set("ManagedPack", true);
        _settings.Set("ManagedPackType", type);
        _settings.Set("ManagedPackID", id);
        _settings.Set("ManagedPackName", name);
        _settings.Set("ManagedPackVersionID", versionId);
        _settings.Set("ManagedPackVersionName", version);
    }

    public string PreLaunchCommand => GetString("PreLaunchCommand");

    public string PostExitCommand => GetString("PostExitCommand");

    public bool OnlineFixes => GetBool("OnlineFixes");

    /// <summary>Turns a gate on or off, which switches a whole group between instance and global.</summary>
    public void SetOverride(string gate, bool enabled) => _settings.Set(gate, enabled);

    // ================================================================== registration

    /// <remarks>
    /// The negative-time guard is upstream's and worth keeping: a bug in an older version could write
    /// a negative total, and a negative playtime renders as nonsense forever after. Resetting on read
    /// repairs it in place the first time an affected instance is opened.
    /// </remarks>
    private void RegisterBase(SettingsObject globalSettings)
    {
        _settings.RegisterSetting("name", "Unnamed Instance");
        _settings.RegisterSetting("iconKey", "default");
        _settings.RegisterSetting("notes", string.Empty);
        _settings.RegisterSetting("lastLaunchTime", 0);
        _settings.RegisterSetting("totalTimePlayed", 0);

        if (GetLong("totalTimePlayed") < 0)
        {
            _settings.Reset("totalTimePlayed");
        }

        _settings.RegisterSetting("lastTimePlayed", 0);
        _settings.RegisterSetting("linkedInstances", "[]");
        _settings.RegisterSetting("InstanceType", string.Empty);

        var gameTime = _settings.RegisterSetting("OverrideGameTime", false);

        Override(globalSettings, "ShowGameTime", gameTime);
        Override(globalSettings, "RecordGameTime", gameTime);

        // Two spellings: the key was renamed and old instance.cfg files still carry the first.
        var commands = _settings.RegisterSetting(["OverrideCommands", "OverrideLaunchCmd"], false);

        Override(globalSettings, "PreLaunchCommand", commands);
        Override(globalSettings, "WrapperCommand", commands);
        Override(globalSettings, "PostExitCommand", commands);

        var console = _settings.RegisterSetting("OverrideConsole", false);

        Override(globalSettings, "ShowConsole", console);
        Override(globalSettings, "AutoCloseConsole", console);
        Override(globalSettings, "ShowConsoleOnError", console);
        Override(globalSettings, "LogPrePostOutput", console);

        // Passthrough, not override: these follow the global value with no gate at all. An instance
        // cannot have its own console scrollback limit, and pretending otherwise would need a gate
        // the UI has nowhere to show.
        Passthrough(globalSettings, "ConsoleMaxLines");
        Passthrough(globalSettings, "ConsoleOverflowStop");

        /*
         * A GLOBAL WITH NO PER-INSTANCE GATE. Without this the key is not registered on the instance
         * at all, so reading it returns null and the launcher behaves as though automatic Java
         * downloads were switched OFF -- whatever the settings window shows.
         *
         * That is exactly what happened: the setting was ticked, the global said true, and a launch
         * that could not find Java 17 refused instead of fetching one. Found by running a real launch
         * against an instance whose Java this machine does not have.
         */
        Passthrough(globalSettings, "AutomaticJavaDownload");
        Passthrough(globalSettings, "AutomaticJavaSwitch");

        /*
         * WHERE AN INSTANCE CAME FROM. Written by the import task, so an instance made from a modpack
         * remembers being one -- which is the difference between "you have some mods" and "you have
         * Fabulously Optimized 5.9.2".
         */
        _settings.RegisterSetting("ManagedPack", false);
        _settings.RegisterSetting("ManagedPackType", string.Empty);
        _settings.RegisterSetting("ManagedPackID", string.Empty);
        _settings.RegisterSetting("ManagedPackName", string.Empty);
        _settings.RegisterSetting("ManagedPackVersionID", string.Empty);
        _settings.RegisterSetting("ManagedPackVersionName", string.Empty);

        _settings.RegisterSetting("Profiler", string.Empty);
    }

    private void RegisterMinecraft(SettingsObject globalSettings)
    {
        // NOTE: the Java location gate also covers IgnoreJavaCompatibility, which is not a location.
        // Inherited: an instance pinned to a specific JVM is exactly the case where the user also
        // wants to say "yes, I know, run it anyway".
        var javaLocation = _settings.RegisterSetting("OverrideJavaLocation", false);
        var javaArgs = _settings.RegisterSetting("OverrideJavaArgs", false);

        _settings.RegisterSetting("AutomaticJava", false);

        Override(globalSettings, "JavaPath", javaLocation);
        Override(globalSettings, "JvmArgs", javaArgs);
        Override(globalSettings, "IgnoreJavaCompatibility", javaLocation);

        var window = _settings.RegisterSetting("OverrideWindow", false);

        Override(globalSettings, "LaunchMaximized", window);
        Override(globalSettings, "MinecraftWinWidth", window);
        Override(globalSettings, "MinecraftWinHeight", window);

        var memory = _settings.RegisterSetting("OverrideMemory", false);

        Override(globalSettings, "MinMemAlloc", memory);
        Override(globalSettings, "MaxMemAlloc", memory);
        Override(globalSettings, "PermGen", memory);

        var natives = _settings.RegisterSetting("OverrideNativeWorkarounds", false);

        Override(globalSettings, "UseNativeOpenAL", natives);
        Override(globalSettings, "CustomOpenALPath", natives);
        Override(globalSettings, "UseNativeGLFW", natives);
        Override(globalSettings, "CustomGLFWPath", natives);

        var performance = _settings.RegisterSetting("OverridePerformance", false);

        Override(globalSettings, "EnableFeralGamemode", performance);
        Override(globalSettings, "EnableMangoHud", performance);
        Override(globalSettings, "UseDiscreteGpu", performance);
        Override(globalSettings, "UseZink", performance);

        var miscellaneous = _settings.RegisterSetting("OverrideMiscellaneous", false);

        Override(globalSettings, "CloseAfterLaunch", miscellaneous);
        Override(globalSettings, "QuitAfterGameStop", miscellaneous);

        var legacy = _settings.RegisterSetting("OverrideLegacySettings", false);

        Override(globalSettings, "OnlineFixes", legacy);

        var environment = _settings.RegisterSetting("OverrideEnv", false);

        Override(globalSettings, "Env", environment);

        _settings.RegisterSetting("JoinServerOnLaunch", false);
        _settings.RegisterSetting("JoinServerOnLaunchAddress", string.Empty);
        _settings.RegisterSetting("JoinWorldOnLaunch", string.Empty);

        _settings.RegisterSetting("UseAccountForInstance", false);
        _settings.RegisterSetting("InstanceAccountId", string.Empty);

        _settings.RegisterSetting("ExportName", string.Empty);
        _settings.RegisterSetting("ExportVersion", "1.0.0");
        _settings.RegisterSetting("ExportSummary", string.Empty);
        _settings.RegisterSetting("ExportAuthor", string.Empty);
        _settings.RegisterSetting("ExportOptionalFiles", true);
    }

    private void Override(SettingsObject globalSettings, string key, Setting? gate)
    {
        if (globalSettings.GetSetting(key) is { } original && gate is not null)
        {
            _settings.RegisterOverride(original, gate);
        }
    }

    private void Passthrough(SettingsObject globalSettings, string key)
    {
        if (globalSettings.GetSetting(key) is { } original)
        {
            _settings.RegisterPassthrough(original, null);
        }
    }

    // ================================================================== typed reads

    private string GetString(string key) => _settings.Get(key)?.ToString() ?? string.Empty;

    private bool GetBool(string key)
        => _settings.Get(key) is { } value
           && (value is bool flag ? flag : bool.TryParse(value.ToString(), out var parsed) && parsed);

    private int GetInt(string key)
        => _settings.Get(key) is { } value
           && int.TryParse(
               value.ToString(),
               System.Globalization.NumberStyles.Integer,
               System.Globalization.CultureInfo.InvariantCulture,
               out var parsed)
            ? parsed
            : 0;

    private long GetLong(string key)
        => _settings.Get(key) is { } value
           && long.TryParse(
               value.ToString(),
               System.Globalization.NumberStyles.Integer,
               System.Globalization.CultureInfo.InvariantCulture,
               out var parsed)
            ? parsed
            : 0;
}
