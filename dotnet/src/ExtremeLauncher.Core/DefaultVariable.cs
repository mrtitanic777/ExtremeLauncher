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
 * Ported from launcher/DefaultVariable.h.
 */

namespace ExtremeLauncher.Core;

/// <summary>
/// A value with a fallback default that also remembers whether it was ever assigned.
/// </summary>
/// <remarks>
/// Note that <see cref="IsExplicit"/> and <see cref="IsDefault"/> are independent: assigning a value
/// equal to the default leaves <see cref="IsDefault"/> true while making <see cref="IsExplicit"/>
/// true as well. Callers rely on that distinction to round-trip serialized forms -- for example a
/// Gradle specifier written as <c>group:artifact:1.0@jar</c> must serialize back with the explicit
/// <c>@jar</c> even though <c>jar</c> is the default extension.
/// </remarks>
public sealed class DefaultVariable<T>
{
    private readonly T _defaultValue;
    private T? _currentValue;

    public DefaultVariable(T defaultValue) => _defaultValue = defaultValue;

    public T Value => IsDefault ? _defaultValue : _currentValue!;

    public bool IsDefault { get; private set; } = true;

    public bool IsExplicit { get; private set; }

    public void Set(T value)
    {
        _currentValue = value;
        IsDefault = EqualityComparer<T>.Default.Equals(value, _defaultValue);
        IsExplicit = true;
    }

    public static implicit operator T(DefaultVariable<T> variable) => variable.Value;

    public override string ToString() => Value?.ToString() ?? string.Empty;
}
