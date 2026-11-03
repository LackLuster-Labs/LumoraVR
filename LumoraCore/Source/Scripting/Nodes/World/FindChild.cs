// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The nearest slot under slot with exactly this name, searched level by level. Partial takes any name
// holding it, ignore case takes either case, and max depth stops the search that many levels down, 1
// for children only, 0 for no limit.
[Node("World/Slots/Searching/Find Child", Capability.WorldRead, Description = "The nearest slot under slot with exactly this name, searched level by level.",
    Keywords = "find by name, search child, child by name, get child by name, lookup")]
[Evaluates(Evaluation.Tree)]
public sealed class FindChild : ReadNode
{
    // Kept between asks: a script asking every frame made two lists and grew them through the whole tree each
    // time.
    [ThreadStatic]
    private static List<Slot>? _level;
    [ThreadStatic]
    private static List<Slot>? _next;

    [return: Port("child")]
    public static Slot? Run(ScriptContext ctx, Slot? slot, string? name, bool partial = false, bool ignoreCase = false, int maxDepth = 0)
    {
        if (!ScriptReads.Visible(ctx, slot) || string.IsNullOrEmpty(name))
            return null;

        var key = new ScriptSearchCache.Key(slot!, name,
            (byte)((partial ? ScriptSearchCache.ByNamePartial : 0) | (ignoreCase ? ScriptSearchCache.ByNameAnyCase : 0)), maxDepth);
        if (ctx.Searches.TryGet(key, out var known))
            return known;
        var found = Search(ctx, slot!, name, partial, ignoreCase, maxDepth);
        ctx.Searches.Keep(key, found);
        return found;
    }

    private static Slot? Search(ScriptContext ctx, Slot slot, string name, bool partial, bool ignoreCase, int maxDepth)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var level = _level ??= new List<Slot>();
        var next = _next ??= new List<Slot>();
        level.Clear();
        next.Clear();
        level.Add(slot);
        try
        {
            for (int depth = 1; level.Count > 0 && (maxDepth <= 0 || depth <= maxDepth); depth++)
            {
                foreach (var current in level)
                {
                    foreach (var child in current.Children)
                    {
                        if (!ScriptReads.Visible(ctx, child))
                            continue;
                        var childName = child.Name.Value ?? string.Empty;
                        if (partial ? childName.Contains(name, comparison) : string.Equals(childName, name, comparison))
                            return child;
                        next.Add(child);
                    }
                }
                (level, next) = (next, level);
                next.Clear();
            }
            return null;
        }
        finally
        {
            level.Clear();
            next.Clear();
        }
    }
}
