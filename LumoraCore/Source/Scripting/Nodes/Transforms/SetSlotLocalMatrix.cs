// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Places a slot in its parent's space by a matrix, decomposed into the position, rotation and scale it
// writes, each judged exactly as Set Local Position, Set Local Rotation and Set Local Scale judge a write.
// All three are checked before any is written, so a refusal changes nothing. A matrix with shear or a
// projective bottom row is placed by the parts Decompose reads from it. One that flattens an axis, or
// holds a number that is not finite, is a fault, not a write.
[Node("Transform/Matrices/Set Local Matrix", Capability.WorldWrite, Description = "Places a slot in its parent's space by a matrix, decomposed into the position, rotation and scale it writes, each judged exactly as Set Local Position, Set Local Rotation and Set Local Scale judge a write.")]
public sealed class SetSlotLocalMatrix : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, [Default(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)] float4x4 matrix)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return;
        matrix.Decompose(out var position, out var rotation, out var scale);
        ScriptWrites.Finite(position, "position");
        var unit = ScriptWrites.Unit(rotation);
        ScriptWrites.Scale(scale);

        ScriptWrites.Check(ctx, slot!.LocalPosition);
        ScriptWrites.Check(ctx, slot.LocalRotation);
        ScriptWrites.Check(ctx, slot.LocalScale);
        ScriptWrites.Write(ctx, slot.LocalPosition, position);
        ScriptWrites.Write(ctx, slot.LocalRotation, unit);
        ScriptWrites.Write(ctx, slot.LocalScale, scale);
    }
}
