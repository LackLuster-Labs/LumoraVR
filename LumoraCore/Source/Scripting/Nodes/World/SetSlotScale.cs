// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Sizes a slot to a world-space scale, written as its local scale. A zero axis, or one that is not a
// finite number, is a fault, not a write. A parent's zero axis divides as one.
[Node("Transform/Global/Set Scale", Capability.WorldWrite, Description = "Sizes a slot to a world-space scale, written as its local scale.",
    Keywords = "resize, size, grow, shrink, world scale")]
public sealed class SetSlotScale : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, [Default(1)] float3 scale)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return;
        ScriptWrites.Scale(scale);
        var parent = slot!.Parent;
        ScriptWrites.Set(ctx, slot.LocalScale, ScriptWrites.Scale(parent == null || parent.IsRootSlot ? scale : ScriptSpaces.Scale(null, parent, scale)));
    }
}
