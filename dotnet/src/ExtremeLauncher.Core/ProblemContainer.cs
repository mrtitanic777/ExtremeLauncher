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
 * Ported from launcher/ProblemProvider.h.
 *
 * Non-fatal complaints collected while parsing. A version file with a broken library name still
 * loads -- the launcher would rather show the instance with a warning than refuse to open it -- so
 * problems accumulate here instead of throwing.
 */

namespace ExtremeLauncher.Core;

public enum ProblemSeverity
{
    None,
    Warning,
    Error,
}

public readonly record struct PatchProblem(ProblemSeverity Severity, string Description);

public interface IProblemProvider
{
    IReadOnlyList<PatchProblem> GetProblems();

    ProblemSeverity GetProblemSeverity();
}

public class ProblemContainer : IProblemProvider
{
    private readonly List<PatchProblem> _problems = [];

    public IReadOnlyList<PatchProblem> GetProblems() => _problems;

    /// <summary>The worst severity seen so far.</summary>
    public ProblemSeverity GetProblemSeverity() { return ProblemSeverity; }

    protected ProblemSeverity ProblemSeverity { get; private set; } = ProblemSeverity.None;

    public virtual void AddProblem(ProblemSeverity severity, string description)
    {
        if (severity > ProblemSeverity)
        {
            ProblemSeverity = severity;
        }

        _problems.Add(new PatchProblem(severity, description));
    }
}
