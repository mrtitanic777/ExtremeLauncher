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
 * Ported from launcher/java/JavaVersion.{h,cpp}.
 *
 * Java has had two version schemes. The old one (through Java 8) reads 1.MAJOR.MINOR_SECURITY, so
 * "1.6.0_33" is Java 6 update 33; the new one (JEP 223, Java 9 onward) reads MAJOR.MINOR.SECURITY.
 * Upstream picks the pattern by testing for a leading "1.", and that is what makes "1.6.0_33" and
 * "6.0.33" compare equal -- a case the ported tests pin.
 */

using System.Globalization;
using System.Text.RegularExpressions;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Java;

public sealed partial class JavaVersion : IComparable<JavaVersion>, IEquatable<JavaVersion>
{
    public JavaVersion()
    {
    }

    public JavaVersion(string? versionString) => Parse(versionString ?? string.Empty);

    public JavaVersion(int major, int minor, int security, int build = 0, string name = "")
    {
        Major = major;
        Minor = minor;
        Security = security;
        Name = name;
        IsParseable = true;

        var parts = new List<string>();

        if (build != 0)
        {
            Build = build.ToString(CultureInfo.InvariantCulture);
            parts.Insert(0, Build);
        }

        if (Security != 0)
        {
            parts.Insert(0, Security.ToString(CultureInfo.InvariantCulture));
        }
        else if (parts.Count != 0)
        {
            parts.Insert(0, "0");
        }

        if (Minor != 0)
        {
            parts.Insert(0, Minor.ToString(CultureInfo.InvariantCulture));
        }
        else if (parts.Count != 0)
        {
            parts.Insert(0, "0");
        }

        parts.Insert(0, Major.ToString(CultureInfo.InvariantCulture));

        _string = string.Join('.', parts);
    }

    private string _string = string.Empty;

    public int Major { get; private set; }

    public int Minor { get; private set; }

    public int Security { get; private set; }

    /// <summary>The prerelease component, e.g. "ea" or "rc2". Upstream calls this <c>build()</c>.</summary>
    public string Build { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsParseable { get; private set; }

    /// <summary>PermGen was removed in Java 8; anything older (or unparseable) still needs it.</summary>
    public bool RequiresPermGen => !IsParseable || Major < 8;

    /// <summary>UTF-8 became the default charset in Java 18 (JEP 400).</summary>
    public bool DefaultsToUtf8 => IsParseable && Major >= 18;

    /// <summary>The module system arrived in Java 9.</summary>
    public bool IsModular => IsParseable && Major >= 9;

    public override string ToString() => _string;

    /// <remarks>
    /// The patterns are unanchored, exactly as upstream leaves them, so a version embedded in a longer
    /// string still parses. Preserved rather than tightened: callers feed this the output of
    /// `java -version`, which is not always a bare version string.
    /// </remarks>
    private void Parse(string versionString)
    {
        _string = versionString;

        var pattern = versionString.StartsWith("1.", StringComparison.Ordinal)
            ? LegacyPattern()
            : ModernPattern();

        var match = pattern.Match(_string);

        IsParseable = match.Success;
        Major = CapturedInteger(match, "major");
        Minor = CapturedInteger(match, "minor");
        Security = CapturedInteger(match, "security");
        Build = match.Groups["prerelease"].Value;
    }

    private static int CapturedInteger(Match match, string name)
    {
        var value = match.Groups[name].Value;

        return value.Length == 0 || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? 0
            : parsed;
    }

    /// <summary>Pre-JEP-223: <c>1.MAJOR.MINOR_SECURITY</c>.</summary>
    [GeneratedRegex("1[.](?<major>[0-9]+)([.](?<minor>[0-9]+))?(_(?<security>[0-9]+)?)?(-(?<prerelease>[a-zA-Z0-9]+))?")]
    private static partial Regex LegacyPattern();

    /// <summary>JEP 223 onward: <c>MAJOR.MINOR.SECURITY</c>.</summary>
    [GeneratedRegex("(?<major>[0-9]+)([.](?<minor>[0-9]+))?([.](?<security>[0-9]+))?(-(?<prerelease>[a-zA-Z0-9]+))?")]
    private static partial Regex ModernPattern();

    public bool Equals(JavaVersion? other)
    {
        if (other is null)
        {
            return false;
        }

        if (IsParseable && other.IsParseable)
        {
            return Major == other.Major
                   && Minor == other.Minor
                   && Security == other.Security
                   && string.Equals(Build, other.Build, StringComparison.Ordinal);
        }

        return string.Equals(_string, other._string, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is JavaVersion other && Equals(other);

    public override int GetHashCode()
        => IsParseable
            ? HashCode.Combine(Major, Minor, Security, StringComparer.Ordinal.GetHashCode(Build))
            : StringComparer.Ordinal.GetHashCode(_string);

    public int CompareTo(JavaVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (this < other)
        {
            return -1;
        }

        return Equals(other) ? 0 : 1;
    }

    public static bool operator <(JavaVersion? left, JavaVersion? right)
    {
        if (left is null)
        {
            return right is not null;
        }

        if (right is null)
        {
            return false;
        }

        if (!left.IsParseable || !right.IsParseable)
        {
            return StringUtils.NaturalCompare(left._string, right._string, CaseSensitivity.CaseSensitive) < 0;
        }

        if (left.Major != right.Major)
        {
            return left.Major < right.Major;
        }

        if (left.Minor != right.Minor)
        {
            return left.Minor < right.Minor;
        }

        if (left.Security != right.Security)
        {
            return left.Security < right.Security;
        }

        // Everything else being equal, a prerelease sorts below the final release.
        var leftPre = left.Build.Length != 0;
        var rightPre = right.Build.Length != 0;

        if (leftPre && !rightPre)
        {
            return true;
        }

        if (!leftPre && rightPre)
        {
            return false;
        }

        if (leftPre && rightPre)
        {
            // Natural, not ordinal: "rc5" must sort below "rc20".
            return StringUtils.NaturalCompare(left.Build, right.Build, CaseSensitivity.CaseSensitive) < 0;
        }

        return false;
    }

    public static bool operator >(JavaVersion? left, JavaVersion? right) => !(left < right) && !(left == right);

    public static bool operator <=(JavaVersion? left, JavaVersion? right) => left < right || left == right;

    public static bool operator >=(JavaVersion? left, JavaVersion? right) => !(left < right);

    public static bool operator ==(JavaVersion? left, JavaVersion? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(JavaVersion? left, JavaVersion? right) => !(left == right);
}
