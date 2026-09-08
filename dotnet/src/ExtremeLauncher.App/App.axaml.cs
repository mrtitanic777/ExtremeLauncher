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
 * Ported from the application-startup half of launcher/Application.cpp, less everything that belongs
 * to a Qt event loop.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ExtremeLauncher.Core;
using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            /*
             * THE COMMAND LINE IS A COMPATIBILITY SURFACE. Desktop entries, Steam shortcuts and batch
             * files across this lineage invoke the launcher as `--launch <id> --server <address>`, so
             * the options and their short forms are upstream's. Parsed in ExtremeLauncher.Launch,
             * away from Avalonia, so the rules can be tested.
             */
            var arguments = LauncherArguments.Parse(desktop.Args);

            if (arguments.Error.Length != 0)
            {
                Fail(desktop, arguments.Error + "\n\n" + LauncherArguments.Usage);
                return;
            }

            if (arguments.ShowHelp || arguments.ShowVersion)
            {
                /*
                 * A GUI process on Windows has no console to print to, so this cannot simply write to
                 * stdout the way the CLI does. Until there is a window to show it in, say where the
                 * answer lives rather than appearing to do nothing.
                 */
                Fail(
                    desktop,
                    arguments.ShowVersion
                        ? $"{BuildConfig.Instance.LauncherDisplayName} {BuildConfig.Instance.VersionString}"
                        : LauncherArguments.Usage);

                return;
            }

            /*
             * PORTABLE BY DEFAULT, as the CLI already is: the data directory is the folder holding the
             * executable unless told otherwise. That is what lets an install be moved to another
             * machine or run from a USB stick, which is a real thing people do with this lineage.
             *
             * THE SAME LauncherPaths THE CLI USES, deliberately. The layout is a compatibility surface
             * -- an existing Prism or MultiMC folder has exactly this shape -- and two front-ends
             * deriving it separately is two chances to derive it differently.
             */
            var paths = new LauncherPaths(arguments.DataDirectory);

            paths.EnsureExists();

            // Library storage paths are stored relative and resolve against the process directory, so
            // the classpath handed to the JVM depends on this. See PORTING.md.
            Directory.SetCurrentDirectory(paths.Root);

            if (arguments.LiveCheck)
            {
                WriteLiveCheck(paths);
            }

            /*
             * THE LAUNCHER'S OWN LOG, opened before anything that can fail. A GUI process has no console
             * (see Program.cs), so without this its view of a run -- which JVM was chosen, what the
             * pipeline did -- existed only in a window and died with the process.
             *
             * Kept for the life of the application deliberately: it is closed by the process ending,
             * and disposing it at shutdown would race whatever is still unwinding.
             */
            var log = LauncherLog.Open(paths.Root);

            log.Info($"{BuildConfig.Instance.LauncherDisplayName} {BuildConfig.Instance.VersionString} starting");
            log.Info($"Data directory: {paths.Root}");

            /*
             * THE API CREDENTIALS, supplied at runtime rather than compiled in. This repository ships
             * none -- README.md requires a fork to bring its own or blank them -- so without this
             * Microsoft sign-in could never be offered at all. See BuildConfigOverrides.
             */
            var (config, credentialNotes) = BuildConfigOverrides.Apply(BuildConfig.Instance, paths.Root);

            BuildConfig.Instance = config;

            foreach (var note in credentialNotes)
            {
                log.Info(note);
            }

            /*
             * THE ICON LIST, built from the application's own resources plus the user's icons folder.
             * The built-in keys are DISCOVERED from the embedded assets rather than listed, so adding
             * a PNG to Assets/icons is the whole job of adding a built-in icon.
             */
            var iconList = new IconList(
                FileSystem.PathCombine(paths.Root, "icons"),
                InstanceIcons.BuiltInKeys);

            var settings = GlobalSettings.Create(paths.LauncherConfig);
            /*
             * THE THEME, before any window exists. Applied here rather than in MainWindow so every
             * window this launcher opens is painted the same way from the first frame -- and followed
             * rather than read once, so the settings window changes it live.
             */
            AppTheme.Follow(this, settings);

            var instances = new InstanceList(paths.Instances, settings);

            instances.LoadGroupList();
            instances.LoadList();

            /*
             * ONE CLIENT for the process, identifying itself as the meta and CDN servers expect. Not
             * disposed: it lives exactly as long as the application does, and disposing it on shutdown
             * would race whatever download is still unwinding.
             */
            /*
             * THROUGH THE PROXY, if one is configured. Built here and not later because a live
             * HttpClient cannot have its proxy changed -- which is why the settings window says the
             * setting applies from the next start rather than pretending otherwise.
             */
            var client = new HttpClient(ProxyFactory.CreateHandler(settings));
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildConfig.Instance.UserAgent);

            var service = new LauncherService(paths, client);

            /*
             * --meta beats the MetaURLOverride setting, which beats the build default. The default is
             * the one that cannot work on this fork: BuildConfig points at meta.extremelauncher.net,
             * which does not resolve, so an unconfigured launcher fails every resolve with
             * "Some component metadata load tasks failed" -- which is what it did.
             */
            var metaUrl = arguments.MetaUrl is { Length: > 0 } fromCommandLine
                ? fromCommandLine
                : GlobalSettings.ResolveMetaUrl(settings);

            /*
             * THE ACCOUNTS, loaded before autosave is switched on. Setting it first would let the
             * first change write an empty list over the file we are about to read -- which is the trap
             * upstream's own setListFilePath comment warns about.
             */
            var accounts = new AccountList(paths.Accounts);

            accounts.Load();
            accounts.Autosave = true;

            log.Info(ProxyFactory.Describe(settings));

            log.Info($"Loaded {accounts.Accounts.Count} account(s); default is "
                + $"{accounts.DefaultAccount?.ProfileName ?? "none"}.");

            var launcher = new AppLauncher(
                service,
                metaUrl,
                arguments.ServerToJoin,
                arguments.WorldToJoin,
                log,

                // Read at launch time, not captured, so changing the default takes effect immediately.
                () => accounts.DefaultAccount,
                client,
                BuildConfig.Instance.MsaClientId);

            /*
             * The owner is fetched lazily because the prompts are built before the window they must be
             * modal to. Without a real implementation here the view model falls back to RefusingPrompts
             * and Delete safely does nothing at all -- safe, but not a launcher.
             */
            var prompts = new DialogPrompts(() => desktop.MainWindow);

            /*
             * The dispatcher, for work that reports from worker threads -- copying, as launching
             * already does through AppLauncher. Background priority so a copy's chatter never outranks
             * input or rendering.
             */
            /*
             * ONE registry for the process, shared with every instance window: two would mean the main
             * window and the instance windows each holding their own idea of which games are running.
             */
            var registry = new LaunchRegistry(launcher);

            /*
             * The feed URL is a build constant. A fork that leaves it empty gets a launcher with no
             * news on the toolbar rather than one that reports a failure at every start.
             *
             * Held here rather than only inside the view model because the feed carries more than
             * headlines: its server addresses go to the launcher service, which merges them into an
             * instance's multiplayer list at launch.
             */
            var newsChecker = new NewsChecker(client, BuildConfig.Instance.NewsRssUrl);

            var viewModel = new MainWindowViewModel(
                launcher,
                prompts,
                action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
                // The SAME metadata source the new-instance dialog uses, so Change version and Add
                // loader see the same lists rather than two ideas of what exists.
                new AppInstanceEditor(
                    () => desktop.MainWindow,
                    prompts,
                    registry,
                    new AppVersionListSource(new MetaVersionListSource(paths, client, metaUrl)),

                    /*
                     * ONE INSTALLER PER INSTANCE, because the mod search is filtered by that
                     * instance's Minecraft version and loader. Read from mmc-pack.json each time the
                     * window opens rather than captured, so changing the version and then adding mods
                     * searches for the version you now have.
                     */
                    record => new AppModInstaller(
                        () => desktop.MainWindow,
                        client,
                        prompts,
                        () => DescribeInstance(record),
                        log),

                    // Same per-instance shape, and for the same reason: an update check is filtered
                    // by the instance's Minecraft version and loader.
                    record => new AppModUpdater(
                        () => desktop.MainWindow,
                        client,
                        () => DescribeInstance(record),
                        log),

                    // One for the process: the export dialog is the same whichever instance opened it.
                    new AppModListExporter(() => desktop.MainWindow, log),

                    // Reads the paste-service setting per upload rather than capturing it, so
                    // changing the service in the settings window takes effect immediately.
                    new AppLogUploader(client, settings),

                    // Reachable only for a pack installed through the browser, which is the only
                    // kind that recorded a project id to ask about.
                    new AppPackUpdateChecker(client, log),

                    // The updater is built per instance inside the editor, so it needs these.
                    client,
                    log,

                    // Opens a page's folder in the file manager -- the same opener the main window uses.
                    new AppFolderOpener(paths, log)),
                registry,
                new AppInstanceCreator(
                    () => desktop.MainWindow,
                    new MetaVersionListSource(paths, client, metaUrl),

                    // What a new instance needs to resolve its dependencies. Without it an instance
                    // records only Minecraft and a loader, and cannot start. See ComponentResolution.
                    (paths, client, metaUrl)),
                new AppPackImporter(() => desktop.MainWindow, client, prompts, log, paths, metaUrl),

                /*
                 * The other way in, and the one that leaves a trail: a pack installed through the
                 * browser records its project id, so something can later say a newer version exists.
                 * A pack imported from a file cannot -- see InstanceSettings.CanCheckForPackUpdates.
                 */
                browser: new AppPackBrowser(() => desktop.MainWindow, client, prompts, log, paths, metaUrl),
                legacyFtbBrowser: new AppLegacyFtbBrowser(() => desktop.MainWindow, client, prompts, log, paths),
                atlBrowser: new AppAtlBrowser(() => desktop.MainWindow, client, prompts, log, paths),
                technicBrowser: new AppTechnicBrowser(() => desktop.MainWindow, client, prompts, log, paths),
                /*
                 * The launcher's own log, which until now lived on disk and nowhere in the launcher
                 * -- and is the first thing anybody asking for help gets asked for.
                 */
                launcherLog: new AppLauncherLogViewer(
                    () => desktop.MainWindow,
                    paths,
                    new AppClipboard(() => desktop.MainWindow),
                    new AppLogUploader(client, settings),
                    prompts),
                accounts: new AppAccounts(
                    () => desktop.MainWindow,
                    accounts,
                    client,
                    BuildConfig.Instance.MsaClientId,
                    log),

                // The SAME settings object the launcher is running on, so an edit applies to the
                // next launch without restarting.
                new AppGlobalSettings(
                    () => desktop.MainWindow,
                    settings,
                    log,

                    // The meta url is read at open time, so changing it in this very window and
                    // reopening the Java dialog uses the new one.
                    new AppJavaInstaller(
                        () => desktop.MainWindow,
                        paths,
                        client,
                        () => GlobalSettings.ResolveMetaUrl(settings),
                        log)),
                new AppIconChooser(() => desktop.MainWindow, iconList),
                new AppExporter(() => desktop.MainWindow, log),
                new AppAbout(() => desktop.MainWindow, paths.Root),

                // Resolved from LauncherPaths, which is the single place that decides where anything
                // lives -- the view model asks for a folder by NAME and never learns a path.
                new AppFolderOpener(paths, log),
                new AppShortcutMaker(paths, iconList, log),
                new AppLinks(log),
                new AppMetadataCache(paths, log),
                new AppNews(() => desktop.MainWindow, new AppLinks(log)),
                newsChecker,

                // The copy dialog: a name, the checkboxes that decide what comes across, and how to
                // duplicate -- with Clone offered only where the instances volume supports it.
                copyPrompt: new AppInstanceCopyPrompt(() => desktop.MainWindow, paths.Instances));
            // Before Load, so the first listing already resolves every instance's icon.
            viewModel.Instances.Icons = iconList;
            viewModel.Instances.Load(instances);

            // Named rather than ignored: an option accepted in silence is worse than one rejected.
            foreach (var unsupported in arguments.Unsupported.Distinct(StringComparer.Ordinal))
            {
                viewModel.Launch.Log($"Ignoring {unsupported}", isError: true);
                log.Warning($"Ignoring {unsupported}");
            }

            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            /*
             * THE NEWS FETCH IS NOT AWAITED, and nothing waits for it. It is the least important
             * thing the launcher does and the first thing that touches the network, so a slow or
             * unreachable feed must not hold the window shut. The toolbar says "Loading news..."
             * while it runs and updates itself when it lands.
             */
            _ = LoadNewsAsync(viewModel, log, service, newsChecker);

            /*
             * --launch starts the instance once the window is up, not before. Posted rather than
             * awaited here: OnFrameworkInitializationCompleted runs before the window is shown, and a
             * launch started from inside it would report its first progress into a window nobody can
             * see yet.
             */
            if (arguments.InstanceIdToLaunch.Length != 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    viewModel.Select(arguments.InstanceIdToLaunch);

                    // Selecting an id that does not exist leaves nothing selected, and the command
                    // guards itself -- so a bad --launch shows an empty launcher rather than starting
                    // something arbitrary.
                    viewModel.LaunchSelectedCommand.Execute(null);
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Writes the file that tells a watching script the launcher came up.
    /// </summary>
    /// <remarks>
    /// Upstream writes its application id into "live.check" in the working directory, and removes the
    /// file again if the write comes up short rather than leaving a truncated one behind -- a
    /// half-written check file would be read as a launcher that started when it did not.
    /// </remarks>
    /// <summary>Fetches the news and says in the log what came back.</summary>
    /// <remarks>
    /// Logged because it is otherwise invisible: the toolbar shows a headline or it does not, and
    /// "no news" covers a feed that is down, a feed that is empty, a proxy in the way and a build
    /// with no feed configured. Somebody reading a support log should be able to tell those apart.
    /// </remarks>
    private static async Task LoadNewsAsync(
        MainWindowViewModel viewModel,
        LauncherLog log,
        LauncherService service,
        NewsChecker checker)
    {
        if (!viewModel.News.IsAvailable)
        {
            log.Info("This build has no news feed configured.");

            return;
        }

        await viewModel.News.LoadAsync().ConfigureAwait(true);

        log.Info(viewModel.News.Tooltip.Length != 0
            ? "News: " + viewModel.News.Tooltip
            : $"Loaded {viewModel.News.Entries.Count} news entries.");

        /*
         * The feed also carries server addresses, which this fork merges into every instance's
         * multiplayer list at launch. Logged here as well as at launch, because a launcher that edits
         * a game file on the player's behalf should say so somewhere they will find it.
         */
        service.FeedServers = checker.ServerList;

        if (checker.ServerList.Count != 0)
        {
            log.Info(
                $"The news feed lists {checker.ServerList.Count} server(s), which will be added to "
                + $"each instance's multiplayer list when it launches: "
                + string.Join(", ", checker.ServerList));
        }
    }

    private static void WriteLiveCheck(LauncherPaths paths)
    {
        var file = FileSystem.PathCombine(paths.Root, "live.check");

        try
        {
            File.WriteAllText(file, $"{BuildConfig.Instance.LauncherName}-{BuildConfig.Instance.VersionString}");
        }
        catch (IOException)
        {
            TryRemove(file);
        }
        catch (UnauthorizedAccessException)
        {
            TryRemove(file);
        }
    }

    private static void TryRemove(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // Best effort: the point was to avoid leaving a misleading file, not to guarantee it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Shows a message instead of a launcher, then exits.
    /// </summary>
    /// <remarks>
    /// A GUI process on Windows has no console, so a bad command line cannot be reported the way the
    /// CLI reports one. A window that says what is wrong beats a process that starts and vanishes.
    /// </remarks>
    private static void Fail(IClassicDesktopStyleApplicationLifetime desktop, string message)
    {
        desktop.MainWindow = new Window
        {
            Title = BuildConfig.Instance.LauncherDisplayName,
            Width = 640,
            Height = 400,
            Content = new ScrollViewer
            {
                Padding = new Thickness(16),
                Content = new SelectableTextBlock
                {
                    Text = message,
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                },
            },
        };
    }

    /// <summary>
    /// What Minecraft version and loader an instance runs, for filtering a mod search.
    /// </summary>
    /// <remarks>
    /// Read from the pack file on each call. An instance whose version was changed a moment ago must
    /// be searched for as it is NOW, not as it was when its window opened.
    /// </remarks>
    private static (string Minecraft, string Loader) DescribeInstance(InstanceRecord record)
    {
        var profile = new PackProfile(LauncherService.CurrentRuntimeContext());

        if (!profile.Load(record.Paths.PackProfilePath))
        {
            return (string.Empty, string.Empty);
        }

        var minecraft = string.Empty;
        var loader = string.Empty;

        foreach (var component in profile.Components)
        {
            switch (component.Uid)
            {
                case "net.minecraft":
                    minecraft = component.Version;
                    break;

                // The names the search understands. A loader this launcher does not know leaves the
                // filter empty, which searches everything rather than nothing.
                case "net.fabricmc.fabric-loader":
                    loader = "Fabric";
                    break;

                case "org.quiltmc.quilt-loader":
                    loader = "Quilt";
                    break;

                case "net.minecraftforge":
                    loader = "Forge";
                    break;

                case "net.neoforged":
                    loader = "NeoForge";
                    break;
            }
        }

        return (minecraft, loader);
    }
}
