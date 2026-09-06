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
 * THE LIST IS WHAT STOPS A TYPO BECOMING AN INSTANCE. VanillaCreationTask does not validate the
 * version it is given -- upstream does not either, because creation must work offline -- so the only
 * thing standing between a mistyped version and an instance that fails at launch is that the user
 * picked from a list. Several tests here are really about that.
 *
 * Created instances are read back through InstanceList, not through the object that made them.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.ViewModels.Tests;

public sealed class NewInstanceViewModelTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-newvm-" + Guid.NewGuid().ToString("N"));

    private readonly string _instances;

    public NewInstanceViewModelTests()
    {
        _instances = Path.Combine(_temp, "instances");
        Directory.CreateDirectory(_instances);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private InstanceList NewList()
    {
        var list = new InstanceList(_instances, GlobalSettings.Create(Path.Combine(_temp, "launcher.cfg")));

        list.LoadGroupList();
        list.LoadList();

        return list;
    }

    private static MetaVersion Version(string version, string type, int year, bool recommended = false)
        => new("net.minecraft", version)
        {
            Type = type,
            IsRecommended = recommended,
            RawTime = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        };

    private sealed class FakeVersions(params MetaVersion[] versions) : IVersionListSource
    {
        public FakeVersions(Exception failure)
            : this([])
            => Failure = failure;

        private Exception? Failure { get; }

        /// <summary>Extra components, keyed by uid -- the loaders.</summary>
        public Dictionary<string, MetaVersion[]> ByUid { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<MetaVersion>> LoadAsync(string uid, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException<IReadOnlyList<MetaVersion>>(Failure);
            }

            return Task.FromResult<IReadOnlyList<MetaVersion>>(
                ByUid.TryGetValue(uid, out var forUid) ? forUid : versions);
        }
    }

    /// <summary>A loader version that declares which Minecraft version it is for, as Forge does.</summary>
    private static MetaVersion LoaderFor(string version, string minecraft, int year)
    {
        var meta = new MetaVersion("net.minecraftforge", version)
        {
            Type = "release",
            RawTime = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        };

        meta.SetRequires([new Require("net.minecraft", minecraft)], []);

        return meta;
    }

    /// <summary>A loader version that declares nothing, as Fabric's do.</summary>
    private static MetaVersion LooseLoader(string uid, string version, int year, bool recommended = false)
        => new(uid, version)
        {
            Type = "release",
            IsRecommended = recommended,
            RawTime = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        };

    private static RuntimeContext Context()
        => new() { System = "windows", JavaArchitecture = "64", JavaRealArchitecture = "x86_64" };

    private static NewInstanceViewModel Model(params MetaVersion[] versions)
        => new(
            new FakeVersions(versions),
            new RuntimeContext { System = "windows", JavaArchitecture = "64", JavaRealArchitecture = "x86_64" });

    // ================================================================== the version list

    /*
     * SNAPSHOTS AND OLD VERSIONS ARE HIDDEN BY DEFAULT, as upstream hides them: the real list runs to
     * well over a thousand entries, nearly all of them 2011 alphas and weekly snapshots.
     */
    [Fact]
    public async Task OnlyReleasesAreShownToBeginWith()
    {
        var model = Model(
            Version("1.20.1", "release", 2023),
            Version("23w31a", "snapshot", 2023),
            Version("b1.7.3", "old_beta", 2011));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        Assert.Equal(["1.20.1"], model.Versions.Select(v => v.Version));
    }

    [Fact]
    public async Task ShowingAllRevealsTheRest()
    {
        var model = Model(
            Version("1.20.1", "release", 2023),
            Version("23w31a", "snapshot", 2023),
            Version("b1.7.3", "old_beta", 2011));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.ShowAllVersions = true;

        Assert.Equal(3, model.Versions.Count);
    }

    [Fact]
    public async Task NewestVersionsComeFirst()
    {
        var model = Model(
            Version("1.16.5", "release", 2021),
            Version("1.20.1", "release", 2023),
            Version("1.18.2", "release", 2022));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        Assert.Equal(["1.20.1", "1.18.2", "1.16.5"], model.Versions.Select(v => v.Version));
    }

    /// <summary>The recommended version is picked to begin with — the latest release, usually.</summary>
    [Fact]
    public async Task TheRecommendedVersionIsSelectedByDefault()
    {
        var model = Model(
            Version("1.16.5", "release", 2021),
            Version("1.20.1", "release", 2023, recommended: true));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        Assert.Equal("1.20.1", model.Selected?.Version);
    }

    /// <summary>The selection survives the filter changing, so ticking the box does not lose it.</summary>
    [Fact]
    public async Task TheSelectionSurvivesShowingAllVersions()
    {
        var model = Model(
            Version("1.20.1", "release", 2023, recommended: true),
            Version("23w31a", "snapshot", 2023));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.ShowAllVersions = true;

        Assert.Equal("1.20.1", model.Selected?.Version);
    }

    /*
     * A NAMED FAILURE, not an empty list. "No versions" reads as "Minecraft has no versions", which is
     * never true -- what happened is the metadata server could not be reached.
     */
    [Fact]
    public async Task AFailedFetchSaysWhatWentWrong()
    {
        var model = new NewInstanceViewModel(new FakeVersions(new HttpRequestException("no such host")));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        Assert.Contains("no such host", model.Error, StringComparison.Ordinal);
        Assert.True(model.IsEmpty);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task ABuildWithNoSourceSaysSo()
    {
        var model = new NewInstanceViewModel();

        await model.LoadVersionsAsync().ConfigureAwait(true);

        Assert.NotEqual(string.Empty, model.Error);
    }

    // ================================================================== what may be created

    /*
     * BOTH HALVES ARE REQUIRED. An unnamed instance is a directory called nothing, and an unchosen
     * version is exactly the typo the list exists to prevent.
     */
    [Fact]
    public async Task NothingCanBeCreatedWithoutBothANameAndAVersion()
    {
        var model = Model(Version("1.20.1", "release", 2023));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Select(null);

        Assert.False(model.CanCreate);

        model.Name = "My Pack";

        Assert.False(model.CanCreate);

        model.Select("1.20.1");

        Assert.True(model.CanCreate);

        model.Name = "   ";

        Assert.False(model.CanCreate);
    }

    // ================================================================== creating

    /*
     * Read back through InstanceList -- the same code that reads an instance Prism made. Anything else
     * tests the writer against itself.
     */
    [Fact]
    public async Task CreatingMakesAnInstanceTheLauncherCanRead()
    {
        var model = Model(Version("1.20.1", "release", 2023, recommended: true));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Name = "My New Pack";

        var list = NewList();
        var id = await model.CreateAsync(list).ConfigureAwait(true);

        Assert.NotEqual(string.Empty, id);

        var reread = NewList();
        var instance = reread.GetInstanceById(id);

        Assert.NotNull(instance);
        Assert.Equal("My New Pack", instance!.Name);
        Assert.True(instance.IsSupported);

        var profile = new PackProfile(
            new RuntimeContext { System = "windows", JavaArchitecture = "64", JavaRealArchitecture = "x86_64" });

        Assert.True(profile.Load(instance.Paths.PackProfilePath));
        Assert.Equal("1.20.1", profile.GetComponentVersion("net.minecraft"));
    }

    [Fact]
    public async Task TheNameIsTrimmed()
    {
        var model = Model(Version("1.20.1", "release", 2023, recommended: true));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Name = "  Padded  ";

        var id = await model.CreateAsync(NewList()).ConfigureAwait(true);

        Assert.Equal("Padded", NewList().GetInstanceById(id)?.Name);
    }

    /// <summary>The group is written and survives a reload, which is where it was lost before.</summary>
    [Fact]
    public async Task TheInstanceLandsInItsGroupOnDisk()
    {
        var model = Model(Version("1.20.1", "release", 2023, recommended: true));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Name = "Grouped";
        model.Group = "Modded";

        var id = await model.CreateAsync(NewList()).ConfigureAwait(true);

        Assert.Equal("Modded", NewList().GetInstanceGroup(id));
    }

    [Fact]
    public async Task CreatingWithoutASelectionMakesNothing()
    {
        var model = Model(Version("1.20.1", "release", 2023));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Select(null);
        model.Name = "Nameless";

        Assert.Equal(string.Empty, await model.CreateAsync(NewList()).ConfigureAwait(true));
        Assert.Empty(Directory.GetDirectories(_instances));
    }

    // ================================================================== mod loaders

    private static LoaderOption Loader(string name)
        => LoaderOption.All.Single(l => l.Name == name);

    /*
     * FORGE PUBLISHES A BUILD PER MINECRAFT VERSION and says so in `requires`, so its list is filtered
     * exactly. Offering a 1.20.1 Forge for a 1.16.5 instance would make an instance that cannot resolve.
     */
    [Fact]
    public async Task ForgeVersionsAreFilteredToTheChosenMinecraftVersion()
    {
        var fake = new FakeVersions(
            Version("1.20.1", "release", 2023, recommended: true),
            Version("1.16.5", "release", 2021));

        fake.ByUid["net.minecraftforge"] =
        [
            LoaderFor("47.2.0", "1.20.1", 2023),
            LoaderFor("36.2.39", "1.16.5", 2021),
        ];

        var model = new NewInstanceViewModel(fake, Context());

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Loader = Loader("Forge");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        Assert.Equal(["47.2.0"], model.LoaderVersions.Select(v => v.Version));

        // Changing Minecraft changes what Forge offers.
        model.Select("1.16.5");

        Assert.Equal(["36.2.39"], model.LoaderVersions.Select(v => v.Version));
    }

    /*
     * FABRIC DECLARES NOTHING, so upstream shows every loader version for 1.14 and later -- its own
     * comment is "FIXME: dirty hack because the launcher is unaware of Fabric's dependencies".
     * Filtering by `requires` would show an empty list for every Minecraft version, which is a worse
     * answer than a slightly too generous one.
     */
    [Fact]
    public async Task FabricShowsEveryVersionForModernMinecraft()
    {
        var fake = new FakeVersions(Version("1.20.1", "release", 2023, recommended: true));

        fake.ByUid["net.fabricmc.fabric-loader"] =
        [
            LooseLoader("net.fabricmc.fabric-loader", "0.15.7", 2024, recommended: true),
            LooseLoader("net.fabricmc.fabric-loader", "0.14.21", 2023),
        ];

        var model = new NewInstanceViewModel(fake, Context());

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Loader = Loader("Fabric");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        Assert.Equal(["0.15.7", "0.14.21"], model.LoaderVersions.Select(v => v.Version));

        // The recommended one is chosen, so Create is not left disabled for no visible reason.
        Assert.Equal("0.15.7", model.SelectedLoaderVersion?.Version);
    }

    /// <summary>Fabric began with 1.14; before that it offers nothing, and says why.</summary>
    [Fact]
    public async Task FabricOffersNothingBeforeMinecraft114()
    {
        var fake = new FakeVersions(
            Version("1.20.1", "release", 2023, recommended: true),
            Version("1.12.2", "release", 2017));

        fake.ByUid["net.fabricmc.fabric-loader"] =
        [
            LooseLoader("net.fabricmc.fabric-loader", "0.15.7", 2024),
        ];

        var model = new NewInstanceViewModel(fake, Context());

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Loader = Loader("Fabric");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        model.Select("1.12.2");

        Assert.Empty(model.LoaderVersions);
        Assert.Contains("does not support", model.LoaderNote, StringComparison.Ordinal);
        Assert.Contains("1.12.2", model.LoaderNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingNoLoaderSaysSoRatherThanShowingAnEmptyList()
    {
        var model = Model(Version("1.20.1", "release", 2023, recommended: true));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        Assert.True(model.Loader.IsNone);
        Assert.Contains("No mod loader", model.LoaderNote, StringComparison.Ordinal);
        Assert.Empty(model.LoaderVersions);
    }

    /*
     * A LOADER CHOSEN BUT NO VERSION OF IT PICKED would write a component with an empty version, which
     * resolves to nothing at first launch. Create stays disabled until both halves are answered.
     */
    [Fact]
    public async Task ALoaderWithNoVersionBlocksCreation()
    {
        var fake = new FakeVersions(
            Version("1.20.1", "release", 2023, recommended: true),
            Version("1.12.2", "release", 2017));

        fake.ByUid["net.fabricmc.fabric-loader"] = [LooseLoader("net.fabricmc.fabric-loader", "0.15.7", 2024)];

        var model = new NewInstanceViewModel(fake, Context());

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Name = "Modded";
        model.Loader = Loader("Fabric");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        Assert.True(model.CanCreate);

        // 1.12.2 has no Fabric, so there is nothing to pick and nothing to create.
        model.Select("1.12.2");

        Assert.Null(model.SelectedLoaderVersion);
        Assert.False(model.CanCreate);
    }

    /*
     * The whole point, read back off disk: both components, with Minecraft important and the loader
     * not -- because a loader is exactly the thing somebody may want to take back off.
     */
    [Fact]
    public async Task CreatingWithALoaderWritesBothComponents()
    {
        var fake = new FakeVersions(Version("1.20.1", "release", 2023, recommended: true));

        fake.ByUid["net.fabricmc.fabric-loader"] =
            [LooseLoader("net.fabricmc.fabric-loader", "0.15.7", 2024, recommended: true)];

        var model = new NewInstanceViewModel(fake, Context());

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Name = "Modded Pack";
        model.Loader = Loader("Fabric");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        var id = await model.CreateAsync(NewList()).ConfigureAwait(true);

        Assert.NotEqual(string.Empty, id);

        var profile = new PackProfile(Context());

        Assert.True(profile.Load(NewList().GetInstanceById(id)!.Paths.PackProfilePath));

        Assert.Equal("1.20.1", profile.GetComponentVersion("net.minecraft"));
        Assert.Equal("0.15.7", profile.GetComponentVersion("net.fabricmc.fabric-loader"));

        Assert.True(profile.GetComponent("net.minecraft")!.IsImportant);
        Assert.False(profile.GetComponent("net.fabricmc.fabric-loader")!.IsImportant);
    }

    [Fact]
    public async Task ChoosingNoLoaderStillMakesAVanillaInstance()
    {
        var model = Model(Version("1.20.1", "release", 2023, recommended: true));

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Name = "Vanilla";

        var id = await model.CreateAsync(NewList()).ConfigureAwait(true);

        var profile = new PackProfile(Context());
        profile.Load(NewList().GetInstanceById(id)!.Paths.PackProfilePath);

        Assert.Equal(["net.minecraft"], profile.Components.Select(c => c.Uid));
    }

    /*
     * TWO CALLERS GET ONE FETCH. Setting Loader starts a fetch so the UI does not have to, and anything
     * driving this without a combo box naturally awaits the same method -- so both ran, and both
     * cleared and repopulated LoaderVersions while the other was walking it.
     *
     * That threw a NullReferenceException out of FirstOrDefault, and ONLY against the real metadata
     * server, where the fetch is slow enough for the two to overlap. Every fixture-backed test passed.
     * Pinned here on the shared task rather than on timing, which would be the same flaky-test mistake
     * again.
     */
    [Fact]
    public async Task TwoCallersShareOneLoaderFetch()
    {
        var fake = new FakeVersions(Version("1.20.1", "release", 2023, recommended: true));

        fake.ByUid["net.fabricmc.fabric-loader"] =
            [LooseLoader("net.fabricmc.fabric-loader", "0.15.7", 2024, recommended: true)];

        var model = new NewInstanceViewModel(fake, Context());

        await model.LoadVersionsAsync().ConfigureAwait(true);

        // The setter starts one...
        model.Loader = Loader("Fabric");

        // ...and this must join it rather than starting a second.
        var first = model.LoadLoaderVersionsAsync();
        var second = model.LoadLoaderVersionsAsync();

        Assert.Same(first, second);

        await first.ConfigureAwait(true);

        Assert.Equal(["0.15.7"], model.LoaderVersions.Select(v => v.Version));
    }

    /// <summary>Changing loader abandons the previous fetch's claim, so the new one really runs.</summary>
    [Fact]
    public async Task ChangingLoaderStartsAFreshFetch()
    {
        var fake = new FakeVersions(Version("1.20.1", "release", 2023, recommended: true));

        fake.ByUid["net.fabricmc.fabric-loader"] = [LooseLoader("net.fabricmc.fabric-loader", "0.15.7", 2024)];
        fake.ByUid["org.quiltmc.quilt-loader"] = [LooseLoader("org.quiltmc.quilt-loader", "0.24.0", 2024)];

        var model = new NewInstanceViewModel(fake, Context());

        await model.LoadVersionsAsync().ConfigureAwait(true);

        model.Loader = Loader("Fabric");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        Assert.Equal(["0.15.7"], model.LoaderVersions.Select(v => v.Version));

        model.Loader = Loader("Quilt");
        await model.LoadLoaderVersionsAsync().ConfigureAwait(true);

        Assert.Equal(["0.24.0"], model.LoaderVersions.Select(v => v.Version));
    }
}
