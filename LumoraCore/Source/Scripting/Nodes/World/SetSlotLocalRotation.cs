// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// In the parent's space. A rotation that is not a finite, non-zero quaternion is a fault, not a write.
[Node("Transform/Local/Set Local Rotation", Capability.WorldWrite, Description = "Turns a slot to a rotation relative to its parent.")]
public sealed class SetSlotLocalRotation : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, [Default(0, 0, 0, 1)] floatQ rotation)
    {
        if (ScriptReads.Reached(ctx, slot))
            ScriptWrites.Set(ctx, slot!.LocalRotation, ScriptWrites.Unit(rotation));
    }
}
