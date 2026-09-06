// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * Ported from the Require half of launcher/meta/JsonFormat.h.
 *
 * RELOCATED, deliberately. Upstream declares this in meta/, but both VersionFile (minecraft/) and
 * MetaVersion (meta/) need it, and here Meta references Minecraft rather than the reverse. C++
 * headers have no such boundary; C# projects do, so it lives in the lower layer to avoid a cycle.
 */

namespace ExtremeLauncher.Minecraft;

/// <summary>
/// A dependency on another metadata package.
/// </summary>
/// <remarks>
/// QUIRK, preserved: equality and ordering are by <see cref="Uid"/> ALONE, so a set can hold only one
/// requirement per package -- adding a second for the same uid is a no-op rather than a conflict.
/// <see cref="DeepEquals"/> is the comparison that considers the version constraints.
/// </remarks>
public readonly struct Require : IEquatable<Require>, IComparable<Require>
{
    public Require(string uid, string equalsVersion = "", string suggests = "")
    {
        Uid = uid;
        EqualsVersion = equalsVersion;
        Suggests = suggests;
    }

    public string Uid { get; }

    /// <summary>An exact version this requirement pins to, if any.</summary>
    public string EqualsVersion { get; }

    /// <summary>A version to prefer when nothing else decides.</summary>
    public string Suggests { get; }

    public bool Equals(Require other) => string.Equals(Uid, other.Uid, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Require other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Uid ?? string.Empty);

    public int CompareTo(Require other) => string.CompareOrdinal(Uid, other.Uid);

    /// <summary>Compares all three fields, unlike <see cref="Equals(Require)"/>.</summary>
    public bool DeepEquals(Require other)
        => string.Equals(Uid, other.Uid, StringComparison.Ordinal)
           && string.Equals(EqualsVersion, other.EqualsVersion, StringComparison.Ordinal)
           && string.Equals(Suggests, other.Suggests, StringComparison.Ordinal);

    public static bool operator ==(Require left, Require right) => left.Equals(right);

    public static bool operator !=(Require left, Require right) => !left.Equals(right);

    public static bool operator <(Require left, Require right) => left.CompareTo(right) < 0;

    public static bool operator >(Require left, Require right) => left.CompareTo(right) > 0;

    public static bool operator <=(Require left, Require right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Require left, Require right) => left.CompareTo(right) >= 0;

    public override string ToString()
        => EqualsVersion.Length != 0 ? $"{Uid}=={EqualsVersion}" : Uid;
}

/// <summary>A set of requirements, keyed by uid.</summary>
public sealed class RequireSet : SortedSet<Require>
{
    public RequireSet()
    {
    }

    public RequireSet(IEnumerable<Require> items) : base(items)
    {
    }
}
