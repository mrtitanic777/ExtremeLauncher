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
 * Ported from launcher/modplatform/legacy_ftb/PackFetchTask.cpp and PackHelpers.h.
 *
 * The legacy Feed The Beast packs are the old, static FTB catalogue: two XML files (public and
 * third-party) list <modpack> entries, each an archive on the FTB CDN. Newer FTB packs come through the
 * FTB App import; this is the discoverable curated list from before that. Only the fetch-and-parse half
 * is here -- the discoverability layer, which is pure and testable. Installing a pack (downloading and
 * unpacking its archive into an instance) is a separate, heavier task.
 *
 * The parse rules are upstream's, quirks intact: oldVersions is a ";"-separated list; an empty entry in
 * it flags the pack "bugged" (still usable) and is dropped; a pack with no versions at all falls back
 * to its current version, or is flagged "broken" if it has none.
 */

using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.ModPlatform;

/// <summary>Which of the three legacy FTB lists a pack came from.</summary>
public enum LegacyFtbPackType
{
    Public,
    ThirdParty,
    Private,
}

/// <summary>One entry from a legacy FTB pack list.</summary>
public sealed class LegacyFtbModpack
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>Every published version, newest-last as the XML lists them.</summary>
    public List<string> OldVersions { get; set; } = [];

    public string CurrentVersion { get; set; } = string.Empty;

    public string McVersion { get; set; } = string.Empty;

    public string Mods { get; set; } = string.Empty;

    public string Logo { get; set; } = string.Empty;

    /// <summary>The archive's directory on the CDN.</summary>
    public string Dir { get; set; } = string.Empty;

    /// <summary>The archive file name (the XML calls this attribute "url").</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>An empty version entry was found and dropped; the pack is still usable.</summary>
    public bool Bugged { get; set; }

    /// <summary>No usable version at all; the pack cannot be installed.</summary>
    public bool Broken { get; set; }

    public LegacyFtbPackType Type { get; set; }

    /// <summary>The code a private pack was fetched under; empty for public/third-party packs.</summary>
    public string PackCode { get; set; } = string.Empty;
}

/// <summary>Parses a legacy FTB pack list XML into <see cref="LegacyFtbModpack"/> entries.</summary>
public static class LegacyFtbPackParser
{
    /// <summary>
    /// Parses the <c>&lt;modpack&gt;</c> entries out of one list. Returns false when the XML will not
    /// parse (upstream reports the whole list as failed), true otherwise -- including an empty list.
    /// </summary>
    public static bool TryParse(string xml, LegacyFtbPackType type, out List<LegacyFtbModpack> packs)
    {
        packs = [];

        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return false;
        }

        foreach (var element in document.Descendants("modpack"))
        {
            var pack = new LegacyFtbModpack
            {
                Name = Attribute(element, "name"),
                CurrentVersion = Attribute(element, "version"),
                McVersion = Attribute(element, "mcVersion"),
                Description = Attribute(element, "description"),
                Mods = Attribute(element, "mods"),
                Logo = Attribute(element, "logo"),
                Author = Attribute(element, "author"),
                Dir = Attribute(element, "dir"),
                File = Attribute(element, "url"),
                Type = type,
            };

            // ";"-separated, keeping empty parts as Qt's QString::split does -- because an empty part is
            // exactly what marks the pack "bugged" below.
            var versions = Attribute(element, "oldVersions").Split(';');
            var kept = versions.Where(v => v.Length != 0).ToList();

            if (kept.Count != versions.Length)
            {
                pack.Bugged = true;
            }

            pack.OldVersions = kept;

            if (pack.OldVersions.Count < 1)
            {
                if (pack.CurrentVersion.Length != 0)
                {
                    pack.OldVersions.Add(pack.CurrentVersion);
                }
                else
                {
                    pack.Broken = true;
                }
            }

            packs.Add(pack);
        }

        return true;
    }

    private static string Attribute(XElement element, string name)
        => element.Attribute(name)?.Value ?? string.Empty;
}

/// <summary>The public and third-party pack lists, and which of them failed to parse.</summary>
public sealed record LegacyFtbFetchResult(
    IReadOnlyList<LegacyFtbModpack> Public,
    IReadOnlyList<LegacyFtbModpack> ThirdParty,
    IReadOnlyList<string> FailedLists);

/// <summary>Fetches the legacy FTB pack lists from the CDN.</summary>
public sealed class LegacyFtbPackSource
{
    private readonly HttpClient _client;

    private readonly string _baseUrl;

    public LegacyFtbPackSource(HttpClient client, string? baseUrl = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _baseUrl = baseUrl is { Length: > 0 } ? baseUrl : BuildConfig.Instance.LegacyFtbCdnBaseUrl;
    }

    /// <summary>Downloads and parses the public and third-party lists.</summary>
    public async Task<LegacyFtbFetchResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        var (publicPacks, publicOk) =
            await FetchListAsync("static/modpacks.xml", LegacyFtbPackType.Public, cancellationToken)
                .ConfigureAwait(false);

        var (thirdPartyPacks, thirdPartyOk) =
            await FetchListAsync("static/thirdparty.xml", LegacyFtbPackType.ThirdParty, cancellationToken)
                .ConfigureAwait(false);

        List<string> failed = [];

        if (!publicOk)
        {
            failed.Add("Public Packs");
        }

        if (!thirdPartyOk)
        {
            failed.Add("Third Party Packs");
        }

        return new LegacyFtbFetchResult(publicPacks, thirdPartyPacks, failed);
    }

    /// <summary>Fetches a private pack list by its code, tagging each entry with that code.</summary>
    public async Task<IReadOnlyList<LegacyFtbModpack>> FetchPrivateAsync(
        string packCode, CancellationToken cancellationToken = default)
    {
        var (packs, _) = await FetchListAsync(
            $"static/{packCode}.xml", LegacyFtbPackType.Private, cancellationToken).ConfigureAwait(false);

        foreach (var pack in packs)
        {
            pack.PackCode = packCode;
        }

        return packs;
    }

    private async Task<(List<LegacyFtbModpack> Packs, bool Ok)> FetchListAsync(
        string relative, LegacyFtbPackType type, CancellationToken cancellationToken)
    {
        var xml = await _client.GetStringAsync(_baseUrl + relative, cancellationToken).ConfigureAwait(false);

        var ok = LegacyFtbPackParser.TryParse(xml, type, out var packs);

        return (packs, ok);
    }
}

/// <summary>
/// The set of private FTB pack codes the user has added, persisted one per line, ported from
/// legacy_ftb/PrivatePackManager.
/// </summary>
public sealed class LegacyFtbPrivatePacks
{
    private readonly string _filePath;

    private readonly HashSet<string> _codes = [];

    private bool _dirty;

    /// <param name="filePath">Where the codes are stored; upstream uses <c>private_packs.txt</c>.</param>
    public LegacyFtbPrivatePacks(string filePath)
        => _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

    public bool IsEmpty => _codes.Count == 0;

    /// <summary>The codes, sorted for a stable order (the set itself is unordered, as upstream's is).</summary>
    public IReadOnlyList<string> Codes => _codes.OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>Reads the codes from disk. A missing or unreadable file leaves the set empty, not an error.</summary>
    public void Load()
    {
        try
        {
            _codes.Clear();

            foreach (var line in File.ReadAllText(_filePath).Split('\n'))
            {
                if (line.Length != 0)
                {
                    _codes.Add(line);
                }
            }

            _dirty = false;
        }
        catch (IOException)
        {
            _codes.Clear();
        }
        catch (UnauthorizedAccessException)
        {
            _codes.Clear();
        }
    }

    /// <summary>Writes the codes back, but only when something changed since the last load or save.</summary>
    public void Save()
    {
        if (!_dirty)
        {
            return;
        }

        File.WriteAllText(_filePath, string.Join('\n', Codes));
        _dirty = false;
    }

    public void Add(string code)
    {
        if (_codes.Add(code))
        {
            _dirty = true;
        }
    }

    public void Remove(string code)
    {
        if (_codes.Remove(code))
        {
            _dirty = true;
        }
    }
}

/// <summary>The pure pieces of legacy_ftb/PackInstallTask: where a pack's archive lives, and how a
/// Forge-based pack names its loader.</summary>
public static class LegacyFtbInstall
{
    /// <summary>
    /// The CDN URL of a pack's archive for a given version. Upstream lays it out as
    /// <c>{dir}/{version with dots as underscores}/{file}</c> under <c>privatepacks/</c> for a private
    /// pack or <c>modpacks/</c> for a public or third-party one.
    /// </summary>
    public static string ArchiveUrl(LegacyFtbModpack pack, string version, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(version);

        var root = baseUrl is { Length: > 0 } ? baseUrl : BuildConfig.Instance.LegacyFtbCdnBaseUrl;
        var folder = pack.Type == LegacyFtbPackType.Private ? "privatepacks/" : "modpacks/";
        var path = $"{pack.Dir}/{version.Replace(".", "_", StringComparison.Ordinal)}/{pack.File}";

        return root + folder + path;
    }

    /// <summary>
    /// The <c>net.minecraftforge</c> component version an old FTB <c>pack.json</c> implies, or null when
    /// the pack is not Forge-based. Upstream reads the first library whose name is a Forge coordinate and
    /// strips the Minecraft version and dashes out of it (e.g. <c>1.20.1-47.1.0</c> → <c>47.1.0</c>).
    /// </summary>
    public static string? ForgeComponentVersion(string packJson, string mcVersion)
    {
        ArgumentNullException.ThrowIfNull(packJson);

        JsonNode? document;

        try
        {
            document = JsonNode.Parse(packJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (document is not JsonObject root || root["libraries"] is not JsonArray libraries)
        {
            return null;
        }

        foreach (var library in libraries)
        {
            if (library is not JsonObject obj || obj["name"] is not JsonValue nameValue)
            {
                continue;
            }

            var name = nameValue.ToString();

            if (!name.StartsWith("net.minecraftforge", StringComparison.Ordinal))
            {
                continue;
            }

            var forgeVersion = new GradleSpecifier(name).Version;

            return forgeVersion
                .Replace(mcVersion, string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal);
        }

        return null;
    }
}
