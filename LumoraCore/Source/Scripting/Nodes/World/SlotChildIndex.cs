// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where a slot stands in its parent's child list, 0 for the first, the order Child, Children and For Each
// read. -1 for the world root, for a slot the script cannot see, and for a slot kept on this machine alone
// directly under a shared one, which that list does not hold.
[Node("World/Slots/Hierarchy/Child Index", Capability.WorldRead, Description = "Returns a slot's position in its parent's child list, starting at zero.")]
[Evaluates(Evaluation.Tree)]
public sealed class SlotChildIndex : ReadNode
{
    [return: Port("index")]
    public static int Run(ScriptContext ctx, Slot? slot)
    {
        if (!ScriptReads.Visible(ctx, slot) || slot!.Parent is not { } parent)
            return -1;
        var children = parent.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (ReferenceEquals(children[i], slot))
                return i;
        }
        return -1;
    }
}
