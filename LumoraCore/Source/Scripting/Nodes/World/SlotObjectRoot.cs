// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot that stands for the whole object a slot is part of: the nearest at or above it carrying an
// Object Root, or a Grabbable as well unless only explicit is set, the way tools pick up a whole prop by
// any piece of it. The world root never counts. The slot itself when nothing above it does, and nothing
// for a slot the script cannot see.
[Node("World/Slots/Hierarchy/Object Root", Capability.WorldRead, Description = "Finds the root of the whole object containing this slot, using an Object Root marker or a grabbable parent.",
    Keywords = "root of object, prop root, top of item")]
[Evaluates(Evaluation.Tree)]
public sealed class SlotObjectRoot : ReadNode
{
    [return: Port("root")]
    public static Slot? Run(ScriptContext ctx, Slot? slot, bool onlyExplicit = false)
    {
        if (!ScriptReads.Visible(ctx, slot))
            return null;
        for (var current = slot; current != null && !current.IsRootSlot; current = current.Parent)
        {
            if (current.GetComponent<ObjectRoot>() is { IsDestroyed: false }
                || (!onlyExplicit && current.GetComponent<Grabbable>() is { IsDestroyed: false }))
                return ScriptReads.Visible(ctx, current) ? current : slot;
        }
        return slot;
    }
}
