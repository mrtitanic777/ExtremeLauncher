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
 * Ported in behaviour from launcher/ui/java/VersionList.cpp.
 *
 * FINDING A JAVA TO INSTALL. `AutomaticJavaDownload` has been a registered, defaulted, and now
 * editable setting for many waves, and nothing has ever acted on it; `ArchiveDownloadTask` and
 * `ManifestDownloadTask` were ported and tested and nothing ever built one. What was missing between
 * them is this: the list of what exists.
 *
 * FOUR VENDORS, and the launcher offers all of them because they are not interchangeable:
 *
 *   net.minecraft.java   Mojang's own builds. What the game is tested against, so the default.
 *   net.adoptium.java    Temurin. The usual choice where Mojang publishes nothing for a platform.
 *   com.azul.java        Zulu. Notably the one with builds for platforms the others skip.
 *   com.ibm.java         Semeru.
 *
 * FILTERED TO THIS MACHINE. The metadata lists seven runtimeOS values per version -- linux-x64,
 * mac-os-arm64, windows-arm64 and so on -- and offering somebody a runtime for another architecture
 * produces a download that unpacks perfectly and cannot execute.
 *
 * THE VERSION DOCUMENT IS FETCHED DIRECTLY rather than through the component machinery, because it is
 * NOT a component document. A java version file carries a `runtimes` array of vendor builds where a
 * Minecraft version file carries libraries and a main class; running it through OneSixVersionFormat
 * would parse a document it was never meant to see and hand back an object with the interesting half
 * missing. The version LIST is loaded the ported way, because that part genuinely is the same shape.
 */

using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Java;
using ExtremeLauncher.Meta;

namespace ExtremeLauncher.Launch;

/// <summary>One installable Java runtime.</summary>
public sealed record InstallableJava(string PackageUid, string VersionName, JavaMetadata Runtime)
{
    /// <summary>What the vendor calls itself, for the list.</summary>
    public string VendorLabel => PackageUid switch
    {
        "net.minecraft.java" => "Mojang",
        "net.adoptium.java" => "Adoptium (Temurin)",
        "com.azul.java" => "Azul (Zulu)",
        "com.ibm.java" => "IBM (Semeru)",
        _ => PackageUid,
    };

    /// <summary>The major version, which is what compatibility is actually decided on.</summary>
    public int Major => Runtime.Version.Major;

    /// <summary>
    /// The version to show and to name a folder with.
    /// </summary>
    /// <remarks>
    /// ONLY MOJANG PUBLISHES A `name`. Azul, IBM and Adoptium give `major`, `minor` and `security` and
    /// nothing else, which was not obvious until the real metadata was read:
    ///
    ///     mojang   "version": { "major": 21, "minor": 0, "name": "21.0.7", "security": 7 }
    ///     azul     "version": { "major": 8,  "minor": 0, "security": 504 }
    ///
    /// Taking Name alone left every non-Mojang entry displayed as "Java  — Azul (Zulu)" and, far
    /// worse, gave them all the SAME folder name -- so installing two Azul runtimes would have had the
    /// second silently overwrite the first.
    /// </remarks>
    public string VersionLabel => Runtime.Version.Name.Length != 0
        ? Runtime.Version.Name
        : $"{Runtime.Version.Major}.{Runtime.Version.Minor}.{Runtime.Version.Security}";

    public string DisplayName => $"Java {VersionLabel} — {VendorLabel}";

    /// <summary>Whether this is a full JDK rather than a runtime.</summary>
    public bool IsJdk => Runtime.PackageType.Contains("jdk", StringComparison.OrdinalIgnoreCase);
}

public sealed class JavaRuntimeSource(LauncherPaths paths, HttpClient client, string metaUrl)
{
    private readonly MetaVersionListSource _versions = new(paths, client, metaUrl);

    /// <summary>The packages this launcher offers, Mojang first because it is the default.</summary>
    public static readonly string[] Packages =
    [
        "net.minecraft.java",
        "net.adoptium.java",
        "com.azul.java",
        "com.ibm.java",
    ];

    /// <summary>
    /// Every runtime that will actually run on this machine.
    /// </summary>
    /// <param name="runtimeOS">The platform string, or empty to use this machine's.</param>
    public async Task<IReadOnlyList<InstallableJava>> ListAsync(
        string runtimeOS = "",
        CancellationToken cancellationToken = default)
    {
        var wanted = runtimeOS.Length != 0 ? runtimeOS : SysInfo.SupportedJavaArchitecture();

        var found = new List<InstallableJava>();

        foreach (var package in Packages)
        {
            IReadOnlyList<MetaVersion> versions;

            try
            {
                versions = await _versions.LoadAsync(package, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is LauncherException or HttpRequestException)
            {
                /*
                 * ONE VENDOR BEING ABSENT IS NOT A FAILURE. Not every metadata server carries all
                 * four, and a launcher that refuses to offer Mojang's builds because IBM's list is
                 * missing has made a small problem into a total one.
                 */
                continue;
            }

            foreach (var version in versions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var detail = await LoadVersionDocumentAsync(package, version.VersionString, cancellationToken)
                    .ConfigureAwait(false);

                if (detail?["runtimes"] is not JsonArray runtimes)
                {
                    continue;
                }

                foreach (var entry in runtimes.OfType<JsonObject>())
                {
                    JavaMetadata runtime;

                    try
                    {
                        runtime = JavaMetadata.Parse(entry);
                    }
                    catch (Exception e) when (e is Core.JsonException or System.Text.Json.JsonException
                        or FormatException or InvalidOperationException)
                    {
                        continue;
                    }

                    if (!string.Equals(runtime.RuntimeOS, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // A JDK is offered as readily as a JRE: some people want one, and refusing it
                    // would be this launcher deciding something it has no business deciding.
                    found.Add(new InstallableJava(package, version.VersionString, runtime));
                }
            }
        }

        /*
         * Newest major first, because that is the order somebody looks in -- and within a major, the
         * newest build. Vendor order is the declaration order above, which puts Mojang's first.
         */
        /*
         * DEDUPLICATED BY CHECKSUM, falling back to the url where there is none.
         *
         * The checksum is the truer identity, and the real metadata proves it: com.ibm.java lists the
         * same java23 build under two different GitHub release tags --
         *
         *     .../jdk-23.0.2%2B7_openj9-0.49.0/ibm-semeru-open-jre_x64_windows_23.0.2_7_....zip
         *     .../jdk-23.0.1%2B11_openj9-0.48.0/ibm-semeru-open-jre_x64_windows_23.0.2_7_....zip
         *
         * -- with an IDENTICAL sha256. Deduplicating by url leaves both in the list, and a user
         * comparing two entries that differ in nothing they can see has been given a puzzle with no
         * answer. It also repeats some entries verbatim, which this catches either way.
         */
        return found
            .GroupBy(
                j => j.Runtime.ChecksumHash.Length != 0 ? j.Runtime.ChecksumHash : j.Runtime.Url,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(j => j.Major)
            .ThenBy(j => Array.IndexOf(Packages, j.PackageUid))
            .ThenByDescending(j => j.Runtime.Version.Minor)
            .ThenByDescending(j => j.Runtime.Version.Security)
            .ToArray();
    }

    /// <summary>Fetches one java version document, or null when it cannot be had.</summary>
    private async Task<JsonObject?> LoadVersionDocumentAsync(
        string package,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{metaUrl.TrimEnd('/')}/{package}/{version}.json";

            var body = await client.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            return JsonNode.Parse(body) as JsonObject;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException
            or TaskCanceledException or UriFormatException)
        {
            // One version being unreadable must not lose the rest of the list.
            return null;
        }
    }

    /// <summary>Builds the task that installs one, into the launcher's own java folder.</summary>
    /// <remarks>
    /// The two download types are NOT interchangeable and the metadata says which: "archive" is a
    /// tarball to unpack, "manifest" is Mojang's per-file listing that has to be walked. Choosing
    /// wrongly produces a download that succeeds and leaves nothing runnable behind.
    /// </remarks>
    public JavaDownloadTask CreateInstallTask(InstallableJava java)
    {
        ArgumentNullException.ThrowIfNull(java);

        var target = FileSystem.PathCombine(paths.Java, FolderNameFor(java));

        var url = new Uri(java.Runtime.Url);

        return java.Runtime.DownloadType == DownloadType.Manifest
            ? new ManifestDownloadTask(client, url, target, java.Runtime.ChecksumType, java.Runtime.ChecksumHash)
            : new ArchiveDownloadTask(
                client,
                url,
                target,
                FileSystem.PathCombine(paths.Root, "cache"),
                java.Runtime.ChecksumType,
                java.Runtime.ChecksumHash);
    }

    /// <summary>
    /// Where an installed runtime goes.
    /// </summary>
    /// <remarks>
    /// The vendor is in the name because two vendors' Java 21 are different things, and a folder
    /// called "java21" that could be either is one somebody has to open to identify.
    ///
    /// A SHORT CHECKSUM SUFFIX, because a vendor and a version STILL do not identify a build. The real
    /// metadata carries two different com.ibm.java runtimes both calling themselves 25.0.2 for
    /// windows-x64, with different downloads:
    ///
    ///     semeru-open-jre_x64_windows_25.0.2.1.zip
    ///     ..._x64_windows_25.0.2_10_openj9-0.57.0.zip
    ///
    /// Without the suffix the second install silently overwrites the first, and the launcher then
    /// points at a runtime that is not the one recorded. Deterministic, so reinstalling the same build
    /// reuses its folder rather than accumulating copies.
    /// </remarks>
    public static string FolderNameFor(InstallableJava java)
    {
        ArgumentNullException.ThrowIfNull(java);

        var vendor = java.Runtime.Vendor.Length != 0 ? java.Runtime.Vendor : java.PackageUid;

        /*
         * The full version, NOT Version.Name -- which only Mojang publishes. Every Azul runtime would
         * otherwise land in a folder called "azul-", and installing a second would overwrite the first
         * with no warning at all.
         */
        var discriminator = java.Runtime.ChecksumHash.Length >= 7
            ? java.Runtime.ChecksumHash[..7]
            : Math.Abs(StringComparer.Ordinal.GetHashCode(java.Runtime.Url)).ToString("x7", CultureInfo.InvariantCulture);

        var name = $"{vendor}-{java.VersionLabel}-{discriminator}";

        // Anything a filesystem would refuse, replaced rather than left to fail at extraction time.
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '-');
        }

        return name;
    }
}
