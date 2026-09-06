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
 * Ported from launcher/settings/SettingsObject.{h,cpp} and INISettingsObject.{h,cpp}.
 */

using System.Globalization;

namespace ExtremeLauncher.Settings;

/// <summary>A registry of named settings over some backing store.</summary>
public abstract class SettingsObject
{
    private readonly Dictionary<string, Setting> _settings = new(StringComparer.Ordinal);

    /// <summary>Raised for any registered setting's change, after it has been written.</summary>
    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public event EventHandler<SettingEventArgs>? SettingReset;

    public IReadOnlyCollection<Setting> Settings => _settings.Values;

    public Setting? RegisterSetting(IEnumerable<string> synonyms, object? defaultValue = null)
    {
        var keys = synonyms.ToList();

        if (keys.Count == 0)
        {
            return null;
        }

        return Register(new Setting(keys, defaultValue));
    }

    public Setting? RegisterSetting(string id, object? defaultValue = null)
        => Register(new Setting(id, defaultValue));

    /// <summary>Registers a setting that shadows <paramref name="original"/> while the gate is true.</summary>
    public Setting? RegisterOverride(Setting original, Setting gate)
        => Register(new OverrideSetting(original, gate));

    /// <summary>Registers a setting that also writes through to <paramref name="original"/>.</summary>
    public Setting? RegisterPassthrough(Setting original, Setting? gate)
        => Register(new PassthroughSetting(original, gate));

    private Setting? Register(Setting setting)
    {
        if (Contains(setting.Id))
        {
            // Upstream logs and returns null rather than throwing.
            return null;
        }

        setting.Storage = this;
        setting.Changed += OnSettingChanged;
        setting.WasReset += OnSettingReset;

        _settings[setting.Id] = setting;

        return setting;
    }

    public Setting? GetSetting(string id) => _settings.GetValueOrDefault(id);

    public object? Get(string id) => GetSetting(id)?.Get();

    public string GetString(string id, string defaultValue = "")
        => Get(id) is { } value
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? defaultValue
            : defaultValue;

    public bool GetBool(string id, bool defaultValue = false)
    {
        var value = Get(id);

        return value switch
        {
            null => defaultValue,
            bool b => b,
            string s => bool.TryParse(s, out var parsed) ? parsed : defaultValue,
            _ => defaultValue,
        };
    }

    public int GetInt(string id, int defaultValue = 0)
    {
        var value = Get(id);

        return value switch
        {
            null => defaultValue,
            int i => i,
            string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue,
            _ => defaultValue,
        };
    }

    public bool Set(string id, object? value)
    {
        var setting = GetSetting(id);

        if (setting is null)
        {
            return false;
        }

        setting.Set(value);
        return true;
    }

    public void Reset(string id) => GetSetting(id)?.Reset();

    public bool Contains(string id) => _settings.ContainsKey(id);

    /// <summary>Re-applies every setting's current value, forcing storage to catch up.</summary>
    public virtual bool Reload()
    {
        foreach (var setting in _settings.Values.ToList())
        {
            setting.Set(setting.Get());
        }

        return true;
    }

    protected abstract void ApplyChange(Setting setting, object? value);

    protected abstract void ApplyReset(Setting setting);

    internal abstract object? RetrieveValue(Setting setting);

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        ApplyChange(e.Setting, e.Value);
        SettingChanged?.Invoke(this, e);
    }

    private void OnSettingReset(object? sender, SettingEventArgs e)
    {
        ApplyReset(e.Setting);
        SettingReset?.Invoke(this, e);
    }
}

/// <summary>A <see cref="SettingsObject"/> backed by an INI file on disk.</summary>
public sealed class IniSettingsObject : SettingsObject
{
    private readonly IniFile _ini = new();

    private bool _suspendSave;
    private bool _savePending;

    public IniSettingsObject(string path)
    {
        FilePath = path;
        _ini.LoadFile(path);
    }

    /// <summary>
    /// Opens the FIRST path, migrating an older config into place if one is found further down the
    /// list.
    /// </summary>
    /// <remarks>
    /// THIS IS A MIGRATION, not a fallback, and the difference matters. The first path is always the
    /// one used and written to; the later ones name places a previous version of the launcher kept its
    /// config. When one of those exists it is COPIED to the first path, so the next run finds it there
    /// and the old file stops being consulted.
    ///
    /// An existing destination is never overwritten — upstream relies on QFile::copy refusing to —
    /// because a real config at the current path must win over an abandoned one at an old path.
    ///
    /// An earlier draft of this port treated the list as a plain fallback chain, opening whichever
    /// path loaded first. That reads the old file forever and writes changes back to it, so a user who
    /// upgrades never actually migrates.
    /// </remarks>
    public IniSettingsObject(IEnumerable<string> paths)
    {
        var list = paths.ToList();

        if (list.Count == 0)
        {
            FilePath = string.Empty;
            return;
        }

        FilePath = list[0];

        foreach (var path in list.Skip(1))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                // overwrite: false -- see the remarks.
                File.Copy(path, FilePath, overwrite: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The destination already exists, or is not writable. Either way the first path is
                // still the one to use.
            }

            break;
        }

        _ini.LoadFile(FilePath);
    }

    public string FilePath { get; set; }

    public override bool Reload() => _ini.LoadFile(FilePath) && base.Reload();

    /// <summary>Batches writes until <see cref="ResumeSave"/>, so a burst of changes hits disk once.</summary>
    public void SuspendSave() => _suspendSave = true;

    public void ResumeSave()
    {
        _suspendSave = false;

        if (_savePending)
        {
            _savePending = false;
            _ini.SaveFile(FilePath);
        }
    }

    protected override void ApplyChange(Setting setting, object? value)
    {
        if (!Contains(setting.Id))
        {
            return;
        }

        if (value is not null)
        {
            // Canonical key gets the value; every synonym is removed so only one spelling survives.
            var keys = setting.ConfigKeys;
            _ini.Set(keys[0], value);

            for (var i = 1; i < keys.Count; i++)
            {
                _ini.Remove(keys[i]);
            }
        }
        else
        {
            // A null value means "unset", handled the same as a reset.
            foreach (var key in setting.ConfigKeys)
            {
                _ini.Remove(key);
            }
        }

        Save();
    }

    protected override void ApplyReset(Setting setting)
    {
        if (!Contains(setting.Id))
        {
            return;
        }

        foreach (var key in setting.ConfigKeys)
        {
            _ini.Remove(key);
        }

        Save();
    }

    internal override object? RetrieveValue(Setting setting)
    {
        if (!Contains(setting.Id))
        {
            return null;
        }

        // Reads accept any synonym; first match wins.
        foreach (var key in setting.ConfigKeys)
        {
            if (_ini.Contains(key))
            {
                return _ini.Get(key);
            }
        }

        return null;
    }

    private void Save()
    {
        if (_suspendSave)
        {
            _savePending = true;
            return;
        }

        _ini.SaveFile(FilePath);
    }
}
