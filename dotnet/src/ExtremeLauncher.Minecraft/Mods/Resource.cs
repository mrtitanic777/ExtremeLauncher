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
 * Ported from launcher/minecraft/mod/Resource.{h,cpp}.
 *
 * A file in one of an instance's managed folders — a mod, a resource pack, a world save. The base
 * class only knows what can be told from the filename and the filesystem; each subtype adds what it
 * learns by opening the thing.
 *
 * NOT PORTED: the QObject/model surface — signals, compare(), applyFilter(), and the sort machinery.
 * Those exist to drive ResourceFolderModel, which is a QAbstractListModel and belongs to the UI wave.
 */

using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft.Mods;

public enum ResourceType
{
    /// <summary>Not something the launcher recognises.</summary>
    Unknown,

    /// <summary>A zip or jar.</summary>
    ZipFile,

    /// <summary>A loose file that is not an archive.</summary>
    SingleFile,

    /// <summary>A directory.</summary>
    Folder,

    /// <summary>A LiteLoader mod.</summary>
    LiteMod,
}

/// <summary>How thoroughly to inspect a resource.</summary>
public enum ProcessingLevel
{
    /// <summary>Everything the resource has to offer.</summary>
    Full,

    /// <summary>Only enough to tell what it is and whether it is valid.</summary>
    BasicInfoOnly,
}

public class Resource
{
    public Resource(string path)
    {
        Path = path;
        ParseFile();
    }

    /// <summary>The file or folder on disk.</summary>
    /// <summary>Where the file is now. Changes when the resource is enabled or disabled.</summary>
    public string Path { get; private set; }

    public ResourceType Type { get; private set; } = ResourceType.Unknown;

    /// <summary>The display name: the filename with its extension and disabled marker removed.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>The filename as it appears on disk, extension and all. Identifies the resource.</summary>
    public string InternalId { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the game will load this.
    /// </summary>
    /// <remarks>
    /// Disabling is a RENAME: a ".disabled" suffix on the filename. That is how every launcher in this
    /// lineage does it, so a pack disabled here is still disabled after a move to another launcher.
    /// </remarks>
    public bool Enabled { get; private set; } = true;

    public DateTimeOffset ChangedDateTime { get; private set; }

    /// <summary>A human-readable size, or an item count for a folder.</summary>
    public string SizeString { get; private set; } = string.Empty;

    /// <summary>Bytes for a file; the number of entries for a folder.</summary>
    public long SizeInfo { get; private set; }

    /// <summary>Whether the resource is usable. Subtypes decide what that means.</summary>
    public virtual bool Valid => Type != ResourceType.Unknown;

    private void ParseFile()
    {
        var fileName = System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));

        Type = ResourceType.Unknown;
        InternalId = fileName;

        if (Directory.Exists(Path))
        {
            Type = ResourceType.Folder;
            Name = fileName;

            var count = Directory.EnumerateFileSystemEntries(Path).Count();

            SizeInfo = count;
            SizeString = $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(count == 1 ? "item" : "items")}";
        }
        else if (File.Exists(Path))
        {
            if (fileName.EndsWith(".disabled", StringComparison.Ordinal))
            {
                fileName = fileName[..^9];
                Enabled = false;
            }

            if (fileName.EndsWith(".zip", StringComparison.Ordinal) || fileName.EndsWith(".jar", StringComparison.Ordinal))
            {
                Type = ResourceType.ZipFile;
                fileName = fileName[..^4];
            }
            else if (fileName.EndsWith(".nilmod", StringComparison.Ordinal))
            {
                // A NilLoader mod, which is a jar under another name.
                Type = ResourceType.ZipFile;
                fileName = fileName[..^7];
            }
            else if (fileName.EndsWith(".litemod", StringComparison.Ordinal))
            {
                Type = ResourceType.LiteMod;
                fileName = fileName[..^8];
            }
            else
            {
                Type = ResourceType.SingleFile;
            }

            Name = fileName;

            var info = new FileInfo(Path);

            SizeInfo = info.Length;
            SizeString = StringUtils.HumanReadableFileSize(info.Length, useSi: true);
        }

        ChangedDateTime = Directory.Exists(Path)
            ? new DateTimeOffset(Directory.GetLastWriteTimeUtc(Path), TimeSpan.Zero)
            : File.Exists(Path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(Path), TimeSpan.Zero)
                : default;
    }

    /// <summary>What <see cref="SetEnabled"/> should do.</summary>
    public enum EnableAction
    {
        Enable,
        Disable,
        Toggle,
    }

    /// <summary>
    /// Turns a resource on or off by renaming it.
    /// </summary>
    /// <remarks>
    /// Ported from Resource::enable. THE SUFFIX IS THE STATE: a mod is disabled by appending
    /// ".disabled" to its filename, which is how every launcher in this lineage does it and how the
    /// game sees it too -- the loader simply does not recognise the extension.
    ///
    /// UNKNOWN and FOLDER resources are refused, as upstream does. A folder has no suffix convention,
    /// and renaming one would hide it from the game without the user being able to see why.
    /// </remarks>
    /// <returns>False when nothing was done, including when it was already in the state asked for.</returns>
    public bool SetEnabled(EnableAction action)
    {
        if (Type is ResourceType.Unknown or ResourceType.Folder)
        {
            return false;
        }

        var enable = action switch
        {
            EnableAction.Enable => true,
            EnableAction.Disable => false,
            _ => !Enabled,
        };

        if (Enabled == enable)
        {
            return false;
        }

        /*
         * The path is used AS GIVEN rather than run through GetFullPath. On Windows that would
         * normalise the separators to backslashes, while everything else in this port produces Qt-style
         * forward slashes (FileSystem.PathCombine) -- so the renamed resource would no longer compare
         * equal to the row the caller is holding, and the mods page would lose its selection on every
         * toggle. Found by a test doing exactly that.
         */
        var path = Path;

        if (enable)
        {
            /*
             * Disabled but with no ".disabled" suffix is a contradiction -- something renamed the file
             * underneath us. Refused rather than guessed at: chopping nine characters off a name that
             * does not end in ".disabled" would destroy part of the real filename.
             */
            if (!path.EndsWith(".disabled", StringComparison.Ordinal))
            {
                return false;
            }

            path = path[..^9];
        }
        else
        {
            path += ".disabled";

            if (File.Exists(path))
            {
                path = UniqueResourceName(path);
            }
        }

        try
        {
            File.Move(Path, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by the game, or a read-only folder. The caller reports it; nothing has changed.
            return false;
        }

        Path = path;

        // Re-read: the name, the type and Enabled are all derived from the filename, which just moved.
        Enabled = true;
        ParseFile();

        return true;
    }

    /// <summary>
    /// A free name for a resource being disabled on top of one already there.
    /// </summary>
    /// <remarks>
    /// Ported from FS::getUniqueResourceName, quirks and all. Two things about it are worth naming
    /// because they look like mistakes:
    ///
    /// It takes the path WITH ".disabled" already appended, and its first act is to strip that off
    /// again to ask whether the enabled form exists -- if it does not, the ".disabled" name is handed
    /// back unchanged even though the caller only got here because that file exists.
    ///
    /// And the suffix it adds is ".duplicate", NOT ".disabled" -- so the result is "mod.jar.duplicate",
    /// a file the loader also ignores, but for the different reason that it is not a jar.
    /// </remarks>
    private static string UniqueResourceName(string filePath)
    {
        if (!filePath.EndsWith(".disabled", StringComparison.Ordinal))
        {
            // "prioritize enabled mods", in upstream's words.
            return filePath;
        }

        if (!File.Exists(filePath[..^9]))
        {
            return filePath;
        }

        // completeBaseName drops only the LAST suffix, so "mod.jar.disabled" gives "mod.jar".
        var directory = System.IO.Path.GetDirectoryName(filePath) ?? string.Empty;
        var baseName = System.IO.Path.GetFileNameWithoutExtension(filePath);

        var counter = 1;
        string candidate;

        do
        {
            candidate = FileSystem.PathCombine(
                directory,
                counter == 1
                    ? baseName + ".duplicate"
                    : baseName + ".duplicate" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture));

            counter++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    /// <summary>
    /// Removes the resource from disk.
    /// </summary>
    /// <remarks>
    /// UPSTREAM FALLS THROUGH TO A PERMANENT DELETE when trashing fails, without asking:
    /// `(attemptTrash &amp;&amp; FS::trash(...)) || FS::deletePath(...)`. Kept, and it is defensible
    /// here in a way it would not be for an instance -- a mod jar is a download, not a world -- but it
    /// is a real difference from how this port removes instances, where the second question is asked.
    /// </remarks>
    public bool Destroy(bool attemptTrash = true)
    {
        Type = ResourceType.Unknown;

        return (attemptTrash && Trash.TryTrash(Path, out _)) || FileSystem.DeletePath(Path);
    }

    public override string ToString() => $"{Name} ({Type})";
}
