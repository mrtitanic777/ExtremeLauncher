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
 * Ported from launcher/minecraft/PackProfile.{h,cpp} -- the stack-management core.
 *
 * An instance's ordered list of components, persisted as mmc-pack.json. Order is application order:
 * Minecraft first, then loaders, then the user's own patches, each overriding what came before. The
 * flattened result is a LaunchProfile, cached until something invalidates it.
 *
 * THE ON-DISK FORMAT IS LOAD-BEARING. Every existing instance has an mmc-pack.json, so the shape --
 * formatVersion 1, a "components" array, and the specific key names -- is preserved exactly.
 *
 * !! A FOURTH UPSTREAM BUG -- BEHAVIOURAL, SEE PORTING.md !!
 * componentToJsonV1 writes "cachedVolatile" but componentFromJsonV1 reads "volatile", so the volatile
 * flag never survives a reload and volatile components are never auto-removed. This port reads BOTH
 * keys, which is backward compatible but does activate a path that has effectively been dormant.
 *
 * NOT PORTED (each needs something that is not here yet):
 *   - reload() / resolve()            -- drive ComponentUpdateTask, the last piece of the resolver
 *   - installJarMods / installCustomJar / installAgents / installComponents -- MinecraftInstance I/O
 *   - the QAbstractListModel surface  -- UI wave
 *   - getModLoaders / getSupportedModLoaders -- ModPlatform::ModLoaderType, wave 8
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft;

namespace ExtremeLauncher.Meta;

public sealed class PackProfile
{
    /// <summary>The only mmc-pack.json format version that exists.</summary>
    public const int CurrentComponentsFileVersion = 1;

    private readonly List<Component> _components = [];
    private readonly Dictionary<string, Component> _index = new(StringComparer.Ordinal);

    private LaunchProfile? _profile;

    public PackProfile(RuntimeContext runtimeContext) => RuntimeContext = runtimeContext;

    public RuntimeContext RuntimeContext { get; set; }

    public IReadOnlyList<Component> Components => _components;

    public int Count => _components.Count;

    public Component? GetComponent(string uid) => _index.GetValueOrDefault(uid);

    public Component? GetComponent(int index) => index >= 0 && index < _components.Count ? _components[index] : null;

    public int IndexOf(string uid) => _components.FindIndex(c => string.Equals(c.Uid, uid, StringComparison.Ordinal));

    // ================================================================== mutation

    public void AppendComponent(Component component) => InsertComponent(_components.Count, component);

    /// <summary>Inserts so the component ends up as near <paramref name="index"/> as possible.</summary>
    /// <returns>The index it actually landed at, or -1 if the uid was already present.</returns>
    public int InsertComponent(int index, Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (_index.ContainsKey(component.Uid))
        {
            return -1;
        }

        var target = Math.Clamp(index, 0, _components.Count);

        _components.Insert(target, component);
        _index[component.Uid] = component;

        InvalidateLaunchProfile();

        return target;
    }

    public bool Remove(int index)
    {
        var component = GetComponent(index);

        if (component is null || !component.IsRemovable)
        {
            return false;
        }

        _components.RemoveAt(index);
        _index.Remove(component.Uid);

        InvalidateLaunchProfile();
        return true;
    }

    public bool Remove(string uid)
    {
        var index = IndexOf(uid);
        return index >= 0 && Remove(index);
    }

    /// <summary>
    /// Copies a metadata component into an editable local patch. Ported from PackProfile::customize.
    /// </summary>
    /// <remarks>
    /// GUARDED TWICE, as upstream is: the profile checks the component may be customised and the
    /// component checks it is not already custom. The two are not redundant — the first is what the
    /// UI reads to enable the button, the second is what makes calling it directly safe.
    /// </remarks>
    /// <param name="patchesDirectory">The instance's patches folder, where the override is written.</param>
    public bool Customize(int index, string patchesDirectory)
    {
        var component = GetComponent(index);

        if (component is null || !component.IsCustomizable)
        {
            return false;
        }

        if (!component.Customize(patchesDirectory))
        {
            return false;
        }

        InvalidateLaunchProfile();
        return true;
    }

    /// <summary>
    /// Adds a new, empty custom component -- upstream's installEmpty, behind the version page's Add Empty.
    /// </summary>
    /// <remarks>
    /// It writes a bare patch (a uid, a name, version "1") and appends the component as a local override,
    /// so it appears immediately and is editable. Refused when the uid is already present -- two
    /// components with one uid is exactly what upstream's blacklist in NewComponentDialog prevents.
    /// Needs no metadata and no network: the whole point is a component the user is inventing.
    /// </remarks>
    /// <returns>False when the uid clashes or the patch could not be written.</returns>
    public bool InstallEmpty(string uid, string name, string patchesDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        ArgumentNullException.ThrowIfNull(patchesDirectory);

        if (GetComponent(uid) is not null)
        {
            return false;
        }

        var file = new VersionFile { Uid = uid, Name = name, Version = "1" };
        var path = PatchFilePathForUid(patchesDirectory, uid);

        try
        {
            FileSystem.EnsureFilePathExists(path);
            File.WriteAllText(path, OneSixVersionFormat.VersionFileToJson(file).ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var component = new Component(uid, file) { Version = "1" };
        component.SetCachedData(name, "1", [], [], isVolatile: false);

        AppendComponent(component);
        InvalidateLaunchProfile();

        return true;
    }

    /// <summary>
    /// Throws away a component's local patch. Ported from PackProfile::revertToBase.
    /// </summary>
    /// <param name="metadataIndex">
    /// Consulted through <see cref="Component.IsRevertible"/>: a patch is only safe to discard if the
    /// metadata index still knows the uid, or reverting would leave a component that can never be
    /// resolved again.
    /// </param>
    public bool RevertToBase(int index, string patchesDirectory, Index metadataIndex)
    {
        ArgumentNullException.ThrowIfNull(metadataIndex);

        var component = GetComponent(index);

        if (component is null || !component.IsRevertible(metadataIndex))
        {
            return false;
        }

        if (!component.Revert(patchesDirectory))
        {
            return false;
        }

        InvalidateLaunchProfile();
        return true;
    }

    public enum MoveDirection
    {
        Up,
        Down,
    }

    /// <summary>Swaps a component with its neighbour. Both ends must be moveable.</summary>
    public void Move(int index, MoveDirection direction)
    {
        if (index < 0 || index >= _components.Count)
        {
            return;
        }

        var theirIndex = direction == MoveDirection.Up ? index - 1 : index + 1;

        if (theirIndex < 0 || theirIndex >= _components.Count || theirIndex == index)
        {
            return;
        }

        var from = _components[index];
        var to = _components[theirIndex];

        if (!from.IsMoveable || !to.IsMoveable)
        {
            return;
        }

        (_components[index], _components[theirIndex]) = (to, from);

        InvalidateLaunchProfile();
    }

    public string GetComponentVersion(string uid) => GetComponent(uid)?.Version ?? string.Empty;

    /// <summary>Sets a component's requested version, adding the component if it is missing.</summary>
    public bool SetComponentVersion(string uid, string version, bool important = false)
    {
        var component = GetComponent(uid);

        if (component is null)
        {
            AppendComponent(new Component(uid) { Version = version, IsImportant = important });
            return true;
        }

        if (string.Equals(component.Version, version, StringComparison.Ordinal))
        {
            return true;
        }

        component.Version = version;
        component.IsImportant = important;

        InvalidateLaunchProfile();
        return true;
    }

    /// <summary>Adds an empty component, e.g. so the user can customise it into existence.</summary>
    public bool InstallEmpty(string uid, string name)
    {
        if (_index.ContainsKey(uid))
        {
            return false;
        }

        var file = new VersionFile { Uid = uid, Name = name, Version = "1" };
        AppendComponent(new Component(uid, file) { Version = "1" });

        return true;
    }

    // ================================================================== the flattened profile

    public void InvalidateLaunchProfile() => _profile = null;

    /// <summary>
    /// Applies every component in order, cached until invalidated.
    /// </summary>
    /// <remarks>
    /// Order is the whole point: a later component overrides an earlier one, which is how Forge
    /// replaces Minecraft's main class without either knowing about the other.
    /// </remarks>
    public LaunchProfile GetProfile()
    {
        if (_profile is not null)
        {
            return _profile;
        }

        var profile = new LaunchProfile();

        foreach (var component in _components)
        {
            component.ApplyTo(profile, RuntimeContext);
        }

        _profile = profile;
        return profile;
    }

    // ================================================================== persistence

    /// <summary>Where a component's local override lives, relative to the instance's patches folder.</summary>
    public static string PatchFilePathForUid(string patchesDirectory, string uid)
        => FileSystem.PathCombine(patchesDirectory, $"{uid}.json");

    public bool Save(string filename)
    {
        try
        {
            Json.Write(ToJson(), filename);
            return true;
        }
        catch (FileSystemException)
        {
            return false;
        }
    }

    public JsonObject ToJson()
    {
        var components = new JsonArray();

        foreach (var component in _components)
        {
            components.Add(ComponentToJsonV1(component));
        }

        return new JsonObject
        {
            ["formatVersion"] = CurrentComponentsFileVersion,
            ["components"] = components,
        };
    }

    /// <returns><see langword="false"/> on any parse failure, leaving the profile empty.</returns>
    public bool Load(string filename)
    {
        if (!File.Exists(filename))
        {
            return false;
        }

        try
        {
            return LoadFromJson(Json.RequireObject(Json.RequireDocumentFromFile(filename)));
        }
        catch (Exception e) when (e is JsonException or FileSystemException)
        {
            Clear();
            return false;
        }
    }

    public bool LoadFromJson(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        Clear();

        try
        {
            if (Json.RequireInteger(root, "formatVersion") != CurrentComponentsFileVersion)
            {
                throw new JsonException($"Invalid component file version, expected {CurrentComponentsFileVersion}");
            }

            foreach (var element in Json.RequireArray(root, "components"))
            {
                var obj = element as JsonObject ?? throw new JsonException("Component must be an object.");
                AppendComponent(ComponentFromJsonV1(obj));
            }
        }
        catch (JsonException)
        {
            // A malformed file yields an empty profile rather than a half-loaded one.
            Clear();
            return false;
        }

        return true;
    }

    public void Clear()
    {
        _components.Clear();
        _index.Clear();
        InvalidateLaunchProfile();
    }

    private static JsonObject ComponentToJsonV1(Component component)
    {
        var obj = new JsonObject { ["uid"] = component.Uid };

        // Only non-default values are written, which keeps mmc-pack.json readable.
        if (component.Version.Length != 0)
        {
            obj["version"] = component.Version;
        }

        if (component.IsDependencyOnly)
        {
            obj["dependencyOnly"] = true;
        }

        if (component.IsImportant)
        {
            obj["important"] = true;
        }

        if (component.IsDisabled)
        {
            obj["disabled"] = true;
        }

        if (component.CachedVersion.Length != 0)
        {
            obj["cachedVersion"] = component.CachedVersion;
        }

        if (component.CachedName.Length != 0)
        {
            obj["cachedName"] = component.CachedName;
        }

        MetadataFormat.SerializeRequires(obj, component.CachedRequires, "cachedRequires");
        MetadataFormat.SerializeRequires(obj, component.CachedConflicts, "cachedConflicts");

        if (component.CachedVolatile)
        {
            obj["cachedVolatile"] = true;
        }

        return obj;
    }

    private static Component ComponentFromJsonV1(JsonObject obj)
    {
        var component = new Component(Json.RequireString(obj, "uid"))
        {
            Version = Json.EnsureString(obj, "version"),
            IsDependencyOnly = Json.EnsureBoolean(obj, "dependencyOnly"),
            IsImportant = Json.EnsureBoolean(obj, "important"),
            IsDisabled = Json.EnsureBoolean(obj, "disabled"),
        };

        component.SetCachedData(
            Json.EnsureString(obj, "cachedName"),
            Json.EnsureString(obj, "cachedVersion"),
            MetadataFormat.ParseRequires(obj, "cachedRequires"),
            MetadataFormat.ParseRequires(obj, "cachedConflicts"),

            // BUG FIX: upstream writes "cachedVolatile" but reads "volatile", so the flag was always
            // lost. Both are read here; see the file header.
            Json.EnsureBoolean(obj, "cachedVolatile") || Json.EnsureBoolean(obj, "volatile"));

        return component;
    }
}
