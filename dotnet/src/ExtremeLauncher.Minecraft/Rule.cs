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
 * Ported from launcher/minecraft/Rule.{h,cpp}.
 *
 * Mojang's version JSON gates each library behind a list of rules, e.g. "allow on osx, disallow on
 * osx 10.5.*". Each rule either applies or does not; one that does not apply returns Defer, meaning
 * "no opinion". The rules are evaluated in order and the LAST one with an opinion wins -- so a
 * trailing disallow overrides a leading allow.
 *
 * The starting verdict when any rules exist at all is Disallow, which is why a version JSON listing
 * only "allow on linux" excludes the library everywhere else.
 */

using System.Text.Json.Nodes;
using ExtremeLauncher.Core;

namespace ExtremeLauncher.Minecraft;

public enum RuleAction
{
    Allow,
    Disallow,

    /// <summary>The rule does not apply here and expresses no opinion.</summary>
    Defer,
}

public abstract class Rule
{
    protected Rule(RuleAction result) => Result = result;

    protected RuleAction Result { get; }

    public RuleAction Apply(Library? parent, RuntimeContext runtimeContext)
        => Applies(parent, runtimeContext) ? Result : RuleAction.Defer;

    public abstract JsonObject ToJson();

    protected abstract bool Applies(Library? parent, RuntimeContext runtimeContext);
}

/// <summary>A rule gated on the operating system (and optionally its version).</summary>
public sealed class OsRule : Rule
{
    private OsRule(RuleAction result, string system, string versionRegex) : base(result)
    {
        System = system;
        VersionRegex = versionRegex;
    }

    public string System { get; }

    /// <summary>
    /// Retained for round-tripping but not evaluated, matching upstream -- <c>applies()</c> only ever
    /// consults the OS.
    /// </summary>
    public string VersionRegex { get; }

    public static OsRule Create(RuleAction result, string system, string versionRegex = "")
        => new(result, system, versionRegex);

    public override JsonObject ToJson()
    {
        var os = new JsonObject { ["name"] = System };

        if (VersionRegex.Length != 0)
        {
            os["version"] = VersionRegex;
        }

        return new JsonObject
        {
            ["action"] = Result == RuleAction.Allow ? "allow" : "disallow",
            ["os"] = os,
        };
    }

    protected override bool Applies(Library? parent, RuntimeContext runtimeContext)
        => runtimeContext.ClassifierMatches(System);
}

/// <summary>A rule with no conditions, which therefore always applies.</summary>
public sealed class ImplicitRule : Rule
{
    private ImplicitRule(RuleAction result) : base(result)
    {
    }

    public static ImplicitRule Create(RuleAction result) => new(result);

    public override JsonObject ToJson()
        => new() { ["action"] = Result == RuleAction.Allow ? "allow" : "disallow" };

    protected override bool Applies(Library? parent, RuntimeContext runtimeContext) => true;
}

public static class RuleParser
{
    /// <summary>Reads the "rules" array of a Mojang v4 library object.</summary>
    public static List<Rule> RulesFromJsonV4(JsonObject objectWithRules)
    {
        var result = new List<Rule>();

        if (objectWithRules["rules"] is not JsonArray rules)
        {
            return result;
        }

        foreach (var element in rules)
        {
            if (element is not JsonObject rule)
            {
                continue;
            }

            var actionText = Json.EnsureString(rule, "action");

            var action = actionText switch
            {
                "allow" => RuleAction.Allow,
                "disallow" => RuleAction.Disallow,
                _ => RuleAction.Defer,
            };

            // An unrecognised action is skipped rather than guessed at.
            if (action == RuleAction.Defer)
            {
                continue;
            }

            if (rule["os"] is JsonObject os)
            {
                result.Add(OsRule.Create(
                    action,
                    Json.EnsureString(os, "name"),
                    Json.EnsureString(os, "version")));

                continue;
            }

            result.Add(ImplicitRule.Create(action));
        }

        return result;
    }
}
