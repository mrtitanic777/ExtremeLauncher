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
 * For FlameDownloadPlanner, the pure step between resolving a CurseForge pack's files and fetching
 * them: which become downloads and to what path, which are manual (blocked) downloads, and which are
 * zip resources. The optional-mod rule — installed disabled unless chosen — and the target-folder path
 * are the parts worth pinning.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.ModPlatform;
using Xunit;

namespace ExtremeLauncher.Launch.Tests;

public sealed class FlameDownloadPlannerTests
{
    private static FlameResolvedFile File(
        string fileName, string url = "https://x.invalid/f.jar", bool required = true, string targetFolder = "mods")
        => new()
        {
            Entry = new FlamePackFile { ProjectId = 1, FileId = 2, Required = required, TargetFolder = targetFolder },
            Version = new IndexedVersion { FileName = fileName, DownloadUrl = url },
        };

    private static readonly HashSet<string> None = [];

    [Fact]
    public void ARequiredFileBecomesADownloadUnderItsTargetFolder()
    {
        var plan = FlameDownloadPlanner.Build([File("jei.jar", targetFolder: "mods")], None);

        var download = Assert.Single(plan.Downloads);
        Assert.Equal("https://x.invalid/f.jar", download.Url);
        Assert.Equal("minecraft/mods/jei.jar", download.RelativePath);
        Assert.Empty(plan.Blocked);
    }

    [Fact]
    public void TheTargetFolderIsHonoured()
    {
        var plan = FlameDownloadPlanner.Build([File("cool.zip", targetFolder: "resourcepacks")], None);

        Assert.Equal("minecraft/resourcepacks/cool.zip", Assert.Single(plan.Downloads).RelativePath);
    }

    [Fact]
    public void AFileWithNoUrlIsBlockedAndNotDownloaded()
    {
        var plan = FlameDownloadPlanner.Build([File("secret.jar", url: "")], None);

        Assert.Empty(plan.Downloads);
        Assert.Equal("secret.jar", Assert.Single(plan.Blocked).Version.FileName);
    }

    [Fact]
    public void AnUnselectedOptionalFileIsInstalledDisabled()
    {
        var plan = FlameDownloadPlanner.Build([File("extra.jar", required: false)], None);

        Assert.Equal("minecraft/mods/extra.jar.disabled", Assert.Single(plan.Downloads).RelativePath);
    }

    [Fact]
    public void ASelectedOptionalFileIsInstalledEnabled()
    {
        var selected = new HashSet<string> { "mods/extra.jar" };

        var plan = FlameDownloadPlanner.Build([File("extra.jar", required: false)], selected);

        Assert.Equal("minecraft/mods/extra.jar", Assert.Single(plan.Downloads).RelativePath);
    }

    [Fact]
    public void ARequiredFileIgnoresTheSelectionAndIsNeverDisabled()
    {
        var plan = FlameDownloadPlanner.Build([File("core.jar", required: true)], None);

        Assert.Equal("minecraft/mods/core.jar", Assert.Single(plan.Downloads).RelativePath);
    }

    [Fact]
    public void ZipFilesAreRecordedAsZipResourcesAndStillDownloaded()
    {
        var plan = FlameDownloadPlanner.Build([File("pack.zip", targetFolder: "resourcepacks")], None);

        var zip = Assert.Single(plan.ZipResources);
        Assert.Equal("pack.zip", zip.FileName);
        Assert.Equal("resourcepacks", zip.TargetFolder);
        Assert.Single(plan.Downloads); // a zip is both a resource and a download
    }

    [Fact]
    public void OptionalFilesListsOnlyTheOptionalOnesAsGameRelativePaths()
    {
        var files = new[]
        {
            File("core.jar", required: true),
            File("extra.jar", required: false, targetFolder: "mods"),
            File("pretty.zip", required: false, targetFolder: "resourcepacks"),
        };

        Assert.Equal(["mods/extra.jar", "resourcepacks/pretty.zip"], FlameDownloadPlanner.OptionalFiles(files));
    }
}
