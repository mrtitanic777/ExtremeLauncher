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
 * Ported from launcher/java/download/{ArchiveDownloadTask,ManifestDownloadTask,SymlinkTask}.{h,cpp}.
 *
 * Installing a Java runtime. Two shapes, because the two vendors publish differently: Adoptium ships a
 * single archive, Mojang ships a manifest naming every file individually. A third task tidies up the
 * macOS bundle layout afterwards.
 *
 * BOTH DOWNLOADERS WRITE FILES WHOSE PATHS COME OUT OF DOWNLOADED DATA — archive member names in one
 * case, manifest keys in the other. Upstream guards neither. The archive path inherits the traversal
 * check now built into Tar and MMCZip; the manifest path gets its own below.
 */

using System.Security.Cryptography;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Java;

/// <summary>Shared plumbing for the two runtime downloaders.</summary>
public abstract class JavaDownloadTask : LauncherTask
{
    protected JavaDownloadTask(HttpClient client, Uri url, string finalPath, string checksumType, string checksumHash, string name)
        : base(name)
    {
        Client = client;
        Url = url;
        FinalPath = finalPath;
        ChecksumType = checksumType;
        ChecksumHash = checksumHash;
    }

    protected HttpClient Client { get; }

    protected Uri Url { get; }

    /// <summary>Where the unpacked runtime ends up.</summary>
    protected string FinalPath { get; }

    protected string ChecksumType { get; }

    protected string ChecksumHash { get; }

    public override bool CanAbort => true;

    /// <summary>
    /// Attaches the metadata's checksum to a download, when there is one.
    /// </summary>
    /// <remarks>
    /// ANYTHING THAT IS NOT "sha256" IS TREATED AS SHA-1, inherited. That is a real hazard if a future
    /// meta server introduces a third algorithm: the download would be validated against the wrong
    /// hash and fail rather than being rejected as unsupported. Kept because changing it would reject
    /// entries the Qt launcher accepts, and the meta server only publishes these two.
    /// </remarks>
    protected void AddChecksum(NetRequest request)
    {
        if (ChecksumHash.Length == 0 || ChecksumType.Length == 0)
        {
            return;
        }

        var expected = Convert.FromHexString(ChecksumHash);

        request.AddValidator(new ChecksumValidator(
            ChecksumType == "sha256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1,
            expected));
    }
}

/// <summary>Downloads a runtime published as a single zip or tarball, and unpacks it.</summary>
public sealed class ArchiveDownloadTask : JavaDownloadTask
{
    private readonly string _cacheDirectory;

    public ArchiveDownloadTask(
        HttpClient client,
        Uri url,
        string finalPath,
        string cacheDirectory,
        string checksumType = "",
        string checksumHash = "")
        : base(client, url, finalPath, checksumType, checksumHash, "Download Java")
        => _cacheDirectory = cacheDirectory;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus("Downloading Java");

        // Cached under its own filename, so a retry after a failed extraction does not re-download
        // two hundred megabytes.
        var archivePath = FileSystem.PathCombine(_cacheDirectory, Path.GetFileName(Url.LocalPath));

        FileSystem.EnsureFilePathExists(archivePath);

        var download = Download.MakeFile(Client, Url, archivePath, "JRE::DownloadJava");
        AddChecksum(download);

        download.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);

        if (!await download.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TaskFailedException(download.FailReason);
        }

        ExtractJava(archivePath);
    }

    /// <remarks>
    /// The archive kind is decided by EXTENSION, not by content. That is upstream's choice and it is
    /// worth knowing: a tarball served as "download.bin" is refused rather than sniffed.
    /// </remarks>
    private void ExtractJava(string input)
    {
        var destination = Path.GetFullPath(FinalPath);

        if (input.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Extracting Java (Progress is not reported for tar archives)");

            using var file = File.OpenRead(input);

            if (!Tar.Extract(file, destination))
            {
                throw new TaskFailedException("Unable to extract supplied tar file.");
            }

            return;
        }

        if (input.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || input.EndsWith(".taz", StringComparison.OrdinalIgnoreCase)
            || input.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Extracting Java (Progress is not reported for tar archives)");

            if (!Tar.ExtractGz(input, destination))
            {
                throw new TaskFailedException("Unable to extract supplied tar file.");
            }

            return;
        }

        if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Extracting Java");

            // Runtime zips have a single top-level folder — jdk-17.0.1+12/ — which is stripped so the
            // runtime lands as bin/, lib/, ... exactly as the tar path does.
            using var zip = System.IO.Compression.ZipFile.OpenRead(input);

            var first = zip.Entries.FirstOrDefault();

            if (first is null)
            {
                throw new TaskFailedException("No files were found in the supplied zip file.");
            }

            var topLevel = first.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

            if (MMCZip.ExtractSubDir(zip, topLevel.Length != 0 ? topLevel + "/" : string.Empty, destination) is null)
            {
                throw new TaskFailedException("Unable to extract supplied zip file.");
            }

            return;
        }

        throw new TaskFailedException("Could not determine archive type!");
    }
}

/// <summary>Downloads a runtime published as a manifest of individual files.</summary>
/// <remarks>
/// Mojang's shape. The manifest names every file with its own url, hash and executable flag, plus
/// directories to create and links to make. It is fetched first, then everything it names.
/// </remarks>
public sealed class ManifestDownloadTask : JavaDownloadTask
{
    public ManifestDownloadTask(
        HttpClient client,
        Uri url,
        string finalPath,
        string checksumType = "",
        string checksumHash = "")
        : base(client, url, finalPath, checksumType, checksumHash, "Download Java")
    {
    }

    /// <summary>One file the manifest asks for.</summary>
    private readonly record struct ManifestFile(string Path, string Url, byte[] Hash, bool IsExecutable);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus("Downloading Java");

        var manifest = Download.MakeByteArray(Client, Url, out var sink, "JRE::DownloadJava");
        AddChecksum(manifest);

        if (!await manifest.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TaskFailedException(manifest.FailReason);
        }

        System.Text.Json.Nodes.JsonNode? document;

        try
        {
            document = System.Text.Json.Nodes.JsonNode.Parse(sink.Data);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new TaskFailedException(e.Message, e);
        }

        await DownloadJavaAsync(document, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadJavaAsync(System.Text.Json.Nodes.JsonNode? document, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(FinalPath);
        FileSystem.EnsureFolderPathExists(root);

        var files = Json.EnsureObject(Json.EnsureObject(document), "files");
        var toDownload = new List<ManifestFile>();

        foreach (var (relativePath, node) in files)
        {
            // The keys come out of a downloaded document, so they get the same treatment as archive
            // member names. Upstream combines them onto the destination unchecked.
            if (ResolveSafely(root, relativePath) is not { } path)
            {
                throw new TaskFailedException($"Manifest entry '{relativePath}' escapes the destination directory.");
            }

            var meta = Json.EnsureObject(node);

            switch (Json.EnsureString(meta, "type"))
            {
                case "directory":
                    FileSystem.EnsureFolderPathExists(path);
                    break;

                case "link":
                {
                    // Linux only, and the target is relative to the link's own directory.
                    var target = Json.EnsureString(meta, "target");

                    if (target.Length != 0)
                    {
                        CreateLink(root, path, target);
                    }

                    break;
                }

                case "file":
                {
                    // NOT PORTED, as upstream leaves it: the "lzma" variant alongside "raw", which
                    // would halve the download. Upstream's own TODO.
                    var raw = Json.EnsureObject(Json.EnsureObject(meta, "downloads"), "raw");
                    var url = Json.EnsureString(raw, "url");

                    if (url.Length != 0 && Uri.TryCreate(url, UriKind.Absolute, out _))
                    {
                        toDownload.Add(new ManifestFile(
                            path,
                            url,
                            Convert.FromHexString(Json.EnsureString(raw, "sha1")),
                            Json.EnsureBoolean(meta, "executable")));
                    }

                    break;
                }
            }
        }

        SetStatus("Downloading Java files");

        var job = new NetJob("JRE::FileDownload", Client);

        foreach (var file in toDownload)
        {
            var download = Download.MakeFile(Client, new Uri(file.Url), file.Path);

            if (file.Hash.Length != 0)
            {
                download.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA1, file.Hash));
            }

            if (file.IsExecutable)
            {
                // The manifest says which files are programs; nothing else can tell, since the raw
                // download carries no mode.
                download.Succeeded += (_, _) => MakeExecutable(file.Path);
            }

            job.AddTask(download);
        }

        job.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);

        if (!await job.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TaskFailedException(job.FailReason);
        }
    }

    private static void CreateLink(string root, string linkPath, string target)
    {
        // As upstream spells it: relative to the link's parent.
        var targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath) ?? root, target));

        if (!targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return;
        }

        FileSystem.EnsureFilePathExists(linkPath);
        NativeLink.TryCreateHardLink(targetPath, linkPath);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort, as upstream treats it.
        }
    }

    private static string? ResolveSafely(string root, string relative)
    {
        if (relative.Length == 0)
        {
            return null;
        }

        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/').TrimStart('/')));

        return combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? combined : null;
    }
}

/// <summary>
/// Flattens a macOS runtime bundle so <c>bin/java</c> is where everything else expects it.
/// </summary>
/// <remarks>
/// macOS builds unpack as <c>Contents/Home/bin/java</c> rather than <c>bin/java</c>. Rather than move
/// the tree — which would break the bundle's own structure and its code signature — the contents of
/// the Home directory are LINKED into the runtime root, so both layouts work at once.
///
/// Hard links rather than symlinks despite the class name, which is upstream's: it uses FS::create_link
/// with an explicit "do not use symlinks" comment elsewhere in the codebase, because a symlink on
/// Windows needs elevation. The name is inherited.
/// </remarks>
public sealed class SymlinkTask : LauncherTask
{
    private readonly string _path;
    private readonly Func<string, string, bool> _createLink;

    public SymlinkTask(string finalPath, Func<string, string, bool>? createLink = null) : base("Link Java binary path")
    {
        _path = finalPath;
        _createLink = createLink ?? NativeLink.TryCreateHardLink;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus("Checking for Java binary path");

        var binPath = FileSystem.PathCombine("bin", "java");

        // Already in the ordinary layout — every non-macOS runtime, and macOS ones that were already
        // flattened by a previous run.
        if (File.Exists(FileSystem.PathCombine(_path, binPath)))
        {
            return Task.CompletedTask;
        }

        SetStatus("Searching for Java binary path");

        var contentsPartialPath = FileSystem.PathCombine("Contents", "Home", binPath);
        var found = FindBinPath(_path, contentsPartialPath);

        if (found is null)
        {
            throw new TaskFailedException("Failed to find Java binary path");
        }

        // Back up from .../Contents/Home/bin/java to .../Contents/Home.
        var folderToLink = found[..^(binPath.Length + 1)];

        SetStatus("Collecting folders to symlink");

        var entries = Directory.GetFileSystemEntries(folderToLink);
        SetProgress(0, entries.Length);

        SetStatus("Symlinking Java binary path");

        for (var i = 0; i < entries.Length; i++)
        {
            var destination = FileSystem.PathCombine(_path, Path.GetFileName(entries[i]));

            if (!_createLink(entries[i], destination))
            {
                throw new TaskFailedException($"Failed to link {entries[i]} to {destination}");
            }

            SetProgress(i + 1, entries.Length);
        }

        return Task.CompletedTask;
    }

    /// <summary>Looks for the bundle path at the root, then one level down.</summary>
    /// <remarks>
    /// One level only, which is upstream's depth. A runtime nested deeper is not found — and none are,
    /// because the archive's own top-level folder has already been stripped by the extractor.
    /// </remarks>
    private static string? FindBinPath(string root, string pattern)
    {
        var path = FileSystem.PathCombine(root, pattern);

        if (File.Exists(path))
        {
            return path;
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            path = FileSystem.PathCombine(directory, pattern);

            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
