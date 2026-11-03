// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves a slot to a place in its parent's child list, 0 for the first, an index past either end taking the
// first or last place. Child, Children, For Each and the layouts on the parent read the order it leaves, on
// every machine: the move is written as order offsets. Judged as a change to the slot's order, and to any
// sibling renumbered to make room. Never the world root, a protected slot, or anything in someone's body or
// hands.
[Node("World/Slots/Hierarchy/Set Child Index", Capability.WorldStructure, Description = "Moves a slot to a place in its parent's child list, 0 for the first, an index past either end taking the first or last place.",
    Keywords = "reorder, move up, move down, sort, child order")]
public sealed class SetSlotChildIndex : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, int index)
    {
        if (ScriptReads.Reached(ctx, slot))
            ScriptSlotWrites.SetChildIndex(ctx, slot!, index);
    }
}
