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
 * The window's own state, as distinct from the list it shows.
 *
 * WHAT A COMMAND CAN DO IS A PROPERTY OF THE SELECTION, and that is the only interesting thing here.
 * Upstream re-derives it by hand: MainWindow::selectionBad() and a scattering of setEnabled() calls
 * that each have to remember the same rules. Expressed once, as properties, so the window binds to
 * them and cannot disagree with itself -- a Launch button enabled for an instance that cannot start is
 * a bug reachable by clicking.
 */

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <param name="post">
    /// Runs an action on the UI thread, for work that reports from worker threads. Defaults to running
    /// inline, which is right for tests and headless callers.
    /// </param>
    /// <param name="registry">
    /// The per-instance coordinators, shared with the instance windows. Made here when not supplied,
    /// which is what the tests do.
    /// </param>
    /// <param name="editor">
    /// Opens the instance window. Null in a build with no window to open, where Edit stays disabled
    /// rather than becoming a button that does nothing.
    /// </param>
    public MainWindowViewModel(
        IInstanceLauncher? launcher = null,
        IUserPrompts? prompts = null,
        Action<Action>? post = null,
        IInstanceEditor? editor = null,
        LaunchRegistry? registry = null,
        IInstanceCreator? creator = null,
        IPackImporter? importer = null,
        IPackBrowser? browser = null,
        ILauncherLogViewer? launcherLog = null,
        IAccountsUi? accounts = null,
        IGlobalSettingsUi? settings = null,
        IIconChooser? icons = null,
        IInstanceExporter? exporter = null,
        IAboutUi? about = null,
        IFolderOpener? folders = null,
        IShortcutMaker? shortcuts = null,
        ILinkOpener? links = null,
        IMetadataCache? metadata = null,
        INewsUi? news = null,
        NewsChecker? newsChecker = null,
        IInstanceCopyPrompt? copyPrompt = null)
    {
        _editor = editor;
        _creator = creator;
        _importer = importer;
        _browser = browser;
        _launcherLog = launcherLog;
        _accounts = accounts;
        _settings = settings;
        _icons = icons;
        _exporter = exporter;
        _about = about;
        _folders = folders;
        _shortcuts = shortcuts;
        _links = links;
        _metadata = metadata;
        _news = news;

        News = new NewsViewModel(newsChecker, post);

        // The toolbar button follows the fetch, which finishes long after this constructor does.
        News.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanShowNews));

        Instances = new InstanceListViewModel();

        /*
         * ONE COORDINATOR PER INSTANCE, from the registry. There used to be a single one here, which
         * refused to start a second instance while any other was running -- for a conflict that only
         * exists between two launches of the SAME instance. Upstream has no such limit. See
         * LaunchRegistry.
         */
        /*
         * Taken from the caller where there is one, because the instance windows need the SAME registry:
         * two of them would mean the main window and the instance window each holding their own idea of
         * which games are running.
         */
        Registry = registry ?? new LaunchRegistry(launcher);

        /*
         * The status strip follows whichever instance was started most recently. It POINTS AT that
         * instance's coordinator rather than being one: an earlier version of this kept a second
         * coordinator alongside the registry's and awaited both, which started the game twice.
         */
        Launch = Registry.For(string.Empty);
        // Kept as well as handed on: renaming and grouping ask their own questions, and routing them
        // through Removal or Duplication would be borrowing an object for its constructor argument.
        _prompts = prompts;

        Removal = new InstanceRemoval(prompts);
        Duplication = new InstanceDuplication(prompts, post, copyPrompt);

        Duplication.PropertyChanged += (_, _) => RaiseSelectionDependent();

        // Undo appearing and disappearing changes what the toolbar may do.
        Removal.PropertyChanged += (_, _) => RaiseSelectionDependent();

        // The window's own enabled-state follows the selection, wherever the change came from.
        Instances.PropertyChanged += (_, _) => RaiseSelectionDependent();

        // ...and a launch starting or finishing changes what the buttons may do.
        PropertyChangedEventHandler initial = (_, _) => RaiseSelectionDependent();

        Launch.PropertyChanged += initial;
        _launchSubscription = () => Launch.PropertyChanged -= initial;
    }

    private readonly IInstanceEditor? _editor;

    private readonly IInstanceCreator? _creator;

    private readonly IPackImporter? _importer;

    private readonly IPackBrowser? _browser;

    private readonly ILauncherLogViewer? _launcherLog;

    private readonly IAccountsUi? _accounts;

    private readonly IGlobalSettingsUi? _settings;

    private readonly IIconChooser? _icons;

    private readonly IInstanceExporter? _exporter;

    private readonly IAboutUi? _about;

    private readonly IFolderOpener? _folders;

    private readonly IShortcutMaker? _shortcuts;

    private readonly ILinkOpener? _links;

    private readonly IMetadataCache? _metadata;

    private readonly INewsUi? _news;

    private readonly IUserPrompts? _prompts;

    /// <summary>Undoes the status strip's subscription to the previous instance's coordinator.</summary>
    private Action? _launchSubscription;

    public InstanceListViewModel Instances { get; }

    /// <summary>The per-instance coordinators. What the instance windows launch through.</summary>
    public LaunchRegistry Registry { get; }

    /// <summary>
    /// The coordinator the main window's status strip follows: whichever instance it last started.
    /// </summary>
    /// <remarks>
    /// One strip, so one coordinator at a time -- but it is the registry's own object for that
    /// instance, not a copy. Two packs can run at once; the strip shows the most recent, and the
    /// instance windows show the rest.
    ///
    /// The placeholder before anything has been launched is the registry's entry for the empty id,
    /// which nothing can ever start: LaunchAsync refuses an empty instance id.
    /// </remarks>
    [ObservableProperty]
    private LaunchCoordinator _launch;

    public InstanceRemoval Removal { get; }

    public InstanceDuplication Duplication { get; }

    /// <summary>The instance the buttons act on, or null.</summary>
    public InstanceItemViewModel? Selected => Instances.Selected;

    /// <summary>Whether anything is selected at all.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>
    /// Whether the selected instance can be started.
    /// </summary>
    /// <remarks>
    /// An UNSUPPORTED instance is selectable and not launchable — the launcher can read enough of it
    /// to list it and not enough to run it. Hiding it would be worse: the user would see an instance
    /// they made simply missing.
    /// </remarks>
    public bool CanLaunch => Selected is { IsSupported: true } selected
                             && !Registry.IsRunning(selected.Id);

    /// <summary>Whether the selected instance can be renamed, copied, deleted and so on.</summary>
    /// <remarks>
    /// True even for an unsupported instance, deliberately. Deleting or renaming one does not require
    /// understanding it, and refusing would leave a user unable to clear up an instance the launcher
    /// has already told them is broken.
    /// </remarks>
    public bool CanEdit => HasSelection && !Launch.IsBusy;

    /// <summary>Whether the last launch left a message worth showing.</summary>
    /// <remarks>
    /// Separate from the message itself because a binding cannot ask "is this string empty" without a
    /// converter, and a converter for a question the view model can answer is a converter too many.
    /// </remarks>
    public bool HasFailure => Launch.Failure.Length != 0 && !Launch.IsBusy;

    /// <summary>Whether the progress bar should sweep rather than fill.</summary>
    /// <remarks>
    /// Work with no known total gets a sweeping bar rather than one stuck at zero, which reads as
    /// "nothing is happening" -- and resolving a version genuinely has no total until it is done.
    /// </remarks>
    public bool HasIndeterminateProgress => Launch.IsBusy && Launch.Progress is null;

    /// <summary>What the window's title bar shows.</summary>
    public string Title => Selected is { } selected
        ? $"Extreme Launcher - {selected.Name}"
        : "Extreme Launcher";

    /// <summary>
    /// Whether the selected instance can be deleted.
    /// </summary>
    /// <remarks>
    /// An UNSUPPORTED instance is deletable, deliberately -- clearing up something the launcher has
    /// already told the user is broken is exactly when they need this. It is refused while a launch is
    /// running, because the files are in use.
    /// </remarks>
    public bool CanDelete => CanEdit;

    /// <summary>
    /// Whether the selected instance can be opened for editing.
    /// </summary>
    /// <remarks>
    /// False when no editor was supplied, so a build without the instance window shows a disabled
    /// button rather than a live one that does nothing -- the failure this port keeps finding.
    ///
    /// An UNSUPPORTED instance is editable: its version page is exactly where a user would go to see
    /// why the launcher cannot read it.
    /// </remarks>
    public bool CanOpenEditor => _editor is not null && CanEdit;

    /// <summary>Opens the instance window for the selected instance.</summary>
    [RelayCommand]
    public void EditSelected()
    {
        if (!CanOpenEditor || Selected is not { } selected || Instances.Source is not { } list)
        {
            return;
        }

        if (list.GetInstanceById(selected.Id) is { } instance)
        {
            _editor!.Open(instance);
        }
    }

    /// <summary>Whether a new instance can be made.</summary>
    /// <remarks>
    /// False with no creator supplied, so a build without the dialog shows a disabled button rather
    /// than a live one that does nothing. Unlike every other button here it does NOT need a selection:
    /// the first thing a new user does is press it with an empty list.
    /// </remarks>
    public bool CanCreateInstance => _creator is not null && !Launch.IsBusy;

    /// <summary>Makes a new instance.</summary>
    [RelayCommand]
    public async Task NewInstanceAsync()
    {
        if (!CanCreateInstance || Instances.Source is not { } list)
        {
            return;
        }

        var created = await _creator!.CreateAsync(list).ConfigureAwait(true);

        Instances.Reload();

        if (created.Length != 0)
        {
            // Selected, so the next thing the user does lands on what they just made.
            Instances.Select(created);
        }

        RaiseSelectionDependent();
    }

    /// <summary>Whether a modpack can be imported.</summary>
    /// <remarks>
    /// Like New, this needs no selection: importing a pack is the other way somebody gets their first
    /// instance, and quite often the only way they ever make one.
    /// </remarks>
    public bool CanImportPack => _importer is not null && !Launch.IsBusy;

    /// <summary>Whether the launcher's own log can be shown.</summary>
    /// <remarks>
    /// NOT BLOCKED BY A LAUNCH, unlike most of this window. Reading the log while a game starts is
    /// exactly when somebody wants to -- and the window only reads files.
    /// </remarks>
    public bool CanShowLauncherLog => _launcherLog is not null;

    /// <summary>Opens the launcher's own log.</summary>
    [RelayCommand(CanExecute = nameof(CanShowLauncherLog))]
    public async Task ShowLauncherLogAsync()
    {
        if (_launcherLog is not null)
        {
            await _launcherLog.ShowAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Whether the modpack browser can be opened.</summary>
    public bool CanBrowsePacks => _browser is not null && !Launch.IsBusy;

    /// <summary>
    /// Searches a platform for a modpack and installs it.
    /// </summary>
    /// <remarks>
    /// THE OTHER HALF OF IMPORTING, and the better half: a pack installed this way knows its own
    /// project id, so something can later tell the player a newer version exists. A pack imported
    /// from a file never can.
    /// </remarks>
    [RelayCommand]
    public async Task BrowsePacksAsync()
    {
        if (!CanBrowsePacks || Instances.Source is not { } list)
        {
            return;
        }

        var installed = await _browser!.BrowseAndInstallAsync(list).ConfigureAwait(true);

        Instances.Reload();

        if (installed.Length != 0)
        {
            Instances.Select(installed);
        }

        RaiseSelectionDependent();
    }

    /// <summary>Imports a modpack from a file.</summary>
    [RelayCommand]
    public async Task ImportPackAsync()
    {
        if (!CanImportPack || Instances.Source is not { } list)
        {
            return;
        }

        var imported = await _importer!.ImportAsync(list).ConfigureAwait(true);

        Instances.Reload();

        if (imported.Length != 0)
        {
            Instances.Select(imported);
        }

        RaiseSelectionDependent();
    }

    /// <summary>
    /// Whether the accounts window can be opened.
    /// </summary>
    /// <remarks>
    /// Needs no selection and is not blocked by a launch in progress -- unlike every other button
    /// here. Signing in while a game starts is a perfectly reasonable thing to want, and the accounts
    /// window touches nothing the launch is using.
    /// </remarks>
    public bool CanOpenAccounts => _accounts is not null;

    /// <summary>Who the next launch will play as, for the toolbar to show.</summary>
    public string CurrentAccountLabel => _accounts?.CurrentAccountName is { Length: > 0 } name
        ? name
        : "No account";

    /// <summary>Opens the accounts window.</summary>
    [RelayCommand]
    public async Task OpenAccountsAsync()
    {
        if (_accounts is null)
        {
            return;
        }

        await _accounts.OpenAsync().ConfigureAwait(true);

        // The default may have changed while it was open, and the toolbar says who is playing.
        OnPropertyChanged(nameof(CurrentAccountLabel));
    }

    /// <summary>
    /// Whether the settings window can be opened.
    /// </summary>
    /// <remarks>
    /// Needs no selection, and is not blocked by a launch: nothing here is read again until the next
    /// launch starts, so editing during one changes nothing under the running game.
    /// </remarks>
    public bool CanOpenSettings => _settings is not null;

    /// <summary>Opens the global settings window.</summary>
    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        if (_settings is null)
        {
            return;
        }

        await _settings.OpenAsync().ConfigureAwait(true);
    }

    /// <summary>Whether the About window can be opened. Needs no selection and never goes away.</summary>
    public bool CanShowAbout => _about is not null;

    /// <summary>Opens the About window.</summary>
    [RelayCommand]
    public async Task ShowAboutAsync()
    {
        if (_about is not null)
        {
            await _about.OpenAsync().ConfigureAwait(true);
        }
    }

    // ================================================================== renaming and grouping

    /// <summary>Whether the selected instance can be renamed.</summary>
    public bool CanRename => _prompts is not null && Instances.Selected is not null;

    /// <summary>Renames the selected instance.</summary>
    [RelayCommand]
    public async Task RenameSelectedAsync()
    {
        if (!CanRename || Instances.Selected is not { } selected || Instances.Source is not { } list)
        {
            return;
        }

        var name = await _prompts!.PromptForTextAsync(
            "Rename instance",
            "What should this instance be called?",
            selected.Name).ConfigureAwait(true);

        /*
         * NULL AND EMPTY ARE DIFFERENT ANSWERS. Null is "cancelled"; empty is a name somebody
         * actually typed, and an instance with no name is a blank tile they cannot find again.
         */
        if (name is null)
        {
            return;
        }

        if (name.Trim().Length == 0)
        {
            Launch.Log("An instance needs a name.", isError: true);

            return;
        }

        if (list.Instances.FirstOrDefault(i => i.Id == selected.Id) is { } record)
        {
            // Written through: InstanceSettings persists on set, so instance.cfg has it already.
            record.Settings.Name = name.Trim();
        }

        Instances.Reload();
        Instances.Select(selected.Id);

        RaiseSelectionDependent();
    }

    /// <summary>Whether the selected instance can be moved to another group.</summary>
    public bool CanChangeGroup => _prompts is not null && Instances.Selected is not null;

    /// <summary>Moves the selected instance into a group.</summary>
    [RelayCommand]
    public async Task ChangeGroupAsync()
    {
        if (!CanChangeGroup || Instances.Selected is not { } selected || Instances.Source is not { } list)
        {
            return;
        }

        var existing = list.GetGroups();

        var message = existing.Count == 0
            ? "Type a group name, or leave it empty for none."
            : "Type a group name, or leave it empty for none. Groups so far: " + string.Join(", ", existing);

        var group = await _prompts!.PromptForTextAsync(
            "Change group",
            message,
            selected.Group).ConfigureAwait(true);

        if (group is null)
        {
            return;
        }

        // EMPTY IS MEANINGFUL HERE, unlike renaming: it is how an instance leaves a group.
        list.SetInstanceGroup(selected.Id, group.Trim());
        list.SaveGroupList();

        Instances.Reload();
        Instances.Select(selected.Id);

        RaiseSelectionDependent();
    }

    /// <summary>Whether a desktop shortcut can be made for the selection.</summary>
    public bool CanCreateShortcut => _shortcuts is not null && Instances.Selected is not null;

    /// <summary>Creates a desktop shortcut that launches the selected instance directly.</summary>
    /// <remarks>
    /// It works because `--launch &lt;id&gt;` is a supported, tested command line: the shortcut is only a
    /// file that types it. That is the difference between "open the launcher, find the instance,
    /// press play" and "double-click the thing on the desktop".
    /// </remarks>
    [RelayCommand]
    public async Task CreateShortcutAsync()
    {
        if (!CanCreateShortcut || Instances.Selected is not { } selected)
        {
            return;
        }

        var result = await _shortcuts!.CreateAsync(selected.Id, selected.Name, selected.IconKey)
            .ConfigureAwait(true);

        // Reported either way. A shortcut is a file somebody then has to go and find, so "where"
        // matters as much as "done" -- and a failure here is usually a permissions problem worth
        // naming rather than a silent no-op.
        Launch.Log(result.Message, isError: !result.Succeeded);
    }

    // ================================================================== help and housekeeping

    /// <summary>
    /// The links this build actually has.
    /// </summary>
    /// <remarks>
    /// BUILT FROM WHAT IS CONFIGURED, because most of these are empty by default -- upstream's
    /// CMakeLists leaves the Discord, Matrix and subreddit URLs blank for a fork to fill in. A menu
    /// entry that opens nothing is worse than an absent one: it looks like the launcher is broken
    /// rather than like the fork has no Discord.
    /// </remarks>
    public IReadOnlyList<LauncherLink> Links
    {
        get
        {
            var config = BuildConfig.Instance;

            var candidates = new (string Label, string Url)[]
            {
                ("Help", config.HelpUrl),
                ("Report a bug", config.BugTrackerUrl),
                ("Source code", config.LauncherGit),
                ("Translate", config.TranslationsUrl),
                ("Discord", config.DiscordUrl),
                ("Matrix", config.MatrixUrl),
                ("Reddit", config.SubredditUrl),
            };

            return candidates
                .Where(c => c.Url.Length != 0)
                .Select(c => new LauncherLink(c.Label, c.Url))
                .ToArray();
        }
    }

    public bool CanOpenLinks => _links is not null && Links.Count != 0;

    /// <summary>Opens one of the launcher's own links.</summary>
    [RelayCommand]
    public async Task OpenLinkAsync(string? url)
    {
        /*
         * The URL comes from BuildConfig -- a compile-time constant of this build, not from the
         * network and not from anything a user typed. The opener still checks the scheme, because a
         * fork could configure anything here and "it came from our own config" is a weaker guarantee
         * than "it is http or https".
         */
        if (_links is not null && url is { Length: > 0 })
        {
            await _links.OpenAsync(url).ConfigureAwait(true);
        }
    }

    /// <summary>The news toolbar: a headline, and whether it can be clicked.</summary>
    public NewsViewModel News { get; }

    /// <remarks>
    /// Both halves matter. No window to open means the button does nothing; no entries means there
    /// is nothing to put in it, which is upstream's rule -- it hides More News in that case too.
    /// </remarks>
    public bool CanShowNews => _news is not null && News.CanShow;

    /// <summary>Opens the news window.</summary>
    /// <param name="fromHeadline">
    /// True when the headline itself was clicked rather than "More news". Upstream opens the article
    /// list collapsed in that case: clicking a story means "show me that story".
    /// </param>
    [RelayCommand]
    public async Task ShowNewsAsync(bool fromHeadline = false)
    {
        if (_news is not null && News.Entries.Count != 0)
        {
            await _news.ShowAsync([.. News.Entries], fromHeadline).ConfigureAwait(true);
        }
    }

    /// <summary>Whether the metadata cache can be cleared.</summary>
    public bool CanClearMetadata => _metadata is not null;

    /// <summary>
    /// Throws away the cached version metadata.
    /// </summary>
    /// <remarks>
    /// The support action of last resort, and upstream has it for the same reason: a half-written or
    /// out-of-date metadata cache produces resolve failures that look like the launcher being broken,
    /// and the fix is to delete it and fetch again. Nothing is lost -- every byte of it is
    /// re-downloadable.
    /// </remarks>
    [RelayCommand]
    public async Task ClearMetadataAsync()
    {
        if (_metadata is null)
        {
            return;
        }

        if (_prompts is not null && !await _prompts.ConfirmAsync(
            "Clear the metadata cache?",
            "The launcher will download version information again the next time it needs it. "
            + "Nothing about your instances is affected.",
            "Clear").ConfigureAwait(true))
        {
            return;
        }

        Launch.Log(await _metadata.ClearAsync().ConfigureAwait(true));
    }

    // ================================================================== folders

    /// <summary>Whether folders can be opened at all in this build.</summary>
    public bool CanOpenFolders => _folders is not null;

    /// <summary>Whether the selected instance's own folder can be opened.</summary>
    public bool CanOpenInstanceFolder => _folders is not null && Instances.Selected is not null;

    /// <summary>Opens the selected instance's folder in the file manager.</summary>
    [RelayCommand]
    public async Task OpenInstanceFolderAsync()
    {
        if (!CanOpenInstanceFolder || Instances.Selected is not { } selected || Instances.Source is not { } list)
        {
            return;
        }

        if (list.Instances.FirstOrDefault(i => i.Id == selected.Id) is { } record)
        {
            await _folders!.OpenAsync(record.Paths.InstanceRoot).ConfigureAwait(true);
        }
    }

    /// <summary>Opens one of the launcher's own folders.</summary>
    /// <remarks>
    /// Takes the KIND rather than a path, so the view model never has to know where anything is --
    /// the app resolves it from LauncherPaths, which is the single place that decides.
    /// </remarks>
    [RelayCommand]
    public async Task OpenFolderAsync(string? kind)
    {
        if (_folders is null || kind is not { Length: > 0 })
        {
            return;
        }

        await _folders.OpenKnownAsync(kind).ConfigureAwait(true);
    }

    /// <summary>Whether the selected instance's icon can be changed.</summary>
    public bool CanChangeIcon => _icons is not null && Instances.Selected is not null;

    /// <summary>Changes the selected instance's icon.</summary>
    [RelayCommand]
    public async Task ChangeIconAsync()
    {
        if (!CanChangeIcon || Instances.Selected is not { } selected || Instances.Source is not { } list)
        {
            return;
        }

        var chosen = await _icons!.ChooseAsync(selected.IconKey).ConfigureAwait(true);

        if (chosen.Length == 0 || string.Equals(chosen, selected.IconKey, StringComparison.Ordinal))
        {
            return;
        }

        /*
         * WRITTEN TO instance.cfg, then the list re-read. Patching the tile in place would show the
         * new icon for an instance whose file still says otherwise -- and the next restart would
         * silently put the old one back.
         */
        if (list.Instances.FirstOrDefault(i => i.Id == selected.Id) is { } record)
        {
            // The setter writes through: IniSettingsObject persists on Set, so there is no separate
            // save step. The instance.cfg on disk has the new key before the next line runs.
            record.Settings.IconKey = chosen;
        }

        Instances.Reload();
        Instances.Select(selected.Id);

        RaiseSelectionDependent();
    }

    /// <summary>Whether the selected instance can be exported.</summary>
    public bool CanExport => _exporter is not null && Instances.Selected is not null;

    /// <summary>Exports the selected instance as an archive or a modpack.</summary>
    [RelayCommand]
    public async Task ExportSelectedAsync()
    {
        if (!CanExport || Instances.Selected is not { } selected || Instances.Source is not { } list)
        {
            return;
        }

        if (list.Instances.FirstOrDefault(i => i.Id == selected.Id) is { } record)
        {
            await _exporter!.ExportAsync(record).ConfigureAwait(true);
        }
    }

    /// <summary>Whether the selected instance can be copied.</summary>
    /// <remarks>
    /// An UNSUPPORTED instance is copyable: duplicating a directory needs no understanding of what is
    /// in it, and someone whose instance the launcher cannot read may well want a backup before
    /// touching it.
    /// </remarks>
    public bool CanCopy => HasSelection && !Launch.IsBusy && !Duplication.IsBusy;

    /// <summary>Whether the copy has anything to say.</summary>
    public bool HasDuplicationStatus => Duplication.Status.Length != 0;

    /// <summary>Whether the copy's bar should sweep rather than fill.</summary>
    /// <remarks>
    /// Same reasoning as the launch's: work with no known total gets a sweeping bar rather than one
    /// stuck at zero, which reads as "nothing is happening". A copy spends its first moments deciding
    /// on a strategy and counting files, with no total to report.
    /// </remarks>
    public bool HasIndeterminateCopyProgress => Duplication.IsBusy && Duplication.Progress is null;

    /// <summary>Copies the selected instance, after asking for a name.</summary>
    [RelayCommand]
    public async Task CopySelectedAsync()
    {
        if (!CanCopy || Selected is not { } selected || Instances.Source is not { } list)
        {
            return;
        }

        var made = await Duplication.CopyAsync(list, selected.Id, selected.Name).ConfigureAwait(true);

        Instances.Reload();

        if (made.Length != 0)
        {
            // Selected, so the next thing the user does lands on the copy rather than the original --
            // which is the whole reason for copying it.
            Instances.Select(made);
        }

        RaiseSelectionDependent();
    }

    /// <summary>Whether there is a deleted instance that can still be brought back.</summary>
    public bool CanUndoDelete => Removal.CanUndo && !Launch.IsBusy;

    /// <summary>Whether the removal has anything to say.</summary>
    public bool HasRemovalStatus => Removal.Status.Length != 0;

    /// <summary>
    /// Deletes the selected instance, after asking.
    /// </summary>
    /// <remarks>
    /// Guarded by the same property the button binds to rather than trusting the button, as the launch
    /// command is -- and here the cost of being wrong is somebody's worlds.
    /// </remarks>
    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (!CanDelete || Selected is not { } selected || Instances.Source is not { } list)
        {
            return;
        }

        var outcome = await Removal.RemoveAsync(list, selected.Id, selected.Name).ConfigureAwait(true);

        if (outcome is RemovalOutcome.Trashed or RemovalOutcome.Deleted)
        {
            // Gone from disk, so gone from the window: a tile for an instance that no longer exists is
            // a click that fails.
            Instances.Select(null);
            Instances.Reload();
        }

        RaiseSelectionDependent();
    }

    /// <summary>Puts back the last deleted instance.</summary>
    [RelayCommand]
    public void UndoDelete()
    {
        if (!CanUndoDelete || Instances.Source is not { } list)
        {
            return;
        }

        Removal.Undo(list);
        Instances.Reload();

        RaiseSelectionDependent();
    }

    /// <summary>Selects an instance, or clears the selection.</summary>
    [RelayCommand]
    public void Select(string? id)
    {
        Instances.Select(id);

        RaiseSelectionDependent();
    }

    /// <summary>Whether the selected instance is running and so can be killed.</summary>
    /// <remarks>
    /// The mirror of <see cref="CanLaunch"/>: running rather than not. Upstream's actionKillInstance,
    /// for stopping a game -- a hung one especially -- without opening its window first.
    /// </remarks>
    public bool CanKill => Selected is { } selected && Registry.IsRunning(selected.Id);

    /// <summary>Stops the selected instance's running game.</summary>
    /// <remarks>
    /// Acts on THAT instance's own coordinator from the registry, not the status strip's -- the strip
    /// follows the most recent launch, which is not necessarily the row the user has selected to kill.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanKill))]
    public void KillSelected()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        Registry.For(selected.Id).Cancel();
    }

    /// <summary>Starts the selected instance.</summary>
    /// <remarks>
    /// Guarded by the same property the button binds to, rather than trusting the button. A command
    /// reachable by keyboard, by a double-click and by a menu item is a command that will eventually
    /// be invoked in a state the button was not in.
    /// </remarks>
    [RelayCommand]
    public async Task LaunchSelectedAsync()
    {
        if (!CanLaunch || Selected is not { } selected)
        {
            return;
        }

        /*
         * ONE coordinator, this instance's own. The status strip is pointed at it first so the window
         * follows the launch it just started, then the launch is awaited -- exactly once.
         */
        var coordinator = Registry.For(selected.Id);

        if (!ReferenceEquals(Launch, coordinator))
        {
            _launchSubscription?.Invoke();

            Launch = coordinator;

            // Re-subscribed, and the old subscription dropped: a stale one would keep repainting the
            // toolbar from an instance the window is no longer showing.
            PropertyChangedEventHandler handler = (_, _) => RaiseSelectionDependent();

            coordinator.PropertyChanged += handler;
            _launchSubscription = () => coordinator.PropertyChanged -= handler;
        }

        await coordinator.LaunchAsync(selected.Id).ConfigureAwait(true);
    }

    private void RaiseSelectionDependent()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanKill));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(HasIndeterminateProgress));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanCopy));

        // Bound to IsEnabled, so it has to be announced as well as computed.
        OnPropertyChanged(nameof(CanChangeIcon));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanChangeGroup));
        OnPropertyChanged(nameof(CanOpenInstanceFolder));
        OnPropertyChanged(nameof(CanCreateShortcut));
        CreateShortcutCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        ChangeGroupCommand.NotifyCanExecuteChanged();
        OpenInstanceFolderCommand.NotifyCanExecuteChanged();
        ExportSelectedCommand.NotifyCanExecuteChanged();
        ChangeIconCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenEditor));
        OnPropertyChanged(nameof(CanCreateInstance));
        OnPropertyChanged(nameof(CanImportPack));
        OnPropertyChanged(nameof(CanBrowsePacks));
        OnPropertyChanged(nameof(HasDuplicationStatus));
        OnPropertyChanged(nameof(HasIndeterminateCopyProgress));
        OnPropertyChanged(nameof(CanUndoDelete));
        OnPropertyChanged(nameof(HasRemovalStatus));

        LaunchSelectedCommand.NotifyCanExecuteChanged();
        KillSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CopySelectedCommand.NotifyCanExecuteChanged();
        EditSelectedCommand.NotifyCanExecuteChanged();
        NewInstanceCommand.NotifyCanExecuteChanged();
        ImportPackCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>Imports a modpack from a file the user picks. Implemented by the app.</summary>
public interface IPackImporter
{
    /// <summary>Returns the new instance's id, or empty when the user cancelled or it failed.</summary>
    Task<string> ImportAsync(InstanceList list);
}

/// <summary>Shows the launcher's own log. Implemented by the app.</summary>
/// <remarks>
/// THE OTHER HALF OF "SEND US YOUR LAUNCHER LOG". Wave 37 gave the launcher a way to upload a log and
/// no way to look at its own, so the answer to that question still began with explaining where the
/// data folder is.
/// </remarks>
public interface ILauncherLogViewer
{
    Task ShowAsync();
}

/// <summary>Finds a modpack on a platform and installs it. Implemented by the app.</summary>
/// <remarks>
/// SEPARATE FROM IPackImporter, because they are separate features that happen to end in the same
/// place. Importing takes a file somebody already has; this one searches, downloads, and -- the part
/// that matters afterwards -- records which project and version the instance came from.
/// </remarks>
public interface IPackBrowser
{
    /// <summary>Returns the new instance's id, or empty when the user cancelled or it failed.</summary>
    Task<string> BrowseAndInstallAsync(InstanceList list);
}

/// <summary>One of the launcher's own web links.</summary>
public sealed record LauncherLink(string Label, string Url);

/// <summary>Opens a web link. Implemented by the app.</summary>
public interface ILinkOpener
{
    Task OpenAsync(string url);
}

/// <summary>Throws away cached metadata. Implemented by the app.</summary>
public interface IMetadataCache
{
    /// <returns>A sentence describing what was removed.</returns>
    Task<string> ClearAsync();
}

/// <summary>What came of making a shortcut.</summary>
public sealed record ShortcutResult(bool Succeeded, string Message);

/// <summary>Writes a desktop shortcut for an instance. Implemented by the app.</summary>
public interface IShortcutMaker
{
    Task<ShortcutResult> CreateAsync(string instanceId, string instanceName, string iconKey);
}

/// <summary>Opens a folder in the desktop's file manager. Implemented by the app.</summary>
public interface IFolderOpener
{
    Task OpenAsync(string path);

    /// <summary>Opens one of the launcher's own folders, named rather than pathed.</summary>
    Task OpenKnownAsync(string kind);
}

/// <summary>Shows the About window. Implemented by the app.</summary>
public interface IAboutUi
{
    Task OpenAsync();
}

/// <summary>Exports an instance to a file. Implemented by the app.</summary>
public interface IInstanceExporter
{
    Task ExportAsync(InstanceRecord instance);
}

/// <summary>Asks the user to pick an instance icon. Implemented by the app.</summary>
public interface IIconChooser
{
    /// <returns>The chosen key, or empty when cancelled.</returns>
    Task<string> ChooseAsync(string currentKey);
}

/// <summary>Shows the Java install dialog. Implemented by the app.</summary>
public interface IJavaInstallUi
{
    Task OpenAsync();
}

/// <summary>Shows the global settings window. Implemented by the app.</summary>
public interface IGlobalSettingsUi
{
    Task OpenAsync();
}

/// <summary>Shows the accounts window. Implemented by the app.</summary>
public interface IAccountsUi
{
    /// <summary>The default account's player name, or empty when none is chosen.</summary>
    string CurrentAccountName { get; }

    Task OpenAsync();
}

/// <summary>Makes a new instance, asking the user what to make. Implemented by the app.</summary>
public interface IInstanceCreator
{
    /// <summary>Returns the new instance's id, or empty when the user cancelled.</summary>
    Task<string> CreateAsync(InstanceList list);
}

/// <summary>Opens the instance window. Implemented by the app, which owns the windows.</summary>
public interface IInstanceEditor
{
    void Open(InstanceRecord instance);
}

/// <summary>
/// The launcher used when the app has not supplied one.
/// </summary>
/// <remarks>
/// Refuses with a message rather than doing nothing. A Play button that silently does nothing is the
/// worst of the three possible behaviours -- worse than an error, and far worse than a disabled
/// button -- because the user cannot tell it from a hang.
/// </remarks>
internal sealed class UnavailableLauncher : IInstanceLauncher
{
    public Task LaunchAsync(
        string instanceId, IProgressSink progress, CancellationToken cancellationToken, string? server = null)
        => throw new InvalidOperationException("Launching is not wired up in this build.");
}
