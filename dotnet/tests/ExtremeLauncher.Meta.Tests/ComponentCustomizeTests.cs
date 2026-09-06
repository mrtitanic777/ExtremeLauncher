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
 * Customising a component into a local patch, and reverting it.
 *
 * THE PROPERTY THAT MATTERS is that a customised component stops listening to the meta server: the
 * copy on disk becomes the source of truth, so somebody can edit it and a refresh will not clobber
 * the edit. Most of these tests are about that link being cut, and the last one proves the copy is
 * really what a fresh resolution reads back.
 */

using ExtremeLauncher.Core;
using ExtremeLauncher.Meta;
using ExtremeLauncher.Minecraft;
using Xunit;

namespace ExtremeLauncher.Meta.Tests;

public sealed class ComponentCustomizeTests : IDisposable
{
    private readonly string _patches = Path.Combine(Path.GetTempPath(), "el-cust-" + Guid.NewGuid().ToString("N"));

    public ComponentCustomizeTests() => Directory.CreateDirectory(_patches);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_patches, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Component WithMeta(string uid, string version = "1.20.1", string name = "Minecraft")
    {
        var file = new VersionFile { Uid = uid, Version = version, Name = name };

        return new Component(uid) { MetaVersion = new MetaVersion(uid, version) { Data = file } };
    }

    private string PatchFor(string uid) => PackProfile.PatchFilePathForUid(_patches, uid);

    // ================================================================== customise

    [Fact]
    public void CustomisingWritesAPatchFileAndCutsTheMetaLink()
    {
        var component = WithMeta("net.minecraft");

        Assert.True(component.Customize(_patches));

        // The file exists, LocalFile now backs it, and the metadata link is gone -- so a refresh can
        // no longer overwrite it.
        Assert.True(File.Exists(PatchFor("net.minecraft")));
        Assert.True(component.IsCustom);
        Assert.Null(component.MetaVersion);
        Assert.False(component.IsCustomizable);
    }

    [Fact]
    public void TheWrittenPatchIsTheComponentsOwnVersionFile()
    {
        /*
         * Not an empty stub: customising copies the resolved version out verbatim, so the user starts
         * editing from what the pack actually ships rather than from nothing.
         */
        var component = WithMeta("net.minecraft", "1.20.1");

        Assert.True(component.Customize(_patches));

        var readBack = OneSixVersionFormat.VersionFileFromJson(
            Json.RequireObject(Json.RequireDocumentFromFile(PatchFor("net.minecraft"))),
            "net.minecraft.json");

        Assert.Equal("net.minecraft", readBack.Uid);
        Assert.Equal("1.20.1", readBack.Version);
    }

    [Fact]
    public void CustomisingSomethingAlreadyCustomDoesNothing()
    {
        // A local-only component has nothing to copy from and is already the source of truth.
        var component = new Component("com.example.homebrew",
            new VersionFile { Uid = "com.example.homebrew", Version = "1", Name = "Homebrew" });

        Assert.True(component.IsCustom);
        Assert.False(component.Customize(_patches));
        Assert.False(File.Exists(PatchFor("com.example.homebrew")));
    }

    [Fact]
    public void CustomisingSomethingWithNoMetadataFails()
    {
        // No meta version behind it, so there is nothing to write out.
        var component = new Component("net.minecraft");

        Assert.False(component.Customize(_patches));
    }

    // ================================================================== revert

    [Fact]
    public void RevertingDeletesThePatchAndUnloadsTheComponent()
    {
        var component = WithMeta("net.minecraft");

        Assert.True(component.Customize(_patches));
        Assert.True(File.Exists(PatchFor("net.minecraft")));

        Assert.True(component.Revert(_patches));

        Assert.False(File.Exists(PatchFor("net.minecraft")));
        Assert.False(component.IsCustom);

        // Unloaded on purpose: the next resolution must re-fetch rather than trust the file just gone.
        Assert.False(component.IsLoaded);
    }

    [Fact]
    public void RevertingSomethingThatIsNotCustomIsANoOp()
    {
        // Upstream returns true here: there is nothing to undo, which is not a failure.
        var component = WithMeta("net.minecraft");

        Assert.True(component.Revert(_patches));
        Assert.False(component.IsCustom);
    }

    [Fact]
    public void RevertingWithNoPatchFileOnDiskStillClearsTheOverride()
    {
        // A custom component whose file was removed out from under us -- reverting should still leave
        // it non-custom rather than refusing because the delete found nothing.
        var component = new Component("net.minecraft",
            new VersionFile { Uid = "net.minecraft", Version = "1.20.1", Name = "Minecraft" });

        Assert.True(component.IsCustom);
        Assert.True(component.Revert(_patches));
        Assert.False(component.IsCustom);
    }

    // ================================================================== the profile wrappers

    [Fact]
    public void TheProfileWontCustomiseWhatCannotBeCustomised()
    {
        var profile = new PackProfile(Context());

        // A local-only component: IsCustomizable is false, so the profile refuses.
        profile.AppendComponent(new Component("com.example.homebrew",
            new VersionFile { Uid = "com.example.homebrew", Version = "1", Name = "Homebrew" }));

        Assert.False(profile.Customize(0, _patches));
    }

    [Fact]
    public void TheProfileCustomisesAndInvalidatesTheFlattenedView()
    {
        var profile = new PackProfile(Context());
        profile.AppendComponent(WithMeta("net.minecraft"));

        // Force the flattened profile to be cached, so we can prove customising drops it.
        _ = profile.GetProfile();

        Assert.True(profile.Customize(0, _patches));
        Assert.True(profile.GetComponent(0)!.IsCustom);
    }

    [Fact]
    public void TheProfileWontRevertAComponentTheIndexHasNeverHeardOf()
    {
        /*
         * The guard that stops a revert stranding a component. Reverting throws the local file away
         * and expects the meta server to have a copy -- so if the index does not list the uid, the
         * component would become unresolvable, and the profile refuses.
         */
        var profile = new PackProfile(Context());

        profile.AppendComponent(new Component("com.example.homebrew",
            new VersionFile { Uid = "com.example.homebrew", Version = "1", Name = "Homebrew" }));

        var emptyIndex = new Index([]);

        Assert.False(profile.RevertToBase(0, _patches, emptyIndex));

        // A file it did write for a known uid can be reverted.
        profile.AppendComponent(WithMeta("net.minecraft"));
        Assert.True(profile.Customize(1, _patches));

        var index = new Index([new VersionList("net.minecraft")]);
        Assert.True(profile.RevertToBase(1, _patches, index));
    }

    private static RuntimeContext Context()
        => new() { System = "linux", JavaArchitecture = "64", JavaRealArchitecture = "amd64" };
}
