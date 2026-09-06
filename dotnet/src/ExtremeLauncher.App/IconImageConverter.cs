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
 * Lets XAML bind an Image straight to an IconEntry.
 *
 * A converter rather than a Bitmap property on the view model, so the view models stay free of
 * Avalonia types -- which is what keeps them testable without a rendering stack.
 */

using System.Globalization;
using Avalonia.Data.Converters;
using ExtremeLauncher.Launch;

namespace ExtremeLauncher.App;

public sealed class IconImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is IconEntry entry ? InstanceIcons.Load(entry) : null;

    /// <remarks>One-way: an Image is never edited into an icon key.</remarks>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
