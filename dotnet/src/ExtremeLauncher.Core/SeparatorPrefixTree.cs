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
 * Ported from launcher/SeparatorPrefixTree.h. A tree of separator-split paths, used by the instance
 * export dialog (FileIgnoreProxy) to track which paths the user has excluded and to drive the
 * three-state checkbox on each folder: a path is UNCHECKED when it or an ancestor is in the tree
 * (Cover finds a covering node), PARTIALLY checked when a descendant is (Exists), and CHECKED otherwise.
 *
 * The separator was a C++ template parameter; here it is a constructor argument (default '/') carried
 * down to every node. A node is either "contained" (an exact inserted path) or purely structural (an
 * intermediate segment on the way to one).
 */

namespace ExtremeLauncher.Core;

/// <summary>A prefix tree of paths split on a separator character.</summary>
public sealed class SeparatorPrefixTree
{
    private readonly Dictionary<string, SeparatorPrefixTree> _children = [];

    private readonly char _separator;

    private bool _contained;

    public SeparatorPrefixTree(char separator = '/') => _separator = separator;

    public SeparatorPrefixTree(IEnumerable<string> paths, char separator = '/')
        : this(separator)
        => Insert(paths);

    private SeparatorPrefixTree(char separator, bool contained)
    {
        _separator = separator;
        _contained = contained;
    }

    /// <summary>Inserts several exact paths.</summary>
    public void Insert(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        foreach (var path in paths)
        {
            Insert(path);
        }
    }

    /// <summary>Inserts one exact path, returning the node it created or reached.</summary>
    public SeparatorPrefixTree Insert(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var sep = path.IndexOf(_separator);

        if (sep == -1)
        {
            var node = new SeparatorPrefixTree(_separator, contained: true);
            _children[path] = node;

            return node;
        }

        var prefix = path[..sep];

        if (!_children.TryGetValue(prefix, out var child))
        {
            child = new SeparatorPrefixTree(_separator, contained: false);
            _children[prefix] = child;
        }

        return child.Insert(path[(sep + 1)..]);
    }

    /// <summary>Whether a node exists for this exact path (structural or contained).</summary>
    public bool Contains(string path) => Find(path) is not null;

    /// <summary>Whether a contained ancestor (or the path itself) covers this path.</summary>
    public bool Covers(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_contained)
        {
            return true;
        }

        var (prefix, remainder) = Split(path);

        return _children.TryGetValue(prefix, out var child) && child.Covers(remainder);
    }

    /// <summary>The covering path (an inserted ancestor of, or equal to, <paramref name="path"/>), or null.</summary>
    public string? Cover(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // A contained node covers everything below it; "" means "covered here", built up by the caller.
        if (_contained)
        {
            return string.Empty;
        }

        var (prefix, remainder) = Split(path);

        if (!_children.TryGetValue(prefix, out var child))
        {
            return null;
        }

        var nested = child.Cover(remainder);

        if (nested is null)
        {
            return null;
        }

        return nested.Length == 0 ? prefix : prefix + _separator + nested;
    }

    /// <summary>Whether the path's node exists in the tree — it need not be a contained (inserted) one.</summary>
    public bool Exists(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var sep = path.IndexOf(_separator);

        if (sep == -1)
        {
            return _children.ContainsKey(path);
        }

        return _children.TryGetValue(path[..sep], out var child) && child.Exists(path[(sep + 1)..]);
    }

    /// <summary>Finds a node by path, or null.</summary>
    public SeparatorPrefixTree? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var sep = path.IndexOf(_separator);

        if (sep == -1)
        {
            return _children.GetValueOrDefault(path);
        }

        return _children.TryGetValue(path[..sep], out var child) ? child.Find(path[(sep + 1)..]) : null;
    }

    /// <summary>Whether this node has no children.</summary>
    public bool Leaf => _children.Count == 0;

    /// <summary>Whether this node is an inserted path rather than a purely structural segment.</summary>
    public bool Contained => _contained;

    /// <summary>Removes a path (and, for a structural prefix, everything under it). False if absent.</summary>
    public bool Remove(string path) => RemoveInternal(path) != Removal.Failed;

    /// <summary>Removes every child of this node.</summary>
    public void Clear() => _children.Clear();

    /// <summary>Every contained path in the tree, separator-joined.</summary>
    public List<string> ToList()
    {
        var collected = new List<string>();

        foreach (var (key, child) in _children)
        {
            foreach (var nested in child.ToList())
            {
                collected.Add(key + _separator + nested);
            }

            if (child._contained)
            {
                collected.Add(key);
            }
        }

        return collected;
    }

    private enum Removal
    {
        Failed,
        Succeeded,
        HasChildren,
    }

    private Removal RemoveInternal(string path)
    {
        if (path.Length == 0)
        {
            if (!_contained)
            {
                // Removing a structural prefix takes everything beneath it.
                Clear();

                return Removal.Succeeded;
            }

            _contained = false;

            return _children.Count != 0 ? Removal.HasChildren : Removal.Succeeded;
        }

        var (childName, remainder) = Split(path);

        if (!_children.TryGetValue(childName, out var child))
        {
            return Removal.Failed;
        }

        var status = child.RemoveInternal(remainder);

        if (status is Removal.Failed or Removal.HasChildren)
        {
            return status;
        }

        // The child said it can go.
        _children.Remove(childName);

        return _contained || _children.Count != 0 ? Removal.HasChildren : Removal.Succeeded;
    }

    /// <summary>Splits a path into its first segment and the remainder (empty when there is no separator).</summary>
    private (string Prefix, string Remainder) Split(string path)
    {
        var sep = path.IndexOf(_separator);

        return sep == -1 ? (path, string.Empty) : (path[..sep], path[(sep + 1)..]);
    }
}
