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
 * Adapts the launch layer's version fetcher to the interface the view models declare.
 *
 * The two are deliberately not the same type: MetaVersionListSource lives in Launch, and Launch must
 * not depend on ViewModels. The adapter is one method, and it lives here -- in the only project that
 * already references both -- rather than being written out twice, once per dialog that needs it.
 */

using ExtremeLauncher.Launch;
using ExtremeLauncher.Meta;
using ExtremeLauncher.ViewModels;

namespace ExtremeLauncher.App;

public sealed class AppVersionListSource(MetaVersionListSource versions) : IVersionListSource
{
    public Task<IReadOnlyList<MetaVersion>> LoadAsync(string uid, CancellationToken cancellationToken)
        => versions.LoadAsync(uid, cancellationToken);
}
