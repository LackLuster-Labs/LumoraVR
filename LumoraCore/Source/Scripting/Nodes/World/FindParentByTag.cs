// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The nearest slot above slot whose tag is exactly this text, case included, its parent first. Max depth
// stops the search that many levels up, 1 for the parent only, 0 for no limit. An empty tag finds nothing.
[Node("World/Slots/Searching/Find Parent By Tag", Capability.WorldRead, Description = "The nearest slot above slot whose tag is exactly this text, case included, its parent first.")]
[Evaluates(Evaluation.Tree)]
public sealed class FindParentByTag : ReadNode
{
    [return: Port("parent")]
    public static Slot? Run(ScriptContext ctx, Slot? slot, string? tag, int maxDepth = 0)
    {
        if (!ScriptReads.Visible(ctx, slot) || string.IsNullOrEmpty(tag))
            return null;

        int depth = 1;
        for (var current = slot!.Parent; current != null && (maxDepth <= 0 || depth <= maxDepth); current = current.Parent, depth++)
        {
            if (!ScriptReads.Visible(ctx, current))
                return null;
            if (string.Equals(current.Tag.Value, tag, StringComparison.Ordinal))
                return current;
        }
        return null;
    }
}
