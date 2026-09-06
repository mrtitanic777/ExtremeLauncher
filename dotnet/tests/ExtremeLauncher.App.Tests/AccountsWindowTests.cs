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
 * The accounts window, driven through its real visual tree.
 *
 * The view model tests already pin the behaviour; these exist because a binding typo is invisible to
 * those and fatal here -- a button wired to a command that does not exist is simply dead, and every
 * unit test still passes.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ExtremeLauncher.Minecraft.Auth;
using ExtremeLauncher.ViewModels;
using Xunit;

namespace ExtremeLauncher.App.Tests;

public sealed class AccountsWindowTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "el-accui-" + Guid.NewGuid().ToString("N"));

    private readonly List<Window> _windows = [];

    public AccountsWindowTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        // See MainWindowTests: a window left showing breaks a later test, not this one.
        foreach (var window in _windows)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ListPath => Path.Combine(_temp, "accounts.json");

    private AccountsWindow Show(AccountList accounts, AccountsViewModel model)
    {
        var window = new AccountsWindow(model, accounts);

        _windows.Add(window);

        window.Show();

        Settle(window);

        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Button Button(Visual root, string content)
        => root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content);

    [AvaloniaFact]
    public void TheWindowOpensWithItsButtonsInTheRightState()
    {
        var accounts = new AccountList(ListPath);
        var window = Show(accounts, new AccountsViewModel(accounts));

        // No client id in a test build, so this must be off rather than a button that fails when used.
        Assert.False(Button(window, "Sign in with Microsoft").IsEnabled);

        // Nothing selected yet.
        Assert.False(Button(window, "Remove").IsEnabled);
        Assert.False(Button(window, "Set as default").IsEnabled);

        // Offline needs a name first.
        Assert.False(Button(window, "Add offline account").IsEnabled);
    }

    [AvaloniaFact]
    public void AddingAnOfflineAccountThroughTheWindowWritesItToTheFile()
    {
        /*
         * ASSERTED ON THE FILE. The view model tests already prove the list changes; what this adds is
         * that the window, its bindings, the autosave and the path all line up -- which is the part
         * that decides whether an account is still there tomorrow.
         */
        var accounts = new AccountList(ListPath) { Autosave = true };
        var model = new AccountsViewModel(accounts);
        var window = Show(accounts, model);

        model.OfflineName = "Steve";

        Settle(window);

        var add = Button(window, "Add offline account");

        Assert.True(add.IsEnabled);

        add.Command!.Execute(null);

        Settle(window);

        Assert.True(File.Exists(ListPath));
        Assert.Contains("Steve", File.ReadAllText(ListPath), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheAccountAppearsAsARowAndCanBeMadeTheDefault()
    {
        var accounts = new AccountList(ListPath);
        var model = new AccountsViewModel(accounts);
        var window = Show(accounts, model);

        model.OfflineName = "Steve";
        model.AddOffline();

        Settle(window);

        var list = window.GetLogicalDescendants().OfType<ListBox>().Single();

        Assert.Single(list.ItemsSource!.Cast<object>());

        // Selecting through the control, not the model, so the two-way binding is what is tested.
        list.SelectedIndex = 0;

        Settle(window);

        Assert.NotNull(model.Selected);

        var setDefault = Button(window, "Set as default");

        Assert.True(setDefault.IsEnabled);

        setDefault.Command!.Execute(null);

        Settle(window);

        Assert.Equal("Steve", model.DefaultAccount?.ProfileName);
        Assert.False(setDefault.IsEnabled);
    }

    [AvaloniaFact]
    public void ClosingTheWindowSavesEvenWithoutAutosave()
    {
        // A sign-in that landed seconds before the window shut is exactly what must not be lost.
        var accounts = new AccountList(ListPath);
        var model = new AccountsViewModel(accounts);
        var window = Show(accounts, model);

        model.OfflineName = "Steve";
        model.AddOffline();

        Assert.False(File.Exists(ListPath));

        window.Close();

        Assert.True(File.Exists(ListPath));
        Assert.Contains("Steve", File.ReadAllText(ListPath), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheSignInPanelIsHiddenUntilASignInStarts()
    {
        var accounts = new AccountList(ListPath);
        var model = new AccountsViewModel(accounts);
        var window = Show(accounts, model);

        var cancel = Button(window, "Cancel");

        // Present in the tree but not shown: the panel it lives in is collapsed until needed.
        Assert.False(cancel.IsEffectivelyVisible);
    }
}
