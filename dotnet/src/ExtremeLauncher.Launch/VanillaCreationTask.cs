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
 * Ported from minecraft/VanillaInstanceCreationTask.cpp.
 *
 * THE LAUNCHER COULD NOT MAKE AN INSTANCE UNTIL THIS EXISTED. Everything else in the port assumed one
 * was already on disk -- put there by Prism, by MultiMC, or by hand. A launcher that can only run
 * instances somebody else created is not a launcher.
 *
 * It is far smaller than the gap suggests, because everything underneath was already ported: write an
 * instance.cfg, build a PackProfile pinning net.minecraft to the chosen version, and let
 * InstanceStagingTask commit it. Upstream's whole createInstance() is eighteen lines for the same
 * reason.
 *
 * BUILT IN STAGING AND COMMITTED, like a copy. A creation that dies half way otherwise leaves a
 * directory that looks like an instance and is not -- and the launcher would list it, and it would
 * fail to launch, and nobody would know why.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using ExtremeLauncher.Settings;
using ExtremeLauncher.Tasks;

namespace ExtremeLauncher.Launch;

public sealed class VanillaCreationTask : LauncherTask, IInstanceTask
{
    private readonly string _name;

    private readonly string _minecraftVersion;

    private readonly string _loaderUid;

    private readonly string _loaderVersion;

    private readonly string _iconKey;

    private readonly RuntimeContext _runtimeContext;

    /// <param name="loaderUid">A mod loader's uid, or empty for vanilla.</param>
    /// <param name="loaderVersion">The loader's version. Ignored when <paramref name="loaderUid"/> is empty.</param>
    private readonly LauncherPaths? _paths;

    private readonly HttpClient? _client;

    private readonly string _metaUrl;

    /// <param name="paths">
    /// Supplying these, a client and a meta url lets the new instance RESOLVE its dependencies -- see
    /// ComponentResolution. Without them the instance is written with only the components it was told
    /// about, which for any modern Minecraft version is not enough to start the game.
    /// </param>
    public VanillaCreationTask(
        string name,
        string minecraftVersion,
        RuntimeContext runtimeContext,
        string loaderUid = "",
        string loaderVersion = "",
        string group = "",
        string iconKey = "default",
        LauncherPaths? paths = null,
        HttpClient? client = null,
        string metaUrl = "")
        : base($"Creating instance {name}")
    {
        _paths = paths;
        _client = client;
        _metaUrl = metaUrl;

        _name = name;
        _minecraftVersion = minecraftVersion;
        _runtimeContext = runtimeContext;
        _loaderUid = loaderUid;
        _loaderVersion = loaderVersion;
        _iconKey = iconKey;

        Group = group;
    }

    /// <summary>Where to build. Assigned by <see cref="InstanceStagingTask"/> before this runs.</summary>
    public string StagingPath { get; set; } = string.Empty;

    /// <inheritdoc/>
    /// <remarks>Explicit, because LauncherTask.Name is the task's display name. See InstanceCopyTask.</remarks>
    string IInstanceTask.Name => _name;

    public string Group { get; }

    /// <summary>Always false: creating an instance replaces nothing.</summary>
    public bool ShouldOverride => false;

    public string OriginalInstanceId => string.Empty;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        SetStatus($"Creating instance from version {_minecraftVersion}");

        if (_name.Length == 0)
        {
            throw new LauncherException("An instance needs a name.");
        }

        if (_minecraftVersion.Length == 0)
        {
            throw new LauncherException("An instance needs a Minecraft version.");
        }

        if (StagingPath.Length == 0)
        {
            throw new LauncherException("No staging path was set.");
        }

        FileSystem.EnsureFolderPathExists(StagingPath);

        /*
         * instance.cfg first. InstanceType is what makes the launcher recognise the directory at all --
         * without it the instance lists as unsupported, which is a confusing way for creation to fail.
         */
        var settings = new IniSettingsObject(FileSystem.PathCombine(StagingPath, "instance.cfg"));

        settings.RegisterSetting("name", string.Empty);
        settings.RegisterSetting("iconKey", "default");
        settings.RegisterSetting("InstanceType", string.Empty);

        settings.Set("InstanceType", "OneSix");
        settings.Set("name", _name);
        settings.Set("iconKey", _iconKey);

        var profile = new PackProfile(_runtimeContext);

        /*
         * IMPORTANT, which is what makes Minecraft unremovable on the version page and is the whole
         * difference between an instance and a folder of components. Upstream passes the same flag.
         */
        profile.SetComponentVersion("net.minecraft", _minecraftVersion, important: true);

        if (_loaderUid.Length != 0)
        {
            // Not important: a loader is exactly the thing a user may want to remove later.
            profile.SetComponentVersion(_loaderUid, _loaderVersion);
        }

        var packPath = FileSystem.PathCombine(StagingPath, "mmc-pack.json");

        if (!profile.Save(packPath))
        {
            throw new LauncherException("Could not write the instance's component list.");
        }

        /*
         * RESOLVED HERE, and the comment that used to sit in this spot was wrong in a way that cost a
         * working launcher. It said:
         *
         *     "NOT RESOLVED HERE, deliberately. Upstream does not either: creation writes what was
         *      asked for, and the first launch resolves it against the metadata server."
         *
         * THE FIRST LAUNCH DOES NOT RESOLVE IT. ComponentUpdateTask runs in Launch mode there, which
         * deliberately only REPORTS unmet requirements rather than acting on them -- so an instance
         * created here recorded `net.minecraft 1.20.1` and nothing else, the launch dutifully warned
         * "Minecraft is missing requirement org.lwjgl3 3.3.1", started a JVM with no windowing
         * library on the classpath, and the game died instantly.
         *
         * Upstream resolves as part of creating the instance, which is why it never hits this.
         *
         * BEST EFFORT, and not fatal. Without a client this does nothing and the instance is still
         * written -- creating one offline stays possible, which is what the old comment was actually
         * protecting. It just cannot then be launched until something resolves it.
         */
        if (_paths is not null && _client is not null && _metaUrl.Length != 0)
        {
            SetStatus("Working out what else this version needs");

            await ComponentResolution.ApplyAsync(
                profile,
                packPath,
                FileSystem.PathCombine(StagingPath, "patches"),
                _paths,
                _client,
                _metaUrl,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
