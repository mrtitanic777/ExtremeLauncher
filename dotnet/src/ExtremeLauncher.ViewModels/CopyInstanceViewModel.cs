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
 * Ported from ui/dialogs/CopyInstanceDialog.cpp -- the name field and the checkboxes that decide which
 * parts of an instance come across.
 *
 * EVERYTHING IS CHECKED BY DEFAULT, which is InstanceCopyPrefs's own default and the right one: a Copy
 * that quietly left your worlds behind would be the opposite of what it is for. The checkboxes are for
 * the person who deliberately wants a slimmer copy -- the same version and mods without a year of
 * screenshots, say.
 *
 * The link-mode options (symlink, hard link, clone) are NOT here yet: they are a performance choice
 * with real per-platform caveats, and a plain copy is the safe default. InstanceCopyPrefs still carries
 * them for when they are exposed.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.ViewModels;

/// <summary>How the copy duplicates files, trading disk and speed against independence.</summary>
public enum InstanceCopyMode
{
    /// <summary>A full, independent copy. Slowest and largest, but the copy shares nothing.</summary>
    Copy,

    /// <summary>Copy-on-write, where the filesystem supports it: instant, and independent once written.</summary>
    Clone,

    /// <summary>Hard links: instant and tiny, but editing a file changes it in BOTH instances.</summary>
    HardLink,

    /// <summary>Symbolic links: instant and tiny, and the links break if the original is moved.</summary>
    SymLink,
}

public sealed partial class CopyInstanceViewModel : ObservableObject
{
    /// <param name="cloneAvailable">
    /// Whether this filesystem supports copy-on-write. Off hides the Clone option rather than offering
    /// one that would fall back to a slow copy or fail -- upstream gates it on the same check.
    /// </param>
    public CopyInstanceViewModel(string sourceName = "", bool cloneAvailable = false)
    {
        _name = sourceName;
        CloneAvailable = cloneAvailable;
    }

    /// <summary>Whether the Clone option should be offered at all.</summary>
    public bool CloneAvailable { get; }

    /// <summary>How the files are duplicated. A full copy by default -- the safe, independent choice.</summary>
    [ObservableProperty]
    private InstanceCopyMode _mode = InstanceCopyMode.Copy;

    /// <summary>The copy's name, prefilled with the original's.</summary>
    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanCopy));

    /// <summary>A copy needs a name; without one there is no folder to make.</summary>
    public bool CanCopy => Name.Trim().Length != 0;

    [ObservableProperty]
    private bool _copySaves = true;

    [ObservableProperty]
    private bool _keepPlaytime = true;

    [ObservableProperty]
    private bool _copyGameOptions = true;

    [ObservableProperty]
    private bool _copyResourcePacks = true;

    [ObservableProperty]
    private bool _copyShaderPacks = true;

    [ObservableProperty]
    private bool _copyServers = true;

    [ObservableProperty]
    private bool _copyMods = true;

    [ObservableProperty]
    private bool _copyScreenshots = true;

    /// <summary>The name and options as the duplication layer wants them.</summary>
    // Radio buttons bind to bools, so each mode gets one that sets Mode when it is picked. Ticking a
    // box for the current mode again is a no-op; ticking another switches. OnModeChanged re-announces
    // all four so the group repaints as one.
    public bool IsModeCopy { get => Mode == InstanceCopyMode.Copy; set { if (value) { Mode = InstanceCopyMode.Copy; } } }

    public bool IsModeClone { get => Mode == InstanceCopyMode.Clone; set { if (value) { Mode = InstanceCopyMode.Clone; } } }

    public bool IsModeHardLink { get => Mode == InstanceCopyMode.HardLink; set { if (value) { Mode = InstanceCopyMode.HardLink; } } }

    public bool IsModeSymLink { get => Mode == InstanceCopyMode.SymLink; set { if (value) { Mode = InstanceCopyMode.SymLink; } } }

    partial void OnModeChanged(InstanceCopyMode value)
    {
        OnPropertyChanged(nameof(IsModeCopy));
        OnPropertyChanged(nameof(IsModeClone));
        OnPropertyChanged(nameof(IsModeHardLink));
        OnPropertyChanged(nameof(IsModeSymLink));
    }

    public InstanceCopyChoice ToChoice() => new(
        Name.Trim(),
        new InstanceCopyPrefs
        {
            CopySaves = CopySaves,
            KeepPlaytime = KeepPlaytime,
            CopyGameOptions = CopyGameOptions,
            CopyResourcePacks = CopyResourcePacks,
            CopyShaderPacks = CopyShaderPacks,
            CopyServers = CopyServers,
            CopyMods = CopyMods,
            CopyScreenshots = CopyScreenshots,

            /*
             * The link mode, mapped to the prefs InstanceCopyTask reads. Clone wins if set; otherwise a
             * hard or symbolic link; otherwise a plain copy. Hard links MUST recurse -- a directory
             * cannot be hard-linked -- while symbolic links point at the top-level folders whole, which
             * is faster and is upstream's default for them.
             */
            UseClone = Mode == InstanceCopyMode.Clone,
            UseHardLinks = Mode == InstanceCopyMode.HardLink,
            UseSymLinks = Mode == InstanceCopyMode.SymLink,
            LinkRecursively = Mode == InstanceCopyMode.HardLink,
        });
}
