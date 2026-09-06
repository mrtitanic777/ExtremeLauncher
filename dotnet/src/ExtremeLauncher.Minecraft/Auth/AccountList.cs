// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *  Copyright (C) 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
 * This file incorporates work covered by the following copyright and
 * permission notice:
 *
 *      Copyright 2013-2021 MultiMC Contributors
 *
 *      Licensed under the Apache License, Version 2.0 (the "License");
 *      you may not use this file except in compliance with the License.
 *      You may obtain a copy of the License at
 *
 *          http://www.apache.org/licenses/LICENSE-2.0
 *
 *      Unless required by applicable law or agreed to in writing, software
 *      distributed under the License is distributed on an "AS IS" BASIS,
 *      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *      See the License for the specific language governing permissions and
 *      limitations under the License.
 *
 * Ported from launcher/minecraft/auth/AccountList.cpp.
 *
 * THE MISSING LINK. The whole MSA chain -- device code, Xbox, entitlements, profile, skins -- was
 * ported and tested waves ago and nothing could reach it, because nothing remembered an account
 * between two runs of the launcher. This is that: accounts.json, and the rules about what may go in it.
 *
 * Upstream's is a QAbstractListModel; this is a plain observable list, because the model half is Qt's
 * table plumbing (four columns, header text, a PointerRole) and Avalonia binds to the objects instead.
 * The behaviour that is not plumbing -- dedupe by profile id, replace-in-place on re-sign-in, the
 * default account, the format version -- is all here and all tested.
 *
 * THE FILE HOLDS REFRESH TOKENS. Two consequences that are easy to miss:
 *
 *   - it is written owner-only, as upstream does, so other users of a shared machine cannot read it;
 *   - it is written through a temp file, so a crash mid-write cannot leave a truncated one. Upstream
 *     gets this from QSaveFile. Losing this file means every account has to sign in again.
 */

using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft.Auth;

public sealed class AccountList
{
    /// <summary>The only format this understands. Upstream calls it MojangMSA.</summary>
    public const int FormatVersion = 3;

    private readonly List<MinecraftAccount> _accounts = [];

    private MinecraftAccount? _defaultAccount;

    public AccountList(string listFilePath = "") => ListFilePath = listFilePath;

    /// <summary>Raised when accounts are added, removed or replaced.</summary>
    public event EventHandler? ListChanged;

    /// <summary>Raised when the default account changes. Separate because the UI reacts differently.</summary>
    public event EventHandler? DefaultAccountChanged;

    public string ListFilePath { get; set; }

    /// <summary>
    /// Saves after every change when set.
    /// </summary>
    /// <remarks>
    /// Upstream warns that setting this before <see cref="Load"/> lets an autosave overwrite the list
    /// you were about to read. Same trap here, so the app sets it only after loading.
    /// </remarks>
    public bool Autosave { get; set; }

    public IReadOnlyList<MinecraftAccount> Accounts => _accounts;

    public MinecraftAccount? DefaultAccount
    {
        get => _defaultAccount;

        set
        {
            /*
             * Refused rather than accepted, because a default that is not in the list is a dangling
             * selection: the UI would show a signed-in user that no account row corresponds to.
             * Upstream reaches the same outcome by a longer route -- its loop simply never matches, so
             * the assignment does not happen -- and the effect is what is being kept.
             */
            if (value is not null && !_accounts.Contains(value))
            {
                return;
            }

            if (ReferenceEquals(_defaultAccount, value))
            {
                return;
            }

            _defaultAccount = value;

            OnDefaultAccountChanged();
        }
    }

    /// <summary>True when some account actually owns the game, so an online server is joinable.</summary>
    public bool AnyAccountIsValid => _accounts.Any(a => a.OwnsMinecraft);

    public MinecraftAccount? FindByProfileId(string profileId)
        => _accounts.FirstOrDefault(a => string.Equals(a.ProfileId, profileId, StringComparison.Ordinal));

    public MinecraftAccount? FindByProfileName(string profileName)
        => _accounts.FirstOrDefault(a => string.Equals(a.ProfileName, profileName, StringComparison.Ordinal));

    public void Add(MinecraftAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (_accounts.Contains(account))
        {
            return;
        }

        /*
         * REPLACED IN PLACE when the profile id already exists, rather than appended. Signing in again
         * to an account you already have is the ordinary way a stale token gets replaced, and appending
         * would leave two rows for one person, one of them dead.
         *
         * Only a NON-EMPTY id counts: an account part-way through signing in has none yet, and
         * treating those as equal would make a second sign-in eat the first.
         */
        if (account.ProfileId.Length != 0)
        {
            var existingIndex = _accounts.FindIndex(
                a => string.Equals(a.ProfileId, account.ProfileId, StringComparison.Ordinal));

            if (existingIndex != -1)
            {
                var replaced = _accounts[existingIndex];

                _accounts[existingIndex] = account;

                // Inherits being the default, or signing in again would silently deselect you.
                if (ReferenceEquals(_defaultAccount, replaced))
                {
                    _defaultAccount = account;

                    OnDefaultAccountChanged();
                }

                OnListChanged();

                return;
            }
        }

        _accounts.Add(account);

        OnListChanged();
    }

    public void Remove(MinecraftAccount account)
    {
        if (!_accounts.Remove(account))
        {
            return;
        }

        if (ReferenceEquals(_defaultAccount, account))
        {
            _defaultAccount = null;

            OnDefaultAccountChanged();
        }

        OnListChanged();
    }

    /// <returns>False when there was nothing to load, which includes every first run.</returns>
    public bool Load()
    {
        if (ListFilePath.Length == 0 || !File.Exists(ListFilePath))
        {
            return false;
        }

        JsonObject root;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(ListFilePath)) is not JsonObject parsed)
            {
                return false;
            }

            root = parsed;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            // Not thrown: a corrupt account list must not stop the launcher from starting. The
            // consequence is a sign-in, which is recoverable; refusing to start is not.
            return false;
        }

        var version = root["formatVersion"]?.GetValue<int>() ?? 0;

        if (version != FormatVersion)
        {
            /*
             * RENAMED, NOT DELETED, and not read either. Upstream moves it to accounts-old.json so a
             * newer launcher's file survives being opened by an older one -- it holds refresh tokens
             * somebody may well want back.
             */
            var backup = FileSystem.PathCombine(
                Path.GetDirectoryName(ListFilePath) ?? string.Empty,
                "accounts-old.json");

            try
            {
                File.Move(ListFilePath, backup, overwrite: true);
            }
            catch (IOException)
            {
            }

            return false;
        }

        return LoadV3(root);
    }

    private bool LoadV3(JsonObject root)
    {
        _accounts.Clear();
        _defaultAccount = null;

        if (root["accounts"] is not JsonArray accounts)
        {
            return true;
        }

        foreach (var entry in accounts)
        {
            if (entry is not JsonObject accountObject)
            {
                // One broken entry must not lose the others -- upstream warns and carries on.
                continue;
            }

            MinecraftAccount? account;

            try
            {
                account = MinecraftAccount.LoadFromJsonV3(accountObject);
            }
            // Core.JsonException is the port's own, thrown by the account parsers; the other is the reader's.
            catch (Exception e) when (e is Core.JsonException or System.Text.Json.JsonException
                or FormatException or InvalidOperationException)
            {
                continue;
            }

            if (account is null)
            {
                continue;
            }

            // A duplicate id can only arrive from a file somebody edited or a corrupt write; the
            // first one wins, matching upstream, so the outcome is at least deterministic.
            if (account.ProfileId.Length != 0 && FindByProfileId(account.ProfileId) is not null)
            {
                continue;
            }

            _accounts.Add(account);

            if (accountObject["active"]?.GetValue<bool>() == true)
            {
                _defaultAccount = account;
            }
        }

        return true;
    }

    public bool Save()
    {
        if (ListFilePath.Length == 0)
        {
            return false;
        }

        var accounts = new JsonArray();

        foreach (var account in _accounts)
        {
            var saved = account.SaveToJson();

            // Written ONLY on the default. Upstream omits the key on the others rather than writing
            // false, and something reading this file may reasonably test for the key's presence.
            if (ReferenceEquals(_defaultAccount, account))
            {
                saved["active"] = true;
            }

            accounts.Add(saved);
        }

        var root = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["accounts"] = accounts,
        };

        try
        {
            FileSystem.EnsureFilePathExists(ListFilePath);

            /*
             * A folder where the file should be is a real bug upstream had to fix, and the comment
             * survives in AccountList.cpp: "make sure the file wasn't overwritten with a folder before".
             */
            if (Directory.Exists(ListFilePath))
            {
                Directory.Delete(ListFilePath, recursive: true);
            }

            /*
             * THROUGH A TEMP FILE, which is what QSaveFile gives upstream. A crash between truncating
             * and writing would otherwise cost every signed-in account -- the one failure here that
             * cannot be undone by trying again.
             */
            var temporary = ListFilePath + ".tmp";

            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            RestrictToOwner(temporary);

            File.Move(temporary, ListFilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Takes away group and other access, because this file holds refresh tokens.
    /// </summary>
    /// <remarks>
    /// Upstream sets the same bits via QFile::setPermissions. Windows has no equivalent to set here --
    /// the file inherits the data directory's ACL, which is already per-user under %APPDATA%.
    /// </remarks>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A filesystem without permission bits is not a reason to lose the account list.
        }
    }

    private void OnListChanged()
    {
        ListChanged?.Invoke(this, EventArgs.Empty);

        if (Autosave)
        {
            Save();
        }
    }

    private void OnDefaultAccountChanged()
    {
        DefaultAccountChanged?.Invoke(this, EventArgs.Empty);

        if (Autosave)
        {
            Save();
        }
    }
}
