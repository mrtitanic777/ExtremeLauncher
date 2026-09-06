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
 * Ported from launcher/settings/{Setting,OverrideSetting,PassthroughSetting}.{h,cpp}.
 *
 * A Setting does NOT hold its own value. Set() raises an event, the owning SettingsObject writes the
 * value to backing storage, and Get() reads it back out. That indirection is what lets one setting be
 * backed by an INI file, an override of a global, or a passthrough to another setting, without the
 * caller knowing which. Preserved as-is.
 *
 * SYNONYMS: a setting can answer to several config keys. The first is canonical -- writes go there
 * and the others are deleted -- while reads accept any of them. That is how config keys get renamed
 * without breaking existing files.
 */

namespace ExtremeLauncher.Settings;

public sealed class SettingChangedEventArgs : EventArgs
{
    public SettingChangedEventArgs(Setting setting, object? value)
    {
        Setting = setting;
        Value = value;
    }

    public Setting Setting { get; }

    public object? Value { get; }
}

public sealed class SettingEventArgs : EventArgs
{
    public SettingEventArgs(Setting setting) => Setting = setting;

    public Setting Setting { get; }
}

public class Setting
{
    private readonly List<string> _synonyms;

    public Setting(IEnumerable<string> synonyms, object? defaultValue = null)
    {
        _synonyms = [.. synonyms];

        if (_synonyms.Count == 0)
        {
            throw new ArgumentException("A setting needs at least one config key.", nameof(synonyms));
        }

        _defaultValue = defaultValue;
    }

    public Setting(string id, object? defaultValue = null) : this([id], defaultValue)
    {
    }

    private readonly object? _defaultValue;

    public event EventHandler<SettingChangedEventArgs>? Changed;

    public event EventHandler<SettingEventArgs>? WasReset;

    /// <summary>Identifies the setting in code. The first synonym.</summary>
    public virtual string Id => _synonyms[0];

    /// <summary>Every config-file key this setting answers to; the first is canonical.</summary>
    public virtual IReadOnlyList<string> ConfigKeys => _synonyms;

    public virtual object? DefaultValue => _defaultValue;

    /// <summary>Reads the current value, falling back to the default when storage has none.</summary>
    public virtual object? Get()
    {
        if (Storage is null)
        {
            return DefaultValue;
        }

        return Storage.RetrieveValue(this) ?? DefaultValue;
    }

    /// <summary>Requests a change. The owning <see cref="SettingsObject"/> performs the write.</summary>
    public virtual void Set(object? value) => Changed?.Invoke(this, new SettingChangedEventArgs(this, value));

    /// <summary>Requests a reset to default. The owning <see cref="SettingsObject"/> performs the removal.</summary>
    public virtual void Reset() => WasReset?.Invoke(this, new SettingEventArgs(this));

    internal SettingsObject? Storage { get; set; }

    internal void RaiseChanged(object? value) => Set(value);

    public override string ToString() => $"{Id}={Get()}";
}

/// <summary>
/// A setting that shadows another one while a gate setting is true, and defers to it otherwise.
/// </summary>
/// <remarks>
/// Used for per-instance overrides of global settings: the gate is the "override this" checkbox.
/// Writes always go to this setting's own storage, so the global is left untouched.
/// </remarks>
public sealed class OverrideSetting : Setting
{
    private readonly Setting _other;
    private readonly Setting _gate;

    public OverrideSetting(Setting other, Setting gate) : base(other.ConfigKeys)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(gate);

        _other = other;
        _gate = gate;
    }

    private bool IsOverriding => Convert.ToBoolean(_gate.Get() ?? false, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The overridden setting's current value, so turning the gate on starts from it.</summary>
    public override object? DefaultValue => _other.Get();

    public override object? Get() => IsOverriding ? base.Get() : _other.Get();
}

/// <summary>
/// Like <see cref="OverrideSetting"/>, but writes reach the underlying setting as well.
/// </summary>
/// <remarks>
/// The gate may be null, in which case it never overrides — upstream checks for that, unlike
/// OverrideSetting which asserts the gate exists.
/// </remarks>
public sealed class PassthroughSetting : Setting
{
    private readonly Setting _other;
    private readonly Setting? _gate;

    public PassthroughSetting(Setting other, Setting? gate) : base(other.ConfigKeys)
    {
        ArgumentNullException.ThrowIfNull(other);

        _other = other;
        _gate = gate;
    }

    private bool IsOverriding
        => _gate is not null
           && Convert.ToBoolean(_gate.Get() ?? false, System.Globalization.CultureInfo.InvariantCulture);

    public override object? DefaultValue => IsOverriding ? _other.Get() : _other.DefaultValue;

    public override object? Get() => IsOverriding ? base.Get() : _other.Get();

    public override void Set(object? value)
    {
        if (IsOverriding)
        {
            base.Set(value);
        }

        // Unconditionally propagated -- that is what makes this a passthrough rather than an override.
        _other.Set(value);
    }

    public override void Reset()
    {
        if (IsOverriding)
        {
            base.Reset();
        }

        _other.Reset();
    }
}
