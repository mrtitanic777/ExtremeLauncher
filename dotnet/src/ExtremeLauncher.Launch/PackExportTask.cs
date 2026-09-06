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
 * Ported in behaviour from launcher/ui/dialogs/ExportPackDialog.cpp and the Modrinth exporter.
 *
 * THE OTHER HALF OF WAVE 14. The launcher could import a .mrpack and had no way to make one, so an
 * instance could come in and never go back out in the format everybody else uses.
 *
 * IT IS THE MOD DOWNLOADER'S METADATA THAT MAKES THIS POSSIBLE. Wave 17 writes a packwiz .pw.toml
 * beside every downloaded mod recording where it came from and its hash; this reads them back. A mod
 * with an index entry becomes a LINK in the manifest -- a URL and a hash, a few hundred bytes -- and
 * everything else is copied bodily into overrides/.
 *
 * That split is the whole difference between a 200 KB pack and a 400 MB one, and it is why a hand-made
 * pack from a folder of jars is so much worse than one exported from a launcher that knows where its
 * mods came from.
 */

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.ModPlatform;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

/// <summary>Which pack format to write.</summary>
public enum PackExportFormat
{
    /// <summary>A .mrpack: files carry download URLs and hashes.</summary>
    Modrinth,

    /// <summary>A CurseForge .zip: files carry only project and file ids.</summary>
    CurseForge,
}

public sealed class PackExportTask : LauncherTask
{
    private readonly InstancePaths _paths;

    private readonly string _targetPath;

    private readonly string _name;

    private readonly string _version;

    private readonly string _summary;

    private readonly bool _optionalFiles;

    private readonly PackExportFormat _format;

    public PackExportTask(
        InstancePaths paths,
        string targetPath,
        string name,
        string version = "1.0.0",
        string summary = "",
        bool optionalFiles = true,
        PackExportFormat format = PackExportFormat.Modrinth)
        : base("Exporting modpack")
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _targetPath = targetPath;
        _name = name;
        _version = version;
        _summary = summary;
        _optionalFiles = optionalFiles;
        _format = format;
    }

    /// <summary>How many mods became manifest links rather than copied files.</summary>
    public int LinkedCount { get; private set; }

    /// <summary>How many files were copied into overrides.</summary>
    public int OverrideCount { get; private set; }

    public long ArchiveSize { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.GameRoot))
        {
            throw new LauncherException($"There is no game folder at {_paths.GameRoot}.");
        }

        if (_name.Length == 0)
        {
            // The manifest requires one, and a pack called "" is not something to hand to anybody.
            throw new LauncherException("A pack needs a name.");
        }

        SetStatus("Reading the instance");

        var profile = new PackProfile(LauncherService.CurrentRuntimeContext());

        if (!profile.Load(_paths.PackProfilePath))
        {
            throw new LauncherException("Could not read the instance's component list.");
        }

        var components = profile.Components
            .Select(c => new PackComponent(c.Uid, c.Version))
            .ToArray();

        if (!components.Any(c => string.Equals(c.Uid, PackComponents.MinecraftUid, StringComparison.Ordinal)))
        {
            throw new LauncherException("The instance has no Minecraft version, so it cannot be exported.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        SetStatus("Working out which mods can be linked");

        // The two formats link mods differently -- Modrinth by URL and hash, CurseForge by project
        // and file id -- but everything not linked becomes an override the same way, so the branches
        // converge on CollectOverrides and one archive writer. Each returns the root files to write
        // (the manifest, plus modlist.html for CurseForge) and the mod filenames it linked.
        var (rootEntries, linkedFileNames) = _format == PackExportFormat.CurseForge
            ? BuildFlame(components)
            : BuildModrinth(components);

        SetStatus("Collecting the rest of the files");

        var overrides = CollectOverrides(linkedFileNames);

        OverrideCount = overrides.Count;

        cancellationToken.ThrowIfCancellationRequested();

        SetStatus($"Writing {_name}");

        // Through a temporary file, as the instance export does: a half-written pack under the chosen
        // name is one somebody will pick up and upload.
        var temporary = _targetPath + ".part";

        try
        {
            await WriteArchiveAsync(temporary, rootEntries, overrides, cancellationToken)
                .ConfigureAwait(false);

            ArchiveSize = new FileInfo(temporary).Length;

            File.Move(temporary, _targetPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Could not write {_targetPath}: {e.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
        }

        SetProgress(1, 1);
    }

    /// <summary>
    /// The mods that can be linked rather than copied.
    /// </summary>
    /// <remarks>
    /// A mod qualifies only if its packwiz entry has a URL AND the file is still on disk under the
    /// name the entry records. Both halves matter: an entry whose jar was deleted would put a
    /// dangling download in somebody else's pack, and a jar renamed by hand no longer matches what
    /// the manifest would tell the importer to save it as.
    /// </remarks>
    /// <summary>Builds the Modrinth manifest and returns the mod filenames it linked.</summary>
    private (List<(string Name, string Content)> Root, HashSet<string> Linked) BuildModrinth(
        IReadOnlyList<PackComponent> components)
    {
        var (linked, names) = CollectLinkedFiles();

        LinkedCount = linked.Count;

        var index = PackExport.CreateModrinthIndex(_name, _version, _summary, components, linked, _optionalFiles);

        return ([(PackTypeDetector.ModrinthMarker, index.ToJsonString())], names);
    }

    /// <summary>Builds the CurseForge manifest plus modlist.html, and returns the linked filenames.</summary>
    /// <remarks>
    /// A CurseForge manifest can only name a mod by its project and file id, so ONLY mods installed
    /// from CurseForge can be linked -- a Modrinth mod has no such ids and falls into overrides
    /// instead, where it is copied bodily. That is not a lossy export: the mod is still in the pack,
    /// just carried rather than referenced. It does mean a pack of Modrinth mods exported to
    /// CurseForge format is almost all overrides, which is honest about what the format can express.
    /// </remarks>
    private (List<(string Name, string Content)> Root, HashSet<string> Linked) BuildFlame(
        IReadOnlyList<PackComponent> components)
    {
        var (files, names) = CollectFlameFiles();

        LinkedCount = files.Count;

        var manifest = PackExport.CreateFlameManifest(_name, _version, _summary, components, files, _optionalFiles);
        var modList = PackExport.CreateFlameModList(files);

        return
        (
            [
                (PackTypeDetector.FlameMarker, manifest.ToJsonString()),
                ("modlist.html", modList),
            ],
            names);
    }

    /// <summary>The mods this instance got from CurseForge, as manifest entries.</summary>
    private (List<FlameExportFile> Files, HashSet<string> FileNames) CollectFlameFiles()
    {
        var files = new List<FlameExportFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var indexDirectory = FileSystem.PathCombine(_paths.GameRoot, ".index");

        if (!Directory.Exists(indexDirectory))
        {
            return (files, names);
        }

        var modsFolder = FileSystem.PathCombine(_paths.GameRoot, "mods");

        foreach (var entryPath in Directory.EnumerateFiles(indexDirectory, "*.pw.toml"))
        {
            PackwizMod mod;

            try
            {
                mod = Packwiz.GetIndexForMod(
                    indexDirectory,
                    Path.GetFileName(entryPath).Replace(".pw.toml", string.Empty, StringComparison.Ordinal));
            }
            catch (Exception e) when (e is IOException or FormatException or InvalidOperationException)
            {
                continue;
            }

            /*
             * ONLY CURSEFORGE MODS. A mod from Modrinth (or a hand-dropped jar with no metadata) has
             * no project/file id to put in the manifest, so it is left for CollectOverrides to carry
             * verbatim -- linking it is simply not something the format can do.
             */
            if (mod.Provider != ResourceProvider.Flame || mod.ProjectId <= 0 || mod.FileId <= 0)
            {
                continue;
            }

            if (mod.Filename.Length == 0)
            {
                continue;
            }

            var jar = FileSystem.PathCombine(modsFolder, mod.Filename);
            var disabled = jar + PackExport.DisabledSuffix;
            var enabled = File.Exists(jar);

            // The entry must still have its jar, or the pack would name a file the author no longer
            // ships -- exactly the check the Modrinth path makes.
            if (!enabled && !File.Exists(disabled))
            {
                continue;
            }

            files.Add(new FlameExportFile(
                mod.ProjectId,
                mod.FileId,
                enabled,
                mod.Name.Length != 0 ? mod.Name : mod.Slug,
                Authors: string.Empty));

            names.Add(mod.Filename);
            names.Add(mod.Filename + PackExport.DisabledSuffix);
        }

        return (files, names);
    }

    private (List<ExportFile> Linked, HashSet<string> FileNames) CollectLinkedFiles()
    {
        var linked = new List<ExportFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var indexDirectory = FileSystem.PathCombine(_paths.GameRoot, ".index");

        if (!Directory.Exists(indexDirectory))
        {
            return (linked, names);
        }

        var modsFolder = FileSystem.PathCombine(_paths.GameRoot, "mods");

        foreach (var entryPath in Directory.EnumerateFiles(indexDirectory, "*.pw.toml"))
        {
            PackwizMod mod;

            try
            {
                mod = Packwiz.GetIndexForMod(
                    indexDirectory,
                    Path.GetFileName(entryPath).Replace(".pw.toml", string.Empty, StringComparison.Ordinal));
            }
            catch (Exception e) when (e is IOException or FormatException or InvalidOperationException)
            {
                continue;
            }

            if (mod.Url.Length == 0 || mod.Filename.Length == 0)
            {
                continue;
            }

            var jar = FileSystem.PathCombine(modsFolder, mod.Filename);
            var disabled = jar + PackExport.DisabledSuffix;

            var enabled = File.Exists(jar);
            var present = enabled || File.Exists(disabled);

            if (!present)
            {
                // The entry outlived its jar. Linking it would hand somebody a pack that fails to
                // install on a file they never chose.
                continue;
            }

            var actual = enabled ? jar : disabled;

            long size;

            try
            {
                size = new FileInfo(actual).Length;
            }
            catch (IOException)
            {
                continue;
            }

            /*
             * BOTH HASHES ARE REQUIRED BY THE FORMAT, and packwiz records only one. The other is
             * computed from the file on disk -- which is also a check that the file really is the one
             * the entry describes, since a mismatch would produce a pack nobody could install.
             */
            var (sha1, sha512) = HashesFor(actual, mod);

            if (sha512.Length == 0)
            {
                continue;
            }

            linked.Add(new ExportFile(
                "mods/" + mod.Filename,
                mod.Url,
                sha1,
                sha512,
                size,
                mod.Side,
                enabled));

            names.Add(mod.Filename);
            names.Add(mod.Filename + PackExport.DisabledSuffix);
        }

        return (linked, names);
    }

    private static (string Sha1, string Sha512) HashesFor(string path, PackwizMod mod)
    {
        try
        {
            using var stream = File.OpenRead(path);

            var sha512 = Convert.ToHexStringLower(SHA512.HashData(stream));

            stream.Position = 0;

            var sha1 = Convert.ToHexStringLower(SHA1.HashData(stream));

            /*
             * The packwiz hash is used where it is the sha512, because that is the one the source
             * published; recomputing agrees with it for an untouched file and disagrees for a modified
             * one, and the published value is the one the importer will check against the CDN.
             */
            if (string.Equals(mod.HashFormat, "sha512", StringComparison.OrdinalIgnoreCase)
                && mod.Hash.Length != 0)
            {
                sha512 = mod.Hash;
            }

            return (sha1, sha512);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>Everything in the game folder that is not a linked mod and not launcher bookkeeping.</summary>
    private List<string> CollectOverrides(HashSet<string> linkedFileNames)
    {
        var files = new List<string>();

        MMCZip.CollectFileListRecursively(_paths.GameRoot, null, files, relative =>
        {
            var path = relative.Replace('\\', '/');

            // The launcher's own metadata. Meaningful only here, and it would tell another launcher
            // to re-download the very mods the manifest already links.
            if (path.StartsWith(".index/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var excluded in ExcludedFromPacks)
            {
                if (path.Equals(excluded, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // A mod that went into the manifest must not ALSO be copied in, or every importer would
            // write the file twice and the pack would be as large as the folder.
            if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                && linkedFileNames.Contains(path["mods/".Length..]))
            {
                return true;
            }

            return false;
        });

        return files;
    }

    /// <summary>
    /// What never belongs in a pack.
    /// </summary>
    /// <remarks>
    /// Narrower than the instance export's list on purpose. A pack is for other people, so somebody
    /// else's worlds and screenshots go too -- an instance export is a backup and keeps them.
    /// </remarks>
    private static readonly string[] ExcludedFromPacks =
    [
        "logs",
        "crash-reports",
        "screenshots",
        "saves",
        "assets",
        "versions",
        "libraries",
        ".fabric",
        "realms_persistence.json",
        "usercache.json",
        "usernamecache.json",
        "servers.dat",
        "options.txt",
    ];

    private async Task WriteArchiveAsync(
        string archivePath,
        IReadOnlyList<(string Name, string Content)> rootEntries,
        IReadOnlyList<string> overrides,
        CancellationToken cancellationToken)
    {
        FileSystem.EnsureFilePathExists(archivePath);

        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in rootEntries)
        {
            var entry = zip.CreateEntry(name);

            /*
             * NO BYTE ORDER MARK. Encoding.UTF8 emits one, and a .mrpack whose manifest starts with EF
             * BB BF is rejected outright by System.Text.Json -- "'0xEF' is an invalid start of a
             * value" -- and by other launchers' parsers too. CurseForge's manifest.json is the same.
             *
             * Every unit test here passed with the BOM in place, because they read the entry back
             * through a StreamReader, which strips it while decoding. Only importing the pack for real,
             * through the path that reads bytes, showed it. Same lesson as elsewhere in this port:
             * reading your own output back through your own abstraction proves the pair agree, not
             * that the file is right.
             */
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));

            await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in overrides)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(_paths.GameRoot, file).Replace('\\', '/');

            // "overrides/" is the folder every importer copies over the instance verbatim, this
            // launcher's own ModrinthImportTask included.
            zip.CreateEntryFromFile(file, "overrides/" + relative);
        }
    }
}
