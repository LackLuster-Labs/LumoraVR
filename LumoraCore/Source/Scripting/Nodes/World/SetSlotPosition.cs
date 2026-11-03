// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves a slot to a world-space point, written as its local position like any other move. A point that
// is not a finite number is a fault, not a write. Under a parent flattened on an axis the point is carried
// in the way Convert Point carries it, that axis divided as one.
[Node("Transform/Global/Set Position", Capability.WorldWrite, Description = "Moves a slot to a world-space point, written as its local position like any other move.",
    Keywords = "move, teleport, world position, global position, place, set transform")]
public sealed class SetSlotPosition : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float3 position)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return;
        ScriptWrites.Finite(position, "position");
        ScriptWrites.Set(ctx, slot!.LocalPosition, ScriptSpaces.Point(null, slot.Parent, position));
    }
}
