// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Null past the last child.
[Node("World/Slots/Hierarchy/Child", Capability.WorldRead, Description = "Gets a slot's child at the given index, starting at zero. Returns null when the index is outside the child list.",
    Keywords = "child at index, nth child")]
[Evaluates(Evaluation.Tree)]
public sealed class ChildAt : ReadNode
{
    [return: Port("child")]
    public static Slot? Run(ScriptContext ctx, Slot? slot, int index)
    {
        if (!ScriptReads.Visible(ctx, slot))
            return null;
        var children = slot!.Children;
        if (index < 0 || index >= children.Count)
            return null;
        var child = children[index];
        return ScriptReads.Visible(ctx, child) ? child : null;
    }
}
