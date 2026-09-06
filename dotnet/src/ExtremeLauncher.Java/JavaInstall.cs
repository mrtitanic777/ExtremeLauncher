// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
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
 * Ported from launcher/java/JavaInstall.{h,cpp}.
 *
 * The BaseVersion base class and its dynamic_cast fallbacks are not carried over -- that hierarchy
 * exists to feed Qt's version-list models, which belong to the UI wave.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Java;

/// <summary>One discovered JVM: where it lives, what it calls itself, and what architecture it is.</summary>
public sealed class JavaInstall : IComparable<JavaInstall>, IEquatable<JavaInstall>
{
    public JavaInstall()
    {
    }

    public JavaInstall(string id, string arch, string path)
    {
        Id = id;
        Arch = arch;
        Path = path;
    }

    public string Id { get; set; } = string.Empty;

    public string Arch { get; set; } = "unknown";

    public string Path { get; set; } = string.Empty;

    public bool Recommended { get; set; }

    public JavaVersion Version => _version ??= new JavaVersion(Id);

    private JavaVersion? _version;

    public bool Equals(JavaInstall? other)
        => other is not null
           && string.Equals(Arch, other.Arch, StringComparison.Ordinal)
           && string.Equals(Id, other.Id, StringComparison.Ordinal)
           && string.Equals(Path, other.Path, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is JavaInstall other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(Arch),
            StringComparer.Ordinal.GetHashCode(Id),
            StringComparer.Ordinal.GetHashCode(Path));

    /// <remarks>Architecture first, then id, then path -- so 32-bit and 64-bit JVMs group together.</remarks>
    public int CompareTo(JavaInstall? other)
    {
        if (other is null)
        {
            return 1;
        }

        var archCompare = StringUtils.NaturalCompare(Arch, other.Arch, CaseSensitivity.CaseInsensitive);

        if (archCompare != 0)
        {
            return archCompare;
        }

        var idCompare = string.CompareOrdinal(Id, other.Id);

        if (idCompare != 0)
        {
            return idCompare;
        }

        return StringUtils.NaturalCompare(Path, other.Path, CaseSensitivity.CaseInsensitive);
    }

    public static bool operator <(JavaInstall? left, JavaInstall? right)
        => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(JavaInstall? left, JavaInstall? right)
        => left is not null && left.CompareTo(right) > 0;

    public static bool operator <=(JavaInstall? left, JavaInstall? right) => !(left > right);

    public static bool operator >=(JavaInstall? left, JavaInstall? right) => !(left < right);

    public static bool operator ==(JavaInstall? left, JavaInstall? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(JavaInstall? left, JavaInstall? right) => !(left == right);

    public override string ToString() => $"{Id} ({Arch}) at {Path}";
}
