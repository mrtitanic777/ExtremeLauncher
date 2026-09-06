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
 * Ported from ui/pages/instance/ModFolderPage.cpp and its siblings, less their Qt models.
 *
 * ONE CLASS FOR THREE PAGES. Mods, resource packs and shader packs differ in which folder they read
 * and what a row is called; everything else -- enabling by rename, removing, re-reading after a change
 * -- is identical. Upstream has ModFolderPage, ResourcePackPage and ShaderPackPage as separate
 * classes over a shared ExternalResourcesPage, which is the same arrangement with more of it written
 * down twice.
 *
 * THE MOST-USED PAGE IN A MINECRAFT LAUNCHER, and the one where the interesting behaviour is that
 * NOTHING HERE IS DEFERRED. Enabling a mod renames a file immediately; deleting one removes it
 * immediately. There is no Save button and no unsaved state, because the filesystem is the document.
 *
 * That is why HasUnsavedChanges is always false: this page has nothing to write on close. Saying so
 * explicitly matters, because the instance window prompts about unsaved changes and a page that
 * claimed dirtiness it could not resolve would prompt forever.
 *
 * THE LIST IS RE-READ AFTER EVERY CHANGE. A rename changes the file's name, its path and its sort
 * position all at once, and mods are also added and removed behind the launcher's back -- by the user
 * dropping a jar in the folder, or by another launcher. Re-reading is cheap and always right.
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.Minecraft.Mods;

namespace ExtremeLauncher.ViewModels;

/// <summary>One row: a mod in the instance's mods folder.</summary>
public sealed partial class ModViewModel : ObservableObject
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>The mod's own declared version, or empty when it does not declare one.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Whether the game will load it — that is, whether it lacks the ".disabled" suffix.</summary>
    public required bool IsEnabled { get; init; }

    public string SizeString { get; init; } = string.Empty;

    /// <summary>Who wrote it, as the jar declares. Empty for a jar whose metadata will not parse.</summary>
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>The mod's own page, where the jar names one.</summary>
    public string HomeUrl { get; init; } = string.Empty;

    /// <summary>The file on disk, which is what an export offers as an optional column.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Which folder a resource page shows.</summary>
public enum ResourceFolderKind
{
    Mods,
    ResourcePacks,
    ShaderPacks,

    /// <summary>The legacy pre-1.6 texture packs, in the "texturepacks" folder.</summary>
    TexturePacks,
}

/// <summary>Checks for and installs mod updates. Implemented by the app.</summary>
public interface IModUpdater
{
    /// <returns>True when something was installed, so the folder should be re-read.</returns>
    Task<bool> CheckAsync(string gameRoot);
}

/// <summary>Copies dropped files into the instance. Implemented in the launch layer.</summary>
public interface IResourceDropTarget
{
    /// <returns>A sentence describing what happened to them.</returns>
    Task<string> DropAsync(IReadOnlyList<string> files);
}

/// <summary>Shows the mod list export dialog. Implemented by the app.</summary>
public interface IModListExporter
{
    Task ExportAsync(string instanceName, IReadOnlyList<ModPlatform.ModListEntry> mods);
}

/// <summary>Downloads resources into an instance folder. Implemented by the app.</summary>
public interface IModInstaller
{
    /// <returns>How many files were installed. Zero when the dialog was cancelled.</returns>
    Task<int> AddAsync(string gameRoot, ResourceFolderKind kind);
}

public sealed partial class ModsPageViewModel : ObservableObject, IInstancePage
{
    private string _gameRoot = string.Empty;

    private readonly IModInstaller? _installer;

    private readonly IModUpdater? _updater;

    /// <param name="installer">
    /// Opens the download dialog and installs what is chosen. Null leaves the Add button disabled
    /// rather than present and inert.
    /// </param>
    /// <param name="updater">
    /// Checks for and installs mod updates. Only meaningful for the mods folder -- resource packs
    /// carry no version metadata to compare against, so the button is absent there rather than
    /// present and always answering "nothing to do".
    /// </param>
    /// <param name="exporter">
    /// Writes the installed list out in a shareable format. Mods only, like the updater.
    /// </param>
    public ModsPageViewModel(
        ResourceFolderKind kind = ResourceFolderKind.Mods,
        IModInstaller? installer = null,
        IModUpdater? updater = null,
        IModListExporter? exporter = null,
        string instanceName = "",
        IResourceDropTarget? dropTarget = null,
        IFolderOpener? folders = null)
    {
        Kind = kind;
        _installer = installer;
        _updater = updater;
        _exporter = exporter;
        _instanceName = instanceName;
        _dropTarget = dropTarget;
        _folders = folders;
    }

    private readonly IFolderOpener? _folders;

    private readonly IModListExporter? _exporter;

    private readonly string _instanceName;

    private readonly IResourceDropTarget? _dropTarget;

    /// <summary>Which folder this page shows.</summary>
    public ResourceFolderKind Kind { get; }

    public string Title => Kind switch
    {
        ResourceFolderKind.ResourcePacks => "Resource packs",
        ResourceFolderKind.ShaderPacks => "Shader packs",
        ResourceFolderKind.TexturePacks => "Texture packs",
        _ => "Mods",
    };

    /// <summary>What one row is, for messages the user reads.</summary>
    private string ThingName => Kind switch
    {
        ResourceFolderKind.ResourcePacks => "resource pack",
        ResourceFolderKind.ShaderPacks => "shader pack",
        ResourceFolderKind.TexturePacks => "texture pack",
        _ => "mod",
    };

    /// <summary>
    /// Always false: every change here is written the moment it is made.
    /// </summary>
    /// <remarks>
    /// The instance window prompts about unsaved changes on close. A page that reported dirtiness it
    /// had no way to resolve would prompt every time and never stop.
    /// </remarks>
    public bool HasUnsavedChanges => false;

    public bool Save() => true;

    /// <summary>The mods, enabled first and then by name.</summary>
    public ObservableCollection<ModViewModel> Mods { get; } = [];

    /// <summary>What the last action did, or empty.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Reads the mods out of an instance's game directory.</summary>
    public void Load(string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);

        _gameRoot = gameRoot;

        Rebuild();

        // Announced: CanAdd depends on the game root, and the button is bound to it. See the note in
        // AccountsViewModel about why a computed property alone is not enough.
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanCheckUpdates));
        OnPropertyChanged(nameof(CanOpenFolder));
        AddCommand.NotifyCanExecuteChanged();
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    public ModViewModel? Selected => Mods.FirstOrDefault(m => m.IsSelected);

    [RelayCommand]
    public void Select(string? path)
    {
        foreach (var mod in Mods)
        {
            mod.IsSelected = path is not null && mod.Path == path;
        }

        RaiseSelectionDependent();
    }

    public bool HasSelection => Selected is not null;

    /// <summary>What the toggle button should say for the current selection.</summary>
    /// <remarks>
    /// One button rather than two, and it names what will HAPPEN rather than the current state. A
    /// button labelled "Enabled" is ambiguous about which way it goes.
    /// </remarks>
    public string ToggleLabel => Selected is { IsEnabled: true } ? "Disable" : "Enable";

    // ================================================================== acting

    /// <summary>Turns the selected mod on or off, renaming it.</summary>
    [RelayCommand]
    public void ToggleSelected()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        var resource = new Resource(selected.Path);

        if (!resource.SetEnabled(Resource.EnableAction.Toggle))
        {
            /*
             * The usual cause is the game still running out of that folder with the jar open. Said
             * plainly, because "failed" alone invites a second press that fails the same way.
             */
            Status = $"Could not change the {ThingName} “{selected.Name}”. The game may still be running.";

            return;
        }

        Status = resource.Enabled ? $"Enabled “{resource.Name}”." : $"Disabled “{resource.Name}”.";

        Rebuild();

        // The file moved, so the selection follows it to its new path rather than being lost.
        Select(resource.Path);
    }

    /// <summary>
    /// Removes the selected mod.
    /// </summary>
    /// <remarks>
    /// Trashed where the platform can and deleted where it cannot, WITHOUT a second question --
    /// upstream's behaviour, and defensible here in a way it would not be for an instance: a mod is a
    /// download, and the thing it would take with it is not somebody's world.
    /// </remarks>
    [RelayCommand]
    public void DeleteSelected()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        if (!new Resource(selected.Path).Destroy())
        {
            Status = $"Could not remove the {ThingName} “{selected.Name}”. The game may still be running.";

            return;
        }

        Status = $"Removed “{selected.Name}”.";

        Rebuild();
    }

    /// <summary>Re-reads the folder.</summary>
    /// <remarks>
    /// Also the answer to mods appearing from outside the launcher: someone dropping a jar into the
    /// folder, or another launcher writing to the same instance.
    /// </remarks>
    [RelayCommand]
    public void Refresh() => Rebuild();

    /// <summary>Whether files can be dropped onto this page.</summary>
    public bool AcceptsDrops => _dropTarget is not null;

    /// <summary>
    /// Takes files dropped onto the window.
    /// </summary>
    /// <remarks>
    /// The page they land on does NOT decide where they go: a jar dropped on the resource-packs page
    /// is still a mod, and filing it by which tab happened to be open would be obeying an accident.
    /// The file itself decides -- see LocalResourceParse.
    /// </remarks>
    public async Task DropFilesAsync(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (_dropTarget is null || files.Count == 0)
        {
            return;
        }

        Status = await _dropTarget.DropAsync(files).ConfigureAwait(true);

        // Re-read from disk rather than trusting what the importer said: the folder is the truth.
        Rebuild();
    }

    /// <summary>Whether the list can be written out.</summary>
    /// <remarks>
    /// Mods only. A resource-pack list is not a thing anybody shares, and upstream offers the export
    /// from the mods page alone.
    /// </remarks>
    public bool CanExportList => _exporter is not null && Kind == ResourceFolderKind.Mods && Mods.Count != 0;

    /// <summary>Writes the installed mod list out in a shareable format.</summary>
    [RelayCommand(CanExecute = nameof(CanExportList))]
    public async Task ExportListAsync()
    {
        if (_exporter is null)
        {
            return;
        }

        /*
         * Built from what is ON SCREEN, so a filtered or sorted list exports as it looks. Disabled
         * mods are included: a list of what is in the folder is more useful than a list of what is
         * switched on, and the file name gives it away anyway.
         */
        var entries = Mods
            .Select(m => new ModPlatform.ModListEntry(
                m.Name,
                m.HomeUrl,
                m.Version,
                m.Authors,
                m.FileName))
            .ToArray();

        await _exporter.ExportAsync(_instanceName, entries).ConfigureAwait(true);
    }

    /// <summary>Whether mods can be downloaded into this folder.</summary>
    /// <remarks>
    /// Needs a game root as well as an installer: the folder is where the files go, and downloading
    /// into an instance that has not been read yet would put them somewhere arbitrary.
    /// </remarks>
    public bool CanAdd => _installer is not null && _gameRoot.Length != 0;

    /// <summary>What the Add button says. Named after the thing, not the verb alone.</summary>
    public string AddLabel => Kind switch
    {
        ResourceFolderKind.ResourcePacks => "Download resource packs",
        ResourceFolderKind.ShaderPacks => "Download shader packs",
        ResourceFolderKind.TexturePacks => "Download texture packs",
        _ => "Download mods",
    };

    /// <summary>Whether this folder can be checked for updates.</summary>
    public bool CanCheckUpdates => _updater is not null
        && Kind == ResourceFolderKind.Mods
        && _gameRoot.Length != 0;

    /// <summary>Opens the update dialog, and re-reads the folder if anything changed.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    public async Task CheckUpdatesAsync()
    {
        if (!CanCheckUpdates)
        {
            return;
        }

        if (await _updater!.CheckAsync(_gameRoot).ConfigureAwait(true))
        {
            // Re-read from disk rather than trusting what the dialog reported, for the same reason
            // installing does: the folder is the truth.
            Rebuild();
        }
    }

    /// <summary>Opens the download dialog and installs whatever comes back.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    public async Task AddAsync()
    {
        if (!CanAdd)
        {
            return;
        }

        var installed = await _installer!.AddAsync(_gameRoot, Kind).ConfigureAwait(true);

        if (installed > 0)
        {
            /*
             * Re-read from disk rather than trusting what the dialog said it fetched. The folder is
             * the truth -- a download that half-succeeded, or a file the user already had, both show
             * up correctly this way and neither does if the list is patched by hand.
             */
            Rebuild();

            Status = installed == 1 ? "Installed 1 file." : $"Installed {installed} files.";
        }
    }

    /// <summary>The folder this page manages -- the one "View folder" opens.</summary>
    /// <remarks>
    /// One folder, not the three the mods page scans: upstream opens the "mods" folder, where anything
    /// modern goes, not "coremods" or "nilmods". The pack kinds each have exactly one folder anyway.
    /// </remarks>
    public string FolderPath => Core.FileSystem.PathCombine(_gameRoot, Kind switch
    {
        ResourceFolderKind.ResourcePacks => "resourcepacks",
        ResourceFolderKind.ShaderPacks => "shaderpacks",
        ResourceFolderKind.TexturePacks => "texturepacks",
        _ => "mods",
    });

    /// <summary>Whether the managed folder can be opened in the file manager.</summary>
    public bool CanOpenFolder => _folders is not null && _gameRoot.Length != 0;

    /// <summary>Opens the managed folder in the desktop's file manager.</summary>
    /// <remarks>
    /// The folder is created first if it is not there, matching upstream's ensureFolderPathExists: a
    /// fresh instance has no "resourcepacks" folder until something is put in one, and opening a
    /// file manager on a path that does not exist is an error rather than an empty window.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    public async Task OpenFolderAsync()
    {
        if (_folders is null || _gameRoot.Length == 0)
        {
            return;
        }

        var path = FolderPath;

        System.IO.Directory.CreateDirectory(path);

        await _folders.OpenAsync(path).ConfigureAwait(true);
    }

    private void Rebuild()
    {
        var selected = Selected?.Path;

        Mods.Clear();

        if (_gameRoot.Length == 0)
        {
            RaiseSelectionDependent();

            return;
        }

        /*
         * Mods are scanned across three folders ("mods", "coremods", "nilmods"); packs live in exactly
         * one each. Shader packs are read as plain resources rather than parsed -- there is no manifest
         * format for them, so the filename is all there is to show.
         */
        var entries = Kind switch
        {
            ResourceFolderKind.ResourcePacks => ResourceFolder.LoadResourcePacks(
                FileSystem.PathCombine(_gameRoot, "resourcepacks")),
            ResourceFolderKind.ShaderPacks => ResourceFolder.Load(
                FileSystem.PathCombine(_gameRoot, "shaderpacks"),
                path => new Resource(path)),
            ResourceFolderKind.TexturePacks => ResourceFolder.LoadTexturePacks(
                FileSystem.PathCombine(_gameRoot, "texturepacks")),
            _ => ResourceFolder.LoadAllMods(_gameRoot),
        };

        /*
         * Enabled first, then by name. Upstream sorts by whichever column was clicked; with no column
         * headers yet, this is the order that answers the question people actually open this page with
         * -- "what is this instance running?"
         */
        foreach (var entry in entries.Values
                     .OrderByDescending(e => e.Resource.Enabled)
                     .ThenBy(
                         e => e.Resource is Mod m ? m.DisplayName : e.Resource.Name,
                         StringComparer.OrdinalIgnoreCase))
        {
            var resource = entry.Resource;

            Mods.Add(new ModViewModel
            {
                Path = resource.Path,

                /*
                 * The mod's OWN declared name where it has one, falling back to the filename. "Sodium"
                 * rather than "sodium-fabric-0.5.3+mc1.20.1" -- and a jar whose metadata will not parse
                 * still gets a row, named after its file, because a mod the launcher cannot read is
                 * exactly the one a user is looking for.
                 */
                Name = resource is Mod mod ? mod.DisplayName : resource.Name,
                Version = resource is Mod withVersion ? withVersion.Details.Version : string.Empty,
                IsEnabled = resource.Enabled,
                SizeString = resource.SizeString,

                // Read off the jar, and empty rather than absent when it will not parse -- a mod the
                // launcher cannot read still belongs in an exported list.
                Authors = resource is Mod withAuthors ? withAuthors.Details.Authors.ToArray() : [],
                HomeUrl = resource is Mod withUrl ? withUrl.Details.HomeUrl : string.Empty,
            });
        }

        if (selected is not null)
        {
            Select(selected);
        }

        RaiseSelectionDependent();
    }

    private void RaiseSelectionDependent()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ToggleLabel));

        ToggleSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
}
