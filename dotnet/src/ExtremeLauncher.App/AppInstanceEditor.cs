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
 * Builds an instance window and its pages. The app owns this because the app owns windows; everything
 * it assembles is a view model that has been tested without one.
 *
 * ONE WINDOW PER INSTANCE. Two windows over the same instance.cfg would each hold their own copy of
 * the settings and the last one saved would silently win. Pressing Edit again brings the existing
 * window forward instead.
 *
 * THE PROFILE IS READ WITHOUT RESOLVING IT. Opening the version page must not need the metadata server
 * -- a user looking at an instance that will not launch is often looking precisely because the network
 * or the metadata is the problem. Components load from mmc-pack.json alone; those with no local
 * metadata show as "(not loaded)" rather than blocking the window on a download.
 */

using Avalonia.Controls;
using Avalonia.Threading;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppInstanceEditor : IInstanceEditor
{
    private readonly Func<Window?> _owner;

    private readonly IUserPrompts _prompts;

    private readonly Dictionary<string, InstanceWindow> _open = new(StringComparer.Ordinal);

    private readonly LaunchRegistry _registry;

    private readonly IClipboard _clipboard;

    private readonly IVersionListSource? _versions;

    private readonly Func<InstanceRecord, IModInstaller?>? _installers;

    private readonly IModListExporter? _listExporter;

    private readonly Func<InstanceRecord, IModUpdater?>? _updaters;

    private readonly ILogUploader? _uploader;

    private readonly IPackUpdateChecker? _packUpdates;

    private readonly HttpClient? _client;

    private readonly LauncherLog? _log;

    private readonly IFolderOpener? _folders;

    /// <param name="versions">
    /// Where the Change version and Add loader dialogs get their lists. Null leaves those buttons
    /// disabled rather than opening a dialog that can only ever say "no metadata source".
    /// </param>
    /// <param name="installers">
    /// Builds a mod installer for one instance. A function rather than an object because the search
    /// has to be filtered by THAT instance's Minecraft version and loader.
    /// </param>
    public AppInstanceEditor(
        Func<Window?> owner,
        IUserPrompts prompts,
        LaunchRegistry registry,
        IVersionListSource? versions = null,
        Func<InstanceRecord, IModInstaller?>? installers = null,
        Func<InstanceRecord, IModUpdater?>? updaters = null,
        IModListExporter? listExporter = null,
        ILogUploader? uploader = null,
        IPackUpdateChecker? packUpdates = null,
        HttpClient? client = null,
        LauncherLog? log = null,
        IFolderOpener? folders = null)
    {
        _owner = owner;
        _prompts = prompts;
        _registry = registry;
        _versions = versions;
        _installers = installers;
        _listExporter = listExporter;
        _updaters = updaters;
        _uploader = uploader;
        _packUpdates = packUpdates;
        _client = client;
        _log = log;
        _folders = folders;

        // Reached through whichever window is asking, which in Avalonia is where the clipboard lives.
        _clipboard = new AppClipboard(owner);
    }

    public void Open(InstanceRecord instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (_open.TryGetValue(instance.Id, out var existing))
        {
            existing.Activate();

            return;
        }

        /*
         * THE INSTANCE WINDOW IS ALSO THE LAUNCH WINDOW, which is upstream's arrangement: it carries
         * Launch and Kill, and jumps to the log page the moment a game starts. The coordinator comes
         * from the registry, so this window and the main window are looking at the same running game
         * rather than two ideas of one.
         */
        var viewModel = new InstanceWindowViewModel(instance.Name, _prompts, _registry.For(instance.Id))
        {
            InstanceId = instance.Id,
        };

        /*
         * The version page first, because it is the one people come here for -- and the one that can
         * break the instance, so it should not be somewhere you arrive at by accident.
         */
        var profile = new PackProfile(LauncherService.CurrentRuntimeContext());

        if (profile.Load(instance.Paths.PackProfilePath))
        {
            var version = new VersionPageViewModel(
                _versions is null ? null : new AppVersionChooser(_owner, _versions),
                _packUpdates,

                /*
                 * BOUND TO THIS INSTANCE. Unlike the checker, which is one object for the whole
                 * launcher, an updater deletes files in one particular folder -- so it is built per
                 * window and cannot be pointed at the wrong instance.
                 */
                _client is null
                    ? null
                    : new AppPackUpdater(
                        _owner,
                        _client,
                        _prompts,
                        instance.Paths,
                        instance.Settings.Settings,
                        instance.Settings,
                        _log),
                _folders,
                new AppNewComponentPrompt(_owner));

            version.Load(
                profile,
                instance.Paths.PackProfilePath,
                instance.Paths.GameRoot,
                instance.Paths.LocalLibraryPath);
            version.LoadProvenance(instance.Settings);
            viewModel.AddPage(version);
        }

        /*
         * Mods next, because it is the page people actually come here for. It needs nothing but the
         * game directory, so it is present even when the pack file will not read.
         */
        /*
         * The installer is built PER INSTANCE, because the search has to be filtered by that
         * instance's Minecraft version and loader -- a 1.20.1 Fabric instance offered Forge mods for
         * 1.7.10 is worse than no search at all.
         *
         * The profile is read above; the loader is whichever loader component it carries.
         */
        var installer = _installers?.Invoke(instance);

        var mods = new ModsPageViewModel(
            ResourceFolderKind.Mods,
            installer,
            _updaters?.Invoke(instance),
            _listExporter,

            // The instance's name, which becomes the suggested file name for the exported list.
            instance.Name,

            // Files dropped anywhere on this window land in this instance, filed by what they are.
            new AppResourceDropTarget(instance.Paths),
            _folders);

        mods.Load(instance.Paths.GameRoot);
        viewModel.AddPage(mods);

        /*
         * Worlds next: the page holding the only data in an instance that exists nowhere else, which is
         * also why its delete asks and the mods page's does not.
         */
        var worlds = new WorldsPageViewModel(
            _prompts, new AppTaskRunner(_owner), _clipboard, new AppWorldFilePicker(_owner), _folders);

        worlds.Load(instance.Paths.GameRoot);
        viewModel.AddPage(worlds);

        foreach (var kind in new[] { ResourceFolderKind.ResourcePacks, ResourceFolderKind.ShaderPacks })
        {
            var packs = new ModsPageViewModel(kind, installer, folders: _folders);

            packs.Load(instance.Paths.GameRoot);
            viewModel.AddPage(packs);
        }

        /*
         * Texture packs are the pre-1.6 format, and upstream shows the page only for an instance whose
         * resolved profile carries the "texturepacks" trait. That trait comes from resolving the
         * component stack, which this window does not do at open time (it needs the metadata server for
         * a cold instance). So the page appears when the instance actually HAS a texturepacks folder --
         * a deliberate divergence: it means a modern instance never shows the page, and a legacy one
         * that already has texture packs on disk does, which is the case that matters. No installer:
         * the port has no download source for the legacy format, so the button is absent rather than
         * present and inert.
         */
        if (Directory.Exists(FileSystem.PathCombine(instance.Paths.GameRoot, "texturepacks")))
        {
            var texturePacks = new ModsPageViewModel(ResourceFolderKind.TexturePacks, folders: _folders);

            texturePacks.Load(instance.Paths.GameRoot);
            viewModel.AddPage(texturePacks);
        }

        /*
         * The game's own settings. After the pack pages because it is a thing you go looking for
         * rather than a thing you browse -- and it is only useful once the game has run once.
         */
        var gameOptions = new GameOptionsPageViewModel();

        gameOptions.Load(instance.Paths.GameRoot);
        viewModel.AddPage(gameOptions);

        var screenshots = new ScreenshotsPageViewModel(
            _prompts,
            _client is null ? null : new AppScreenshotUploader(_client, _log),
            _clipboard,
            _folders);

        screenshots.Load(instance.Paths.GameRoot);
        viewModel.AddPage(screenshots);

        /*
         * The multiplayer list. Editable now, but locked while the game is running: servers.dat is a
         * file the game owns and rewrites whole on exit. The prompts are for the removal
         * confirmation -- an address nobody wrote down elsewhere is gone for good.
         */
        // Join launches through the same per-instance coordinator the window's Launch button uses, so
        // the window locks its pages and jumps to the log exactly as it does for a normal launch.
        var servers = new ServersPageViewModel(
            _prompts, new CoordinatorServerJoiner(_registry.For(instance.Id), instance.Id));

        servers.Load(instance.Paths.GameRoot);
        viewModel.AddPage(servers);

        /*
         * Settings and Notes both need nothing but instance.cfg, so they are there even when the pack
         * file will not read -- which is when someone is most likely to be changing the Java path.
         */
        var settings = new SettingsPageViewModel();

        settings.Load(instance.Settings);
        viewModel.AddPage(settings);

        /*
         * The log last in the list and first to be shown when a game starts. Bound to the coordinator's
         * own lines, which are already bounded and already marshalled onto the UI thread.
         */
        viewModel.AddPage(new LogPageViewModel(_registry.For(instance.Id), _clipboard, _uploader, _prompts));

        /*
         * The game's OWN logs, beside the launcher's. The log page above shows this session; this one
         * shows yesterday's, which is what somebody has when the launcher was closed and reopened
         * between the crash and the question about it.
         */
        var otherLogs = new OtherLogsPageViewModel(
            action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
            _clipboard,
            _uploader,
            _prompts);

        otherLogs.Load(instance.Paths.GameRoot);
        viewModel.AddPage(otherLogs);

        // Notes needs nothing but instance.cfg, so it is there even when the pack file will not read.
        var notes = new NotesPageViewModel();

        notes.Load(instance.Settings);
        viewModel.AddPage(notes);

        var window = new InstanceWindow { DataContext = viewModel };

        _open[instance.Id] = window;

        // Forgotten on close, so the next Edit opens a window reading fresh files rather than reviving
        // one holding stale ones.
        window.Closed += (_, _) => _open.Remove(instance.Id);

        if (_owner() is { } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }
}
