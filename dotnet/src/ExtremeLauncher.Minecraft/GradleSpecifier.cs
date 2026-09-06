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
 * Ported from launcher/minecraft/GradleSpecifier.h.
 */

using System.Text.RegularExpressions;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

/// <summary>
/// A Maven/Gradle artifact coordinate, e.g. <c>org.gradle.test:service:1.0:jdk15@jar</c>.
/// </summary>
public sealed partial class GradleSpecifier : IEquatable<GradleSpecifier>
{
    private const string DefaultExtension = "jar";

    private readonly string _invalidValue = string.Empty;
    private readonly DefaultVariable<string> _extension = new(DefaultExtension);

    public GradleSpecifier()
    {
        IsValid = false;
    }

    public GradleSpecifier(string? value)
    {
        value ??= string.Empty;

        var match = SpecifierPattern().Match(value);
        IsValid = match.Success;

        if (!IsValid)
        {
            _invalidValue = value;
            return;
        }

        GroupId = match.Groups[1].Value;
        ArtifactId = match.Groups[2].Value;
        Version = match.Groups[3].Value;
        Classifier = match.Groups[4].Value;

        // The C++ checks lastCapturedIndex() >= 5, which for this pattern is equivalent to
        // asking whether the extension group participated at all.
        if (match.Groups[5].Success)
        {
            _extension.Set(match.Groups[5].Value);
        }
    }

    /*
       org.gradle.test.classifiers : service : 1.0 : jdk15 @ jar
        0 "org.gradle.test.classifiers:service:1.0:jdk15@jar"
        1 "org.gradle.test.classifiers"
        2 "service"
        3 "1.0"
        4 "jdk15"
        5 "jar"
    */
    [GeneratedRegex(@"\A(?:([^:@]+):([^:@]+):([^:@]+)(?::([^:@]+))?(?:@([^:@]+))?)\z")]
    private static partial Regex SpecifierPattern();

    public bool IsValid { get; }

    public string GroupId { get; } = string.Empty;

    public string ArtifactId { get; } = string.Empty;

    public string Version { get; } = string.Empty;

    public string Classifier { get; set; } = string.Empty;

    public string Extension => _extension.Value;

    public string ArtifactPrefix => $"{GroupId}:{ArtifactId}";

    public string Serialize()
    {
        if (!IsValid)
        {
            return _invalidValue;
        }

        var result = $"{GroupId}:{ArtifactId}:{Version}";

        if (Classifier.Length != 0)
        {
            result += $":{Classifier}";
        }

        // Explicit, not "differs from default": "...@jar" must round-trip verbatim.
        if (_extension.IsExplicit)
        {
            result += $"@{Extension}";
        }

        return result;
    }

    public string GetFileName()
    {
        if (!IsValid)
        {
            return string.Empty;
        }

        var fileName = $"{ArtifactId}-{Version}";

        if (Classifier.Length != 0)
        {
            fileName += $"-{Classifier}";
        }

        return $"{fileName}.{Extension}";
    }

    public string ToPath(string? fileNameOverride = null)
    {
        if (!IsValid)
        {
            return string.Empty;
        }

        var fileName = string.IsNullOrEmpty(fileNameOverride) ? GetFileName() : fileNameOverride;

        return $"{GroupId.Replace('.', '/')}/{ArtifactId}/{Version}/{fileName}";
    }

    public bool MatchName(GradleSpecifier other)
        => string.Equals(other.ArtifactId, ArtifactId, StringComparison.Ordinal)
           && string.Equals(other.GroupId, GroupId, StringComparison.Ordinal)
           && string.Equals(other.Classifier, Classifier, StringComparison.Ordinal);

    /// <remarks>
    /// Matches the C++ <c>operator==</c>, which compares the extension's *effective* value rather
    /// than its explicitness -- so <c>a:b:1.0</c> and <c>a:b:1.0@jar</c> compare equal even though
    /// they serialize differently.
    /// </remarks>
    public bool Equals(GradleSpecifier? other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(GroupId, other.GroupId, StringComparison.Ordinal)
               && string.Equals(ArtifactId, other.ArtifactId, StringComparison.Ordinal)
               && string.Equals(Version, other.Version, StringComparison.Ordinal)
               && string.Equals(Classifier, other.Classifier, StringComparison.Ordinal)
               && string.Equals(Extension, other.Extension, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is GradleSpecifier other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(GroupId),
            StringComparer.Ordinal.GetHashCode(ArtifactId),
            StringComparer.Ordinal.GetHashCode(Version),
            StringComparer.Ordinal.GetHashCode(Classifier),
            StringComparer.Ordinal.GetHashCode(Extension));

    public static bool operator ==(GradleSpecifier? left, GradleSpecifier? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(GradleSpecifier? left, GradleSpecifier? right) => !(left == right);

    public override string ToString() => Serialize();
}
