// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (c) 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
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
 * Ported from launcher/Untar.{h,cpp}, itself an adaptation of zlib's untgz.c, KDE's ktar.cpp and the
 * format description on Wikipedia.
 *
 * WHAT THIS IS FOR: Java runtimes. Adoptium and Mojang ship JREs as .tar.gz on Linux and macOS, so
 * this is the only way the launcher can install one for the user. That also means it unpacks archives
 * fetched over the network, which is why the traversal guard below is not optional.
 *
 * THE FORMAT, for reading the offsets below. A tar is a sequence of 512-byte blocks. Each member is a
 * header block followed by its contents padded up to a block boundary; two zero blocks end the
 * archive. The header fields used here:
 *
 *     offset   0  name      100 bytes, NUL-padded (but exactly-100-byte names are NOT terminated)
 *     offset 100  mode        8 bytes, octal ASCII
 *     offset 124  size       12 bytes, octal ASCII
 *     offset 156  typeflag    1 byte
 *     offset 157  linkname  100 bytes
 *
 * Names longer than 100 bytes use the GNU extension: a type 'L' member whose *contents* are the real
 * name of the member that follows, and 'K' likewise for a link target.
 */

using System.Text;

namespace ExtremeLauncher.Core;

public static class Tar
{
    private const int BlockSize = 512;

    /// <summary>Names up to this length live in the header; longer ones need the GNU extension.</summary>
    private const int ShortNameSize = 100;

    /// <summary>The member kinds that appear in a tar header's type field.</summary>
    private enum TypeFlag : byte
    {
        /// <summary>A regular file, as written by every tar since the 1980s.</summary>
        Regular = (byte)'0',

        /// <summary>A regular file, as written by the very oldest tars — the field is NUL.</summary>
        ARegular = 0,

        Link = (byte)'1',
        Symlink = (byte)'2',
        Character = (byte)'3',
        Block = (byte)'4',
        Directory = (byte)'5',
        Fifo = (byte)'6',
        Contiguous = (byte)'7',

        GlobalPosixHeader = (byte)'g',
        ExtendedPosixHeader = (byte)'x',

        /// <summary>GNU extension: this member's contents are the next member's link target.</summary>
        GnuLongLink = (byte)'K',

        /// <summary>GNU extension: this member's contents are the next member's name.</summary>
        GnuLongName = (byte)'L',
    }

    /// <summary>
    /// Unpacks a tar stream into a directory.
    /// </summary>
    /// <param name="createLink">
    /// How to materialise a link member. Defaults to a hard link, which is what upstream uses and does
    /// not need elevation on Windows the way a symlink would.
    /// </param>
    public static bool Extract(Stream input, string destination, Func<string, string, bool>? createLink = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        createLink ??= NativeLink.TryCreateHardLink;

        // Resolved once so every entry can be checked against it.
        var root = Path.GetFullPath(destination);

        var buffer = new byte[BlockSize];

        string name = string.Empty, linkTarget = string.Empty, firstFolderName = string.Empty;
        var doNotReset = false;

        while (true)
        {
            if (!ReadBlock(input, buffer))
            {
                // Always expect complete blocks: a truncated archive is a failure, not an end.
                return false;
            }

            // The end of the archive is a run of zero blocks.
            if (buffer[0] == 0)
            {
                return true;
            }

            if (!TryGetOctal(buffer, 100, 8, out var mode) || !TryGetOctal(buffer, 124, 12, out var size))
            {
                return false;
            }

            if (name.Length == 0)
            {
                name = DecodeName(buffer, 0, ShortNameSize);

                // The single top-level folder of a runtime tarball is stripped, so a JRE unpacks as
                // bin/, lib/, ... rather than jdk-17.0.1+12/bin/. See the Directory case below.
                if (firstFolderName.Length != 0 && name.StartsWith(firstFolderName, StringComparison.Ordinal))
                {
                    name = name[firstFolderName.Length..];
                }
            }

            if (linkTarget.Length == 0)
            {
                /*
                 * UPSTREAM BUG (#9), FIXED HERE. Upstream reads this from offset 0 — the NAME field —
                 * rather than offset 157, the linkname field. Every link member therefore gets its own
                 * filename as its target, so extracting one produces a link pointing at itself in its
                 * own directory, which either fails outright or leaves a broken entry. Not preserved:
                 * there is no behaviour here worth being bug-compatible with, and Java runtimes on
                 * Linux do contain links.
                 */
                linkTarget = DecodeName(buffer, 157, ShortNameSize);
            }

            switch ((TypeFlag)buffer[156])
            {
                case TypeFlag.Regular:
                case TypeFlag.ARegular:
                    if (!ExtractFile(input, root, name, size, mode))
                    {
                        return false;
                    }

                    break;

                case TypeFlag.Directory:
                    // The FIRST directory is remembered and skipped rather than created; its name is
                    // then stripped from everything that follows.
                    if (firstFolderName.Length == 0)
                    {
                        firstFolderName = name;
                        break;
                    }

                    if (ResolveSafely(root, name) is not { } folderPath
                        || !FileSystem.EnsureFolderPathExists(folderPath))
                    {
                        return false;
                    }

                    break;

                case TypeFlag.GnuLongName:
                {
                    // The name of the NEXT member, so it must survive into the next iteration.
                    doNotReset = true;

                    if (ReadLongName(input, size) is not { } longName)
                    {
                        return false;
                    }

                    name = longName;
                    break;
                }

                case TypeFlag.GnuLongLink:
                {
                    doNotReset = true;

                    if (ReadLongName(input, size) is not { } longLink)
                    {
                        return false;
                    }

                    linkTarget = longLink;
                    break;
                }

                case TypeFlag.Link:
                case TypeFlag.Symlink:
                {
                    if (ResolveSafely(root, name) is not { } linkPath)
                    {
                        return false;
                    }

                    // The target is relative to the link's OWN directory, which is how tar records it.
                    // Resolved to a full native path so the comparison below is against like for like.
                    var targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath) ?? root, linkTarget));

                    if (!targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        // A link pointing outside the destination is the same escape as a traversing
                        // name, just spelled differently.
                        return false;
                    }

                    FileSystem.EnsureFilePathExists(linkPath);

                    if (!createLink(targetPath, linkPath))
                    {
                        return false;
                    }

                    ApplyMode(linkPath, mode);
                    break;
                }

                // Device nodes, FIFOs and the POSIX metadata headers carry nothing a launcher wants.
                // Skipped, along with their contents.
                case TypeFlag.Character:
                case TypeFlag.Block:
                case TypeFlag.Fifo:
                case TypeFlag.Contiguous:
                case TypeFlag.GlobalPosixHeader:
                case TypeFlag.ExtendedPosixHeader:
                default:
                    if (!SkipContents(input, size))
                    {
                        return false;
                    }

                    break;
            }

            if (!doNotReset)
            {
                name = string.Empty;
                linkTarget = string.Empty;
            }

            doNotReset = false;
        }
    }

    /// <summary>Unpacks a gzipped tar.</summary>
    public static bool ExtractGz(string source, string destination)
    {
        try
        {
            using var file = File.OpenRead(source);
            using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);

            return Extract(gzip, destination);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ================================================================== the pieces

    private static bool ExtractFile(Stream input, string root, string name, long size, int mode)
    {
        if (ResolveSafely(root, name) is not { } fileName || !FileSystem.EnsureFilePathExists(fileName))
        {
            return false;
        }

        try
        {
            using (var output = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var block = new byte[BlockSize];

                while (size > 0)
                {
                    // Contents are padded up to a block boundary, so a whole block is always read and
                    // only the meaningful part of it written.
                    if (!ReadBlock(input, block))
                    {
                        return false;
                    }

                    output.Write(block, 0, (int)Math.Min(BlockSize, size));
                    size -= BlockSize;
                }
            }

            ApplyMode(fileName, mode);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads a GNU long name or link target out of a member's contents.</summary>
    private static string? ReadLongName(Stream input, long size)
    {
        // The stored size counts a trailing NUL that is not part of the name.
        size--;

        if (size < 0)
        {
            return null;
        }

        // Rounded up to a whole number of blocks, which is how it sits in the archive.
        var padded = size + (BlockSize - (size % BlockSize));
        var bytes = new byte[padded];

        for (long offset = 0; offset < padded; offset += BlockSize)
        {
            if (!ReadBlock(input, bytes.AsSpan((int)offset, BlockSize)))
            {
                return null;
            }
        }

        return DecodeName(bytes, 0, bytes.Length);
    }

    private static bool SkipContents(Stream input, long size)
    {
        var block = new byte[BlockSize];

        while (size > 0)
        {
            if (!ReadBlock(input, block))
            {
                return false;
            }

            size -= BlockSize;
        }

        return true;
    }

    private static bool ReadBlock(Stream input, Span<byte> block)
    {
        var read = 0;

        // A stream may hand back less than asked for -- GZipStream routinely does -- so this fills the
        // block rather than trusting one Read. Upstream reads from a QIODevice that does not.
        while (read < block.Length)
        {
            var got = input.Read(block[read..]);

            if (got == 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }

    private static bool ReadBlock(Stream input, byte[] block) => ReadBlock(input, block.AsSpan());

    /// <summary>Reads one of the header's octal ASCII number fields.</summary>
    private static bool TryGetOctal(ReadOnlySpan<byte> buffer, int offset, int length, out int value)
    {
        value = 0;

        var field = buffer.Slice(offset, length);
        var end = field.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(end < 0 ? field : field[..end]).Trim();

        if (text.Length == 0)
        {
            // An empty field is zero, which is what a directory's size is.
            return true;
        }

        try
        {
            value = Convert.ToInt32(text, 8);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <remarks>
    /// A name field is NUL-padded, EXCEPT when the name is exactly 100 bytes — then it fills the field
    /// with no terminator at all. Reading to the first NUL *or* the end of the field handles both.
    /// </remarks>
    private static string DecodeName(ReadOnlySpan<byte> buffer, int offset, int length)
    {
        var field = buffer.Slice(offset, Math.Min(length, buffer.Length - offset));
        var end = field.IndexOf((byte)0);

        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>
    /// Resolves an entry name against the destination, refusing anything that escapes it.
    /// </summary>
    /// <returns><see langword="null"/> when the name would write outside the destination.</returns>
    /// <remarks>
    /// UPSTREAM HAS NO SUCH CHECK, and this code unpacks archives fetched over the network. A tar
    /// member named <c>../../../.ssh/authorized_keys</c> would be written exactly there. Added rather
    /// than ported; the same guard exists in MMCZip.ExtractSubDir and NativesExtractor.
    /// </remarks>
    private static string? ResolveSafely(string root, string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        var combined = Path.GetFullPath(Path.Combine(root, name.Replace('\\', '/').TrimStart('/')));

        return combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || combined == root
            ? combined
            : null;
    }

    /// <summary>
    /// Applies a tar member's mode, forcing owner read and write on.
    /// </summary>
    /// <remarks>
    /// The forcing is upstream's hack, and it stays: an archive built with a read-only file in it would
    /// otherwise leave the launcher unable to replace what it just wrote.
    ///
    /// UPSTREAM BUG (#10), FIXED HERE. Upstream passes the octal-parsed value straight to
    /// QFile::Permissions, whose flags are laid out one hex nibble per class (ReadUser = 0x400) rather
    /// than one octal digit. So mode 0755 arrives as 0x1ED and is read as an unrelated set of bits —
    /// which is why the ReadUser|WriteUser hack was needed to make anything work at all. .NET's
    /// UnixFileMode IS the POSIX layout, so the value maps directly. This matters: <c>bin/java</c>
    /// without its execute bit is a Java runtime that cannot be launched.
    ///
    /// No-op on Windows, which has no such bits.
    /// </remarks>
    private static void ApplyMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                (UnixFileMode)(mode & 0xFFF) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException
                                      or ArgumentOutOfRangeException)
        {
            // Best effort, as upstream treats it.
        }
    }
}
