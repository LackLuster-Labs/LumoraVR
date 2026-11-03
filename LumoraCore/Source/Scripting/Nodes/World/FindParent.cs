// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The nearest slot above slot with exactly this name, its parent first. Partial takes any name holding it,
// ignore case takes either case, and max depth stops the search that many levels up, 1 for the parent
// only, 0 for no limit. An empty name finds nothing.
[Node("World/Slots/Searching/Find Parent", Capability.WorldRead, Description = "The nearest slot above slot with exactly this name, its parent first.")]
[Evaluates(Evaluation.Tree)]
public sealed class FindParent : ReadNode
{
    [return: Port("parent")]
    public static Slot? Run(ScriptContext ctx, Slot? slot, string? name, bool partial = false, bool ignoreCase = false, int maxDepth = 0)
    {
        if (!ScriptReads.Visible(ctx, slot) || string.IsNullOrEmpty(name))
            return null;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int depth = 1;
        for (var current = slot!.Parent; current != null && (maxDepth <= 0 || depth <= maxDepth); current = current.Parent, depth++)
        {
            if (!ScriptReads.Visible(ctx, current))
                return null;
            var parentName = current.Name.Value ?? string.Empty;
            if (partial ? parentName.Contains(name, comparison) : string.Equals(parentName, name, comparison))
                return current;
        }
        return null;
    }
}
