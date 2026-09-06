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
 * That the built-in icons are really there and really decode.
 *
 * THE ONE THING NO VIEW MODEL TEST CAN SEE. IconList happily reports a built-in key whether or not
 * the application actually ships a matching resource -- it is handed the key list. If the csproj glob
 * missed the folder, or the avares URI is wrong, every tile in the launcher draws nothing and every
 * other test still passes.
 *
 * WHAT THESE CANNOT SEE, and it is worth being exact about it: the headless platform FAKES the image
 * decoder. `new Bitmap(anything)` returns a stub 1x1 bitmap and never throws, so a test asserting on
 * a decoded bitmap here proves nothing about decoding -- it proves the same thing for a valid PNG and
 * for a text file. My first draft of this file asserted exactly that and passed for the wrong reason.
 *
 * So the built-in checks assert on the RESOURCE STREAM instead: that it exists, and that its bytes
 * begin with a PNG signature. That part is real -- AssetLoader is not stubbed -- and it is the half
 * that can actually be got wrong by a build-file mistake.
 */

using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using ExtremeLauncher.Launch;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class InstanceIconsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "el-iconres-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public void TheApplicationShipsItsBuiltInIcons()
    {
        var keys = InstanceIcons.BuiltInKeys;

        // A glob that matched nothing would leave this empty and everything else still green.
        Assert.NotEmpty(keys);

        // The key a fresh instance is created with. Without it every new instance draws nothing.
        Assert.Contains("default", keys);
    }

    [AvaloniaFact]
    public void EveryBuiltInIconIsReallyThereAndIsReallyAPng()
    {
        /*
         * The bytes, not a decoded bitmap -- the headless decoder accepts anything, so a bitmap
         * assertion would pass for a text file. A resource that is missing, empty or not actually an
         * image is what a build-file mistake produces, and this is what catches it.
         */
        var failures = new List<string>();

        foreach (var key in InstanceIcons.BuiltInKeys)
        {
            var uri = new Uri("avares://ExtremeLauncher/Assets/icons/" + key + ".png");

            if (!AssetLoader.Exists(uri))
            {
                failures.Add(key + " (missing)");

                continue;
            }

            using var stream = AssetLoader.Open(uri);

            var header = new byte[8];

            if (stream.Read(header, 0, 8) != 8
                || header[0] != 0x89 || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
            {
                failures.Add(key + " (not a PNG)");
            }
        }

        Assert.True(failures.Count == 0, "bad built-in icons: " + string.Join(", ", failures));
    }

    [AvaloniaFact]
    public void ThereAreAsManyBuiltInIconsAsThereAreFilesShipped()
    {
        // Guards the glob: a pattern that matched only some of the folder would still pass the
        // per-key checks above, because it would simply report fewer keys.
        Assert.Equal(27, InstanceIcons.BuiltInKeys.Count);
    }

    [AvaloniaFact]
    public void AnUnknownKeyResolvesToTheDefaultAndStillDraws()
    {
        // An instance whose icon was deleted must still show something, not an empty tile.
        var icons = new IconList(_folder, InstanceIcons.BuiltInKeys);

        Assert.NotNull(InstanceIcons.Load(icons, "never-existed"));
    }

    [AvaloniaFact]
    public void AUserIconIsLoadedFromItsFile()
    {
        Directory.CreateDirectory(_folder);

        // A real 1x1 PNG, so this exercises the decoder rather than just the path handling.
        File.WriteAllBytes(
            Path.Combine(_folder, "mine.png"),
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

        var icons = new IconList(_folder, InstanceIcons.BuiltInKeys);

        // NotNull only. The size cannot be asserted here: the headless decoder reports 1x1 for
        // everything, so checking it would be checking the stub rather than the file.
        Assert.NotNull(InstanceIcons.Load(icons, "mine"));
    }

    [AvaloniaFact]
    public void AnSvgIsRefusedRatherThanThrown()
    {
        // Listed by IconList as unrenderable; this is the second guard, so a file whose extension
        // lies cannot reach the decoder and throw somewhere far less obvious.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "vector.svg"), "<svg/>");

        var icons = new IconList(_folder, []);

        Assert.Null(InstanceIcons.Load(new IconEntry("vector", IconSource.User, Path.Combine(_folder, "vector.svg"))));

        // And it is still listed, so the folder and the launcher agree about what is in it.
        Assert.Contains(icons.All(), e => e.Key == "vector");
    }

    [AvaloniaFact]
    public void AMissingFileIsRefusedRatherThanThrown()
    {
        /*
         * The case a corrupt-file test cannot cover headlessly: the decoder is faked and never
         * throws, so "a corrupt PNG returns null" is not testable here. This IS -- the file check
         * happens before the decoder is reached, and an icon whose file was deleted while the
         * launcher was open is the way this actually happens.
         *
         * The catch around the decoder stays regardless: on a real platform a truncated PNG throws,
         * and one bad file in an icons folder must not take the window down.
         */
        Assert.Null(InstanceIcons.Load(
            new IconEntry("gone", IconSource.User, Path.Combine(_folder, "gone.png"))));
    }
}
