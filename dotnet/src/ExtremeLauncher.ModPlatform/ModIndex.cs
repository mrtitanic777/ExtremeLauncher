// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2022 flowln <flowlnlnln@gmail.com>
 *  Copyright (c) 2023 Trial97 <alexandru.tripon97@gmail.com>
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
 * Ported from launcher/modplatform/ModIndex.{h,cpp}.
 *
 * THE SHARED VOCABULARY. CurseForge and Modrinth describe the same world in different words; every
 * provider parses into the types here, and everything downstream -- the mod browser, update checks,
 * pack import, packwiz metadata -- speaks only these. Adding a third provider means writing one
 * parser, not touching the rest of the launcher.
 *
 * WHY THE IDS ARE STRINGS. Upstream uses QVariant, because Modrinth ids are strings ("P7dR8mSH") and
 * CurseForge ids are integers (306612). QVariant papers over that at the cost of every comparison
 * being a runtime question. Strings are the honest common type: an integer id round-trips through one
 * exactly, and every use is equality or interpolation into a URL.
 *
 * There turns out to be no place the distinction survives to. CurseForge documents these fields as
 * integers, but upstream sends them as JSON STRINGS in every POST body -- ids and murmur2 fingerprints
 * alike -- and CurseForge accepts them. See FlameApi's body builders, which do the same.
 */

using System.Globalization;

namespace ExtremeLauncher.ModPlatform;

/// <summary>Which mod loader a resource targets.</summary>
/// <remarks>
/// Flags, because a single mod may declare several. Upstream's Q_DECLARE_FLAGS with explicit
/// <c>1 &lt;&lt; n</c> values -- the numbers are stored in packwiz files, so they are part of the
/// on-disk format and cannot be renumbered.
/// </remarks>
[Flags]
public enum ModLoaderTypes
{
    None = 0,
    NeoForge = 1 << 0,
    Forge = 1 << 1,
    Cauldron = 1 << 2,
    LiteLoader = 1 << 3,
    Fabric = 1 << 4,
    Quilt = 1 << 5,
}

/// <summary>Where a resource came from.</summary>
public enum ResourceProvider
{
    /// <summary>CurseForge. Named "Flame" throughout upstream, and spelled "curseforge" on disk.</summary>
    Flame,

    Modrinth,
}

/// <summary>What kind of thing a resource is.</summary>
public enum ResourceType
{
    Mod,
    ResourcePack,
    ShaderPack,
    Modpack,
}

/// <summary>How one resource relates to another it names.</summary>
public enum DependencyType
{
    Required,
    Optional,
    Incompatible,

    /// <summary>Shipped inside the file itself, so nothing needs downloading.</summary>
    Embedded,

    Tool,
    Include,
    Unknown,
}

/// <summary>How stable a release claims to be.</summary>
/// <remarks>
/// Ordered Release &lt; Beta &lt; Alpha &lt; Unknown, and upstream compares these with the full set of
/// relational operators -- "at least as stable as" is a filter the mod browser offers. The numbering
/// starts at 1 to match upstream exactly, because it is compared, not just switched on.
/// </remarks>
public enum VersionType
{
    Release = 1,
    Beta,
    Alpha,
    Unknown,
}

/// <summary>Names for the enums, in the spelling the provider APIs use.</summary>
public static class ProviderCapabilities
{
    /// <summary>The provider's own name for itself, as it appears in API paths and packwiz files.</summary>
    public static string Name(ResourceProvider provider)
        => provider switch
        {
            ResourceProvider.Modrinth => "modrinth",
            ResourceProvider.Flame => "curseforge",
            _ => string.Empty,
        };

    /// <summary>The name to show a user.</summary>
    public static string ReadableName(ResourceProvider provider)
        => provider switch
        {
            ResourceProvider.Modrinth => "Modrinth",
            ResourceProvider.Flame => "CurseForge",
            _ => string.Empty,
        };

    /// <summary>
    /// The hashes this provider can be asked about, best first.
    /// </summary>
    /// <remarks>
    /// ORDER IS THE POINT. A file is identified to the provider by hashing it, and hashing a large jar
    /// is not free, so the caller tries the first algorithm and only falls back. CurseForge's list ends
    /// in murmur2 -- a non-cryptographic hash over the file with whitespace stripped -- which is
    /// legacy, slow to get right, and last for both reasons.
    /// </remarks>
    public static IReadOnlyList<string> HashTypes(ResourceProvider provider)
        => provider switch
        {
            ResourceProvider.Modrinth => ["sha512", "sha1"],
            ResourceProvider.Flame => ["sha1", "md5", "murmur2"],
            _ => [],
        };
}

public static class ModIndex
{
    /*
     * Upstream keeps this as a QMap and reads it backwards (QMap::key) to turn an enum into a string,
     * which is a linear scan returning the first match. Two switches say the same thing without the
     * container, and without the "which direction is this lookup" question at every call site.
     */

    /// <summary>The provider's spelling of a version type, or "unknown".</summary>
    public static string ToString(VersionType type)
        => type switch
        {
            VersionType.Release => "release",
            VersionType.Beta => "beta",
            VersionType.Alpha => "alpha",
            _ => "unknown",
        };

    /// <summary>Anything unrecognised, including an empty string, is Unknown.</summary>
    public static VersionType VersionTypeFromString(string type)
        => type switch
        {
            "release" => VersionType.Release,
            "beta" => VersionType.Beta,
            "alpha" => VersionType.Alpha,
            _ => VersionType.Unknown,
        };

    /// <summary>The loader's spelling on the wire, or an empty string.</summary>
    /// <remarks>
    /// Single values only. A combination has no single name, and upstream's switch falls through to
    /// "" for one -- silently, which is why <see cref="HasSingleModLoader"/> exists to ask first.
    /// </remarks>
    public static string ToString(ModLoaderTypes type)
        => type switch
        {
            ModLoaderTypes.NeoForge => "neoforge",
            ModLoaderTypes.Forge => "forge",
            ModLoaderTypes.Cauldron => "cauldron",
            ModLoaderTypes.LiteLoader => "liteloader",
            ModLoaderTypes.Fabric => "fabric",
            ModLoaderTypes.Quilt => "quilt",
            _ => string.Empty,
        };

    /// <summary>Anything unrecognised is <see cref="ModLoaderTypes.None"/>.</summary>
    public static ModLoaderTypes ModLoaderFromString(string type)
        => type switch
        {
            "neoforge" => ModLoaderTypes.NeoForge,
            "forge" => ModLoaderTypes.Forge,
            "cauldron" => ModLoaderTypes.Cauldron,
            "liteloader" => ModLoaderTypes.LiteLoader,
            "fabric" => ModLoaderTypes.Fabric,
            "quilt" => ModLoaderTypes.Quilt,
            _ => ModLoaderTypes.None,
        };

    /// <summary>Whether exactly one loader is set.</summary>
    /// <remarks>
    /// The <c>x &amp;&amp; !(x &amp; (x - 1))</c> trick from upstream, which is "non-zero and a power of
    /// two". Callers use it before asking for a name, since a combination has none.
    /// </remarks>
    public static bool HasSingleModLoader(ModLoaderTypes loaders)
    {
        var x = (int)loaders;

        return x != 0 && (x & (x - 1)) == 0;
    }

    /// <summary>The page a user would open to read about this project.</summary>
    /// <remarks>
    /// CurseForge's is a redirect: /projects/&lt;id&gt; resolves to the real slug-based URL. Upstream
    /// uses it because the id is the only thing always known, and the same is true here.
    /// </remarks>
    public static string GetMetaUrl(ResourceProvider provider, string projectId)
        => (provider == ResourceProvider.Flame
            ? "https://www.curseforge.com/projects/"
            : "https://modrinth.com/mod/") + projectId;

    /// <summary>
    /// Mods whose dependency on a loader's API must be substituted rather than followed.
    /// </summary>
    /// <remarks>
    /// Quilt can run Fabric mods, so a Fabric mod installed on Quilt asks for Fabric API -- which is
    /// the wrong package there, and installing it alongside QSL breaks the pack. The table maps the
    /// Fabric project id to the Quilt one for the two libraries that matter. It is upstream's data,
    /// verbatim, ids included: they identify real projects, so they cannot be tidied.
    /// </remarks>
    public static IReadOnlyList<OverrideDependency> GetOverrideDependencies() =>
    [
        new("634179", "306612", "API", ResourceProvider.Flame),
        new("720410", "308769", "KotlinLibraries", ResourceProvider.Flame),
        new("qvIfYCYJ", "P7dR8mSH", "API", ResourceProvider.Modrinth),
        new("lwVhp9o5", "Ha28R6CL", "KotlinLibraries", ResourceProvider.Modrinth),
    ];

    /// <summary>
    /// Substitutes a loader's API dependency for the right one when Fabric and Quilt disagree. Ported
    /// from GetModDependenciesTask::getOverride.
    /// </summary>
    /// <remarks>
    /// On Quilt, a mod that asks for Fabric API is redirected to QSL; on Fabric, one that asks for the
    /// Quilt package is redirected back to Fabric API. Quilt wins when both loader bits are set, matching
    /// upstream. A dependency that matches no override, or a loader set that is neither Fabric nor Quilt,
    /// is returned unchanged. A substituted dependency keeps its <see cref="Dependency.Type"/> but drops
    /// its version, exactly as upstream constructs it.
    /// </remarks>
    public static Dependency ApplyLoaderOverride(Dependency dependency, ResourceProvider provider, ModLoaderTypes loaders)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        var isQuilt = loaders.HasFlag(ModLoaderTypes.Quilt);

        if (!isQuilt && !loaders.HasFlag(ModLoaderTypes.Fabric))
        {
            return dependency;
        }

        foreach (var over in GetOverrideDependencies())
        {
            // On Quilt we look for the mod's Fabric-API request and hand back Quilt's; on Fabric, the
            // reverse.
            var lookFor = isQuilt ? over.Fabric : over.Quilt;

            if (over.Provider == provider && dependency.AddonId == lookFor)
            {
                return new Dependency
                {
                    AddonId = isQuilt ? over.Quilt : over.Fabric,
                    Type = dependency.Type,
                };
            }
        }

        return dependency;
    }
}

/// <summary>A Fabric project id and the Quilt project that replaces it.</summary>
/// <param name="Quilt">The project to install instead.</param>
/// <param name="Fabric">The project the mod actually asked for.</param>
/// <param name="Slug">Which library this is, for messages.</param>
public readonly record struct OverrideDependency(
    string Quilt,
    string Fabric,
    string Slug,
    ResourceProvider Provider);

public sealed class ModpackAuthor
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

public sealed class DonationData
{
    public string Id { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

/// <summary>Another resource this one names.</summary>
public sealed class Dependency
{
    public string AddonId { get; set; } = string.Empty;

    public DependencyType Type { get; set; } = DependencyType.Unknown;

    public string Version { get; set; } = string.Empty;
}

/// <summary>One downloadable file of one project.</summary>
public sealed class IndexedVersion
{
    public string AddonId { get; set; } = string.Empty;

    public string FileId { get; set; } = string.Empty;

    /// <summary>The display name of the release.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The comparable version, where the provider supplies one separately.</summary>
    public string VersionNumber { get; set; } = string.Empty;

    public VersionType VersionType { get; set; } = VersionType.Unknown;

    /// <summary>Every Minecraft version this file declares support for.</summary>
    public List<string> McVersion { get; } = [];

    public string DownloadUrl { get; set; } = string.Empty;

    public string Date { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public ModLoaderTypes Loaders { get; set; } = ModLoaderTypes.None;

    public string HashType { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Whether the provider would pick this file.
    /// </summary>
    /// <remarks>
    /// Defaults to true, as upstream does. CurseForge marks some files unusable -- a broken upload
    /// left in place because something already references it -- and only those come back false.
    /// </remarks>
    public bool IsPreferred { get; set; } = true;

    public string Changelog { get; set; } = string.Empty;

    public List<Dependency> Dependencies { get; } = [];

    /// <summary>Client, server or both. CurseForge only.</summary>
    public string Side { get; set; } = string.Empty;

    /// <summary>UI state, not provider data: whether the user ticked this version.</summary>
    public bool IsCurrentlySelected { get; set; }
}

/// <summary>The project details a listing does not carry, fetched separately.</summary>
public sealed class ExtraPackData
{
    public List<DonationData> Donate { get; } = [];

    public string IssuesUrl { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string WikiUrl { get; set; } = string.Empty;

    public string DiscordUrl { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>The long description, in Markdown.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>One project, as a provider describes it.</summary>
public sealed class IndexedPack
{
    public string AddonId { get; set; } = string.Empty;

    public ResourceProvider Provider { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<ModpackAuthor> Authors { get; } = [];

    public string LogoName { get; set; } = string.Empty;

    public string LogoUrl { get; set; } = string.Empty;

    public string WebsiteUrl { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    /// <summary>
    /// Whether <see cref="Versions"/> has been filled in.
    /// </summary>
    /// <remarks>
    /// A search result carries no versions -- fetching them for every hit would be dozens of requests
    /// for a list the user is still scrolling. The flag distinguishes "no versions" from "not asked
    /// yet", which an empty list cannot.
    /// </remarks>
    public bool VersionsLoaded { get; set; }

    public List<IndexedVersion> Versions { get; } = [];

    /// <summary>
    /// Whether <see cref="ExtraData"/> has been filled in.
    /// </summary>
    /// <remarks>
    /// Defaults to TRUE, unlike <see cref="VersionsLoaded"/>. Upstream's comment explains it: not every
    /// provider has this data, so the default has to mean "there is nothing more coming" or the UI
    /// waits forever on a provider that will never answer. A provider that does supply it clears the
    /// flag when it creates the pack and sets it again once loaded.
    /// </remarks>
    public bool ExtraDataLoaded { get; set; } = true;

    public ExtraPackData ExtraData { get; set; } = new();

    /// <summary>Whether the version at this index is ticked. False while versions are unloaded.</summary>
    public bool IsVersionSelected(int index)
        => VersionsLoaded && index >= 0 && index < Versions.Count && Versions[index].IsCurrentlySelected;

    /// <summary>Whether any version is ticked. False while versions are unloaded.</summary>
    public bool IsAnyVersionSelected()
        => VersionsLoaded && Versions.Exists(v => v.IsCurrentlySelected);
}

/// <summary>One entry of a provider's category list, used to filter searches.</summary>
public sealed class Category
{
    public string Name { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;
}

/// <summary>Formatting helpers shared by the provider parsers.</summary>
internal static class IdFormat
{
    /// <summary>
    /// Renders a JSON id as the string this vocabulary stores.
    /// </summary>
    /// <remarks>
    /// CurseForge sends numbers, Modrinth sends strings, and both mean "the id". Invariant formatting
    /// matters: a locale that groups digits would turn 306612 into "306,612" and every later lookup
    /// would miss.
    /// </remarks>
    public static string FromJson(System.Text.Json.Nodes.JsonNode? node)
        => node?.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => node.GetValue<long>().ToString(CultureInfo.InvariantCulture),
            System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
            _ => string.Empty,
        };
}
