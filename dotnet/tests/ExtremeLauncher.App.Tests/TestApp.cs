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
 * The Avalonia application these tests run inside, with no display attached.
 *
 * WHY THIS PROJECT EXISTS. Until now nothing in this port's UI had ever been executed. The build
 * type-checks compiled bindings and stops there: it cannot tell whether a DataTemplate resolves,
 * whether a button reaches the command it names, whether a converter throws, or whether a window even
 * constructs. Every one of those has been "unverified" in PORTING.md for four waves.
 *
 * THE REAL App CLASS IS NOT USED. App.OnFrameworkInitializationCompleted builds a data directory,
 * an HttpClient and an instance list, which is startup rather than UI. These tests want the styles and
 * the templates, so this stands in and loads the same theme.
 */

using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(ExtremeLauncher.App.Tests.TestApp))]

/*
 * SERIAL, NECESSARILY. Avalonia headless has ONE dispatcher and one application instance; xUnit runs
 * test classes in parallel by default, so two of them driving the same UI thread interleave.
 *
 * The symptom was a different toolbar button failing on each run -- "Delete", then "Copy" -- from a
 * test that passed on its own every time. Nondeterministic failures in a UI test suite are worse than
 * no UI test suite: they teach you to re-run rather than to look.
 */
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace ExtremeLauncher.App.Tests;

public sealed class TestApp : Application
{
    public TestApp() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                /*
                 * The stub font manager comes with headless drawing. Without it, anything that measures
                 * text throws "Unable to locate 'Avalonia.Platform.IFontManagerImpl'" -- which is most
                 * of a UI, and which shows up as a different test failing on each run.
                 */
                UseHeadlessDrawing = true,
            });
}
