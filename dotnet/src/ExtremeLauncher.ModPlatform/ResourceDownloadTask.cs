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
 * Ported from launcher/ResourceDownloadTask.{h,cpp}.
 *
 * INSTALLING ONE MOD, which is also how a mod is UPDATED -- an update is an install of a newer version
 * plus the removal of the older file, and the order those happen in is the whole point of this class.
 *
 * THE OLD FILE IS DELETED LAST, AND ONLY ON SUCCESS. Upstream says why in a comment on the indirection
 * that makes it possible: "so that we don't delete a mod before being sure it was downloaded
 * successfully". Update a mod, lose the network mid-download, and the instance must still have the
 * version it started with. Getting this backwards costs the user a working mod in exchange for
 * nothing.
 *
 * AND ONLY IF THE NAME CHANGED. Many updates keep the filename, in which case the download has already
 * replaced the file and deleting "the old one" would delete the new one.
 */

using System.Security.Cryptography;
using ExtremeLauncher.Core;
using ExtremeLauncher.Net;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.ModPlatform;

public sealed class ResourceDownloadTask : LauncherTask
{
    private readonly IndexedPack _pack;
    private readonly IndexedVersion _version;
    private readonly string _targetFolder;
    private readonly string _indexDirectory;
    private readonly HttpClient _client;
    private readonly HttpMetaCache _cache;
    private readonly bool _isIndexed;

    /// <param name="targetFolder">Where the file goes — usually the instance's mods folder.</param>
    /// <param name="indexDirectory">Where packwiz metadata lives, or empty to write none.</param>
    /// <param name="isIndexed">
    /// Whether this resource kind is tracked in the metadata index. Only mods are; a resource pack is
    /// installed without one, which is why upstream tests the model type before building the task.
    /// </param>
    public ResourceDownloadTask(
        IndexedPack pack,
        IndexedVersion version,
        string targetFolder,
        string indexDirectory,
        HttpClient client,
        HttpMetaCache cache,
        bool isIndexed = true)
        : base($"Downloading {pack.Name}")
    {
        _pack = pack;
        _version = version;
        _targetFolder = targetFolder;
        _indexDirectory = indexDirectory;
        _client = client;
        _cache = cache;
        _isIndexed = isIndexed;
    }

    /// <summary>The file this installs, as it will be named on disk.</summary>
    public string FileName => _version.FileName;

    /// <summary>The file this replaced, if the update renamed it. Empty otherwise.</summary>
    public string ReplacedFileName { get; private set; } = string.Empty;

    public override bool CanAbort => true;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        /*
         * METADATA FIRST, because writing it is what reveals the file the previous version used --
         * the packwiz entry is keyed by slug, so reading it before overwriting is the only record of
         * what is already installed.
         */
        var superseded = _isIndexed && _indexDirectory.Length != 0
            ? WriteMetadata()
            : string.Empty;

        SetStatus($"Downloading resource:\n{_version.DownloadUrl}");

        var destination = FileSystem.PathCombine(_targetFolder, _version.FileName);

        var download = Download.MakeFile(_client, new Uri(_version.DownloadUrl), destination, _version.FileName);

        if (CreateValidator() is { } validator)
        {
            download.AddValidator(validator);
        }

        download.ProgressChanged += (_, e) => SetProgress(e.Current, e.Total);

        if (!await download.RunAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TaskFailedException(download.FailReason);
        }

        /*
         * ONLY NOW, and only if the name differs. An update that keeps its filename has already been
         * replaced by the download above, so deleting "the old file" would delete the new one.
         */
        if (superseded.Length != 0 && superseded != _version.FileName)
        {
            ReplacedFileName = superseded;

            FileSystem.DeletePath(FileSystem.PathCombine(_targetFolder, superseded));

            // The disabled form of the same mod, which the user may have turned off before updating.
            FileSystem.DeletePath(FileSystem.PathCombine(_targetFolder, superseded + EnsureMetadata.DisabledSuffix));
        }
    }

    /// <summary>Writes the packwiz entry and returns the filename it replaced.</summary>
    private string WriteMetadata()
    {
        // Read before write: the entry is keyed by slug, so overwriting it loses the old filename.
        var existing = Packwiz.GetIndexForMod(_indexDirectory, _pack.Slug);
        var superseded = existing.IsValid ? existing.Filename : string.Empty;

        var metadata = EnsureMetadata.CreateMetadata(
            _pack,
            _version,
            new ResourceToIdentify(FileSystem.PathCombine(_targetFolder, _version.FileName), _pack.Name));

        Packwiz.UpdateModIndex(_indexDirectory, metadata);

        return superseded;
    }

    /// <summary>
    /// The checksum to verify the download against, or null when there is none to use.
    /// </summary>
    /// <remarks>
    /// MURMUR2 IS NOT ONE, and upstream's switch falls through for it: it is CurseForge's file
    /// fingerprint, not a cryptographic digest, and there is no validator that speaks it. A CurseForge
    /// download whose only hash is murmur2 is therefore installed UNVERIFIED — which is the format's
    /// limitation rather than the launcher's choice, and worth knowing.
    /// </remarks>
    /// <remarks>
    /// MD4 is skipped too. Upstream maps it because Qt offers one; .NET does not, and no provider
    /// publishes MD4 hashes.
    /// </remarks>
    private IValidator? CreateValidator()
    {
        if (_version.Hash.Length == 0)
        {
            return null;
        }

        var algorithm = Hashing.AlgorithmFromString(_version.HashType) switch
        {
            HashAlgorithm.Md5 => HashAlgorithmName.MD5,
            HashAlgorithm.Sha1 => HashAlgorithmName.SHA1,
            HashAlgorithm.Sha256 => HashAlgorithmName.SHA256,
            HashAlgorithm.Sha512 => HashAlgorithmName.SHA512,
            _ => (HashAlgorithmName?)null,
        };

        if (algorithm is null)
        {
            return null;
        }

        try
        {
            return new ChecksumValidator(algorithm.Value, Convert.FromHexString(_version.Hash));
        }
        catch (FormatException)
        {
            /*
             * A hash that is not hex cannot verify anything, and refusing the install over it would
             * block a mod the provider is perfectly willing to serve. Downloaded unverified instead,
             * which is the same position as a murmur2-only file.
             */
            return null;
        }
    }
}
