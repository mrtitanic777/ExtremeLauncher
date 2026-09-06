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
 * Ported from launcher/modplatform/helpers/ExportToModList.cpp.
 *
 * WRITING DOWN WHAT IS INSTALLED, in a form somebody else can read. Not a modpack -- a list, for a
 * forum post, a README, or the friend asking what you are running.
 *
 * FIVE BUILDERS, NOT ONE TEMPLATE. My first draft of this file built each line by substituting into a
 * per-format template string and then deleting the fields nobody asked for. It produced plausible
 * output and it was not upstream's, which appends field by field and SKIPS A FIELD THE MOD HAS NOT
 * GOT. The difference shows immediately: a mod with no url gets `Sodium [0.5.13]` here and
 * `Sodium () [0.5.13]` from a template with an empty substitution.
 *
 * Each format also escapes differently -- HTML entity-escapes, Markdown backslash-escapes eighteen
 * characters, CSV quotes and doubles, JSON is built as real JSON rather than printed -- and that is
 * not something a shared template can express.
 */

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeLauncher.ModPlatform;

/// <summary>The shapes a mod list can be written in.</summary>
public enum ModListFormat
{
    Html,
    Markdown,
    PlainText,
    Json,
    Csv,

    /// <summary>Whatever line template the user supplies.</summary>
    Custom,
}

/// <summary>Which fields to include, beyond the name.</summary>
[Flags]
public enum ModListFields
{
    None = 0,
    Authors = 1 << 0,
    Url = 1 << 1,
    Version = 1 << 2,
    FileName = 1 << 3,

    /// <summary>What the dialog starts with.</summary>
    Default = Authors | Url | Version,
}

/// <summary>One mod, as much of it as a list needs.</summary>
public sealed record ModListEntry(
    string Name,
    string Url = "",
    string Version = "",
    IReadOnlyList<string>? Authors = null,
    string FileName = "")
{
    public IReadOnlyList<string> AuthorList => Authors ?? [];

    public string AuthorsJoined => string.Join(", ", AuthorList);
}

public static class ExportToModList
{
    /// <summary>The example line the dialog shows for each format. Upstream's, verbatim.</summary>
    public static string ExampleLine(ModListFormat format) => format switch
    {
        ModListFormat.Html => "<li><a href=\"{url}\">{name}</a> [{version}] by {authors}</li>",
        ModListFormat.Markdown => "[{name}]({url}) [{version}] by {authors}",
        ModListFormat.PlainText => "{name} ({url}) [{version}] by {authors}",
        ModListFormat.Json => "{\"name\":\"{name}\",\"url\":\"{url}\",\"version\":\"{version}\",\"authors\":\"{authors}\"},",
        ModListFormat.Csv => "{name},{url},{version},\"{authors}\"",
        _ => "{name}",
    };

    public static string Render(
        IEnumerable<ModListEntry> mods,
        ModListFormat format,
        ModListFields fields = ModListFields.Default)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var list = mods.ToList();

        return format switch
        {
            ModListFormat.Html => ToHtml(list, fields),
            ModListFormat.Markdown => ToMarkdown(list, fields),
            ModListFormat.Json => ToJson(list, fields),
            ModListFormat.Csv => ToCsv(list, fields),
            _ => ToPlainText(list, fields),
        };
    }

    /// <summary>Renders with a caller-supplied line template.</summary>
    /// <remarks>
    /// The one place substitution IS the mechanism, because the user wrote the template and an empty
    /// field is theirs to deal with.
    /// </remarks>
    public static string Render(IEnumerable<ModListEntry> mods, string lineTemplate)
    {
        ArgumentNullException.ThrowIfNull(mods);

        return string.Join(
            "\n",
            mods.Select(mod => lineTemplate
                .Replace("{name}", mod.Name, StringComparison.Ordinal)
                .Replace("{url}", mod.Url, StringComparison.Ordinal)
                .Replace("{version}", mod.Version, StringComparison.Ordinal)
                .Replace("{authors}", mod.AuthorsJoined, StringComparison.Ordinal)
                .Replace("{filename}", mod.FileName, StringComparison.Ordinal)));
    }

    // ================================================================== the five builders

    private static string ToHtml(List<ModListEntry> mods, ModListFields fields)
    {
        var lines = new List<string>();

        foreach (var mod in mods)
        {
            var name = HtmlEscape(mod.Name);

            // The NAME becomes the link, rather than the url being appended. That is what makes an
            // HTML list worth having over a plain one.
            if (fields.HasFlag(ModListFields.Url) && mod.Url.Length != 0)
            {
                name = $"<a href=\"{HtmlEscape(mod.Url)}\">{name}</a>";
            }

            var line = new StringBuilder(name);

            if (fields.HasFlag(ModListFields.Version) && mod.Version.Length != 0)
            {
                line.Append(" [").Append(HtmlEscape(mod.Version)).Append(']');
            }

            if (fields.HasFlag(ModListFields.Authors) && mod.AuthorList.Count != 0)
            {
                line.Append(" by ").Append(HtmlEscape(mod.AuthorsJoined));
            }

            if (fields.HasFlag(ModListFields.FileName))
            {
                line.Append(" (").Append(HtmlEscape(mod.FileName)).Append(')');
            }

            lines.Add($"<li>{line}</li>");
        }

        // A whole document, which is what upstream emits -- a bare run of <li> is not a list.
        return $"<html><body><ul>\n\t{string.Join("\n\t", lines)}\n</ul></body></html>";
    }

    private static string ToMarkdown(List<ModListEntry> mods, ModListFields fields)
    {
        var lines = new List<string>();

        foreach (var mod in mods)
        {
            var name = MarkdownEscape(mod.Name);

            if (fields.HasFlag(ModListFields.Url) && mod.Url.Length != 0)
            {
                // The url is NOT escaped: it goes inside the link target, where the escapes would
                // become part of the address.
                name = $"[{name}]({mod.Url})";
            }

            var line = new StringBuilder(name);

            if (fields.HasFlag(ModListFields.Version) && mod.Version.Length != 0)
            {
                line.Append(" [").Append(MarkdownEscape(mod.Version)).Append(']');
            }

            if (fields.HasFlag(ModListFields.Authors) && mod.AuthorList.Count != 0)
            {
                line.Append(" by ").Append(MarkdownEscape(mod.AuthorsJoined));
            }

            if (fields.HasFlag(ModListFields.FileName))
            {
                line.Append(" (").Append(MarkdownEscape(mod.FileName)).Append(')');
            }

            lines.Add("- " + line);
        }

        return string.Join("\n", lines);
    }

    private static string ToPlainText(List<ModListEntry> mods, ModListFields fields)
    {
        var lines = new List<string>();

        foreach (var mod in mods)
        {
            var line = new StringBuilder(mod.Name);

            if (fields.HasFlag(ModListFields.Url) && mod.Url.Length != 0)
            {
                line.Append(" (").Append(mod.Url).Append(')');
            }

            if (fields.HasFlag(ModListFields.Version) && mod.Version.Length != 0)
            {
                line.Append(" [").Append(mod.Version).Append(']');
            }

            if (fields.HasFlag(ModListFields.Authors) && mod.AuthorList.Count != 0)
            {
                line.Append(" by ").Append(mod.AuthorsJoined);
            }

            if (fields.HasFlag(ModListFields.FileName))
            {
                line.Append(" (").Append(mod.FileName).Append(')');
            }

            lines.Add(line.ToString());
        }

        return string.Join("\n", lines);
    }

    /// <remarks>
    /// Built as real JSON rather than printed, so a mod named <c>"Sodium"</c> with a quote in it
    /// cannot produce a document nothing can parse. Authors are an ARRAY here, not a joined string --
    /// upstream's choice, and the right one for a machine-readable format.
    /// </remarks>
    private static string ToJson(List<ModListEntry> mods, ModListFields fields)
    {
        var array = new JsonArray();

        foreach (var mod in mods)
        {
            var entry = new JsonObject { ["name"] = mod.Name };

            if (fields.HasFlag(ModListFields.Url) && mod.Url.Length != 0)
            {
                entry["url"] = mod.Url;
            }

            if (fields.HasFlag(ModListFields.Version) && mod.Version.Length != 0)
            {
                entry["version"] = mod.Version;
            }

            if (fields.HasFlag(ModListFields.Authors) && mod.AuthorList.Count != 0)
            {
                var authors = new JsonArray();

                foreach (var author in mod.AuthorList)
                {
                    authors.Add(author);
                }

                entry["authors"] = authors;
            }

            if (fields.HasFlag(ModListFields.FileName))
            {
                entry["filename"] = mod.FileName;
            }

            array.Add(entry);
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ToCsv(List<ModListEntry> mods, ModListFields fields)
    {
        var lines = new List<string>();

        foreach (var mod in mods)
        {
            var cells = new List<string> { mod.Name };

            /*
             * A CELL IS EMITTED EVEN WHEN EMPTY, unlike every other format. A CSV whose rows have
             * different column counts is not a CSV, so "skip the field" cannot mean "skip the cell".
             */
            if (fields.HasFlag(ModListFields.Url))
            {
                cells.Add(mod.Url);
            }

            if (fields.HasFlag(ModListFields.Version))
            {
                cells.Add(mod.Version);
            }

            if (fields.HasFlag(ModListFields.Authors))
            {
                cells.Add(mod.AuthorsJoined);
            }

            if (fields.HasFlag(ModListFields.FileName))
            {
                cells.Add(mod.FileName);
            }

            lines.Add(string.Join(",", cells.Select(CsvCell)));
        }

        return string.Join("\n", lines);
    }

    // ================================================================== escaping

    private static string HtmlEscape(string text)
        => text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Upstream's set, in its order.</summary>
    private const string MarkdownSpecials = "\\`*_{}[]<>()#+-.!|";

    private static string MarkdownEscape(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (MarkdownSpecials.Contains(ch, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>Quotes a cell only when it needs it, doubling any quote inside.</summary>
    private static string CsvCell(string text)
        => text.Contains(',', StringComparison.Ordinal)
            || text.Contains('"', StringComparison.Ordinal)
            || text.Contains('\n', StringComparison.Ordinal)
                ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : text;
}
