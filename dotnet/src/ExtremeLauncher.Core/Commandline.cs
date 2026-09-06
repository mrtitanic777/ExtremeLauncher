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
 *      Authors: Orochimarufan <orochimarufan.x3@gmail.com>
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
 * Ported from launcher/Commandline.{h,cpp}.
 *
 * Splits a string into arguments the way a shell would. This is what stands between a user typing
 *   -Dfoo="some path with spaces" -Xmx4G
 * into the JVM arguments box and the launcher passing four broken arguments to Java.
 *
 * NOT PORTED: the ParsingError / Parser / FlagStyle / ArgumentStyle machinery in Commandline.h. It is
 * an argument-parsing framework that upstream stopped using — nothing in the codebase constructs a
 * Parser, and the launcher's own command line goes through Qt's QCommandLineParser instead. Only
 * splitArgs has callers.
 */

using System.Text;

namespace ExtremeLauncher.Core;

public static class Commandline
{
    /// <summary>
    /// Splits a string into argv items the way a shell would.
    /// </summary>
    /// <remarks>
    /// A hand-written scanner rather than a regex, because the rules are stateful: a backslash escapes
    /// the next character, single and double quotes both open a quoted run that ends only at the same
    /// character, and unquoted runs of spaces separate arguments.
    ///
    /// THIS IS NOT POSIX SHELL SPLITTING, and the differences are inherited:
    ///
    ///   - The quote characters are CONSUMED, so <c>-Dfoo="a b"</c> becomes <c>-Dfoo=a b</c> as one
    ///     argument. That is what makes it usable for JVM arguments, where the quotes are the user's
    ///     way of saying "this space is part of the value" rather than something Java should see.
    ///   - A backslash only escapes INSIDE quotes. Outside them it is an ordinary character, so
    ///     <c>C:\Users\bob</c> survives unquoted.
    ///   - Single quotes are not literal the way POSIX makes them: a backslash escapes inside them
    ///     too, so <c>'a\'b'</c> gives <c>a'b</c>.
    ///   - An UNTERMINATED quote is not an error. Everything to the end of the string joins the
    ///     current argument. A user who typed one quote gets one long argument rather than a refusal.
    ///   - EMPTY ARGUMENTS ARE IMPOSSIBLE. <c>""</c> contributes nothing at all, because the final
    ///     append is guarded on the buffer being non-empty. A program that distinguishes an empty
    ///     argument from an absent one cannot be given one through this box.
    ///
    /// WINDOWS PATHS ARE THE TRAP, and only when quoted. <c>-Dfoo="C:\Users\bob"</c> comes out as
    /// <c>-Dfoo=C:Usersbob</c> — quoting the path to protect a space is exactly what destroys it.
    /// Users write forward slashes or double the backslashes. This is inherited behaviour that every
    /// existing instance.cfg was written against, so it stays.
    /// </remarks>
    public static List<string> SplitArgs(string args)
    {
        var argv = new List<string>();
        var current = new StringBuilder();

        var escape = false;

        // The quote character that opened the current run, or '\0' when not inside one.
        var inQuotes = '\0';

        foreach (var c in args)
        {
            if (escape)
            {
                current.Append(c);
                escape = false;
            }
            else if (inQuotes != '\0')
            {
                if (c == '\\')
                {
                    escape = true;
                }
                else if (c == inQuotes)
                {
                    inQuotes = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == ' ')
            {
                // Runs of spaces collapse, and so does a quoted-empty argument: the guard is what
                // makes an empty argument impossible to express.
                if (current.Length != 0)
                {
                    argv.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (c is '"' or '\'')
            {
                inQuotes = c;
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length != 0)
        {
            argv.Add(current.ToString());
        }

        return argv;
    }
}
