// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Turns a slot so its forward (+z) points at a world-space point, its up as near to up as it can. An up
// of zero means world up, and an up along the line of sight is swapped for one across it. A point the
// slot stands on changes nothing and fails. The library's LookRotation hands back the inverse, hence the
// flip.
[Node("Transform/Directions/Look At", Capability.WorldWrite, Description = "Turns a slot so its forward (+z) points at a world-space point, its up as near to up as it can.",
    Keywords = "rotate towards, face, aim at, point at, turn towards")]
public sealed class LookAt : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float3 target, [Default(0, 1, 0)] float3 up)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return;
        var forward = ScriptWrites.Finite(target, "target") - slot!.GlobalPosition;
        if (forward.LengthSquared < 1e-12f)
        {
            ctx.Fail("the slot stands on the point it should look at");
            return;
        }
        forward = forward.Normalized;
        up = ScriptWrites.Finite(up, "up");
        if (up.LengthSquared < 1e-12f)
            up = float3.Up;
        if (MathF.Abs(float3.Dot(forward, up.Normalized)) > 0.9999f)
            up = MathF.Abs(forward.z) < 0.9f ? float3.Forward : float3.Right;

        var rotation = floatQ.LookRotation(forward, up).Inverse;
        var parent = slot.Parent;
        ScriptWrites.Set(ctx, slot.LocalRotation, ScriptWrites.Unit(parent == null ? rotation : parent.GlobalRotation.Inverse * rotation));
    }
}
