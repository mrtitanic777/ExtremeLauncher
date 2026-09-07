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
 * This file incorporates work covered by the following copyright and permission notice:
 *      Copyright 2013-2021 MultiMC Contributors
 *      Licensed under the Apache License, Version 2.0.
 *
 * Ported from launcher/ui/themes/CatPack.cpp (JsonCatPack). The launcher shows a cat picture behind the
 * instance list; a "cat pack" is a folder with an index.json that names a default image and a set of
 * date-ranged variants (a Christmas cat, a birthday cat, and so on). Only the date-selection logic is
 * ported here -- it is pure and testable; the embedded-resource BasicCatPack path and the theme wiring
 * belong to the UI and are not part of this.
 *
 * THE CONTRACT is tests/CatPack_test.cpp: the FIRST variant in file order whose range contains the day
 * wins, a range whose start falls after its end wraps around the new year, and anything unmatched falls
 * back to the default.
 */

using System.Text.Json.Nodes;

namespace ExtremeLauncher.Core;

/// <summary>
/// A cat pack loaded from an <c>index.json</c>: a default image and date-ranged variants.
/// </summary>
public sealed class JsonCatPack
{
    /// <summary>A day in the year with no year of its own, as the manifest stores range ends.</summary>
    public readonly record struct PartialDate(int Month, int Day);

    /// <summary>One dated image: shown when the day falls within [<see cref="Start"/>, <see cref="End"/>].</summary>
    public sealed record Variant(string Path, PartialDate Start, PartialDate End);

    private readonly string _defaultPath;

    private readonly List<Variant> _variants = [];

    /// <param name="manifestPath">Absolute path to the pack's <c>index.json</c>.</param>
    public JsonCatPack(string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(manifestPath)) ?? string.Empty;

        // The id upstream uses is the pack directory's name; the default image and every variant path
        // are resolved against that same directory.
        Id = new DirectoryInfo(directory).Name;

        var root = Json.RequireObject(Json.RequireDocumentFromFile(manifestPath, "CatPack JSON file"));

        Name = Json.RequireString(root, "name", "Catpack name");
        _defaultPath = FileSystem.PathCombine(directory, Json.RequireString(root, "default", "Default Cat"));

        foreach (var entry in Json.EnsureArray(root, "variants", null, "Catpack Variants"))
        {
            var variant = Json.RequireObjectValue(entry, "Cat variant");

            _variants.Add(new Variant(
                FileSystem.PathCombine(directory, Json.RequireString(variant, "path", "Variant path")),
                PartialDateOf(Json.RequireObject(variant, "startTime", "Variant startTime")),
                PartialDateOf(Json.RequireObject(variant, "endTime", "Variant endTime"))));
        }
    }

    /// <summary>The pack directory's name, as upstream keys it.</summary>
    public string Id { get; }

    /// <summary>The human name from the manifest.</summary>
    public string Name { get; }

    /// <summary>The image to show today.</summary>
    public string Path() => Path(DateOnly.FromDateTime(DateTime.Now));

    /// <summary>The image to show on <paramref name="now"/>.</summary>
    public string Path(DateOnly now)
    {
        foreach (var variant in _variants)
        {
            var start = EnsureDay(now.Year, variant.Start.Month, variant.Start.Day);
            var end = EnsureDay(now.Year, variant.End.Month, variant.End.Day);

            // A range that starts after it ends spans the new year. Move whichever end keeps the day in
            // view: if the end is already behind us, the range runs into next year; otherwise the start
            // began last year.
            if (start > end)
            {
                if (end < now)
                {
                    end = end.AddYears(1);
                }
                else
                {
                    start = start.AddYears(-1);
                }
            }

            if (start <= now && now <= end)
            {
                return variant.Path;
            }
        }

        return DefaultForDay(now);
    }

    /// <summary>Clamps month to 1..12 and day to 1..31, as upstream's partialDate() does.</summary>
    private static PartialDate PartialDateOf(JsonObject date)
    {
        var month = Math.Clamp(Json.EnsureInteger(date, "month", 1), 1, 12);
        var day = Math.Clamp(Json.EnsureInteger(date, "day", 1), 1, 31);

        return new PartialDate(month, day);
    }

    /// <summary>A real date, with the day pulled back to the last of the month when it overshoots.</summary>
    private static DateOnly EnsureDay(int year, int month, int day)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);

        return new DateOnly(year, month, Math.Min(day, daysInMonth));
    }

    /// <summary>
    /// The default image: the file as named, or -- when the default points at a directory -- one of the
    /// images inside it, chosen by the day of the year (or at random for a directory called "random").
    /// </summary>
    private string DefaultForDay(DateOnly now)
    {
        if (!Directory.Exists(_defaultPath))
        {
            return _defaultPath;
        }

        string[] patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"];

        var files = patterns
            .SelectMany(pattern => Directory.EnumerateFiles(_defaultPath, pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            return string.Empty;
        }

        var isRandom = string.Equals(
            new DirectoryInfo(_defaultPath).Name, "random", StringComparison.OrdinalIgnoreCase);

        var index = isRandom
            ? Random.Shared.Next(files.Count)
            : (now.DayOfYear - 1) % files.Count;

        return FileSystem.CleanPath(System.IO.Path.GetFullPath(files[index]));
    }
}
