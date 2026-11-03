// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Twists the rigid body the slot is part of, its own or the nearest above it, for this step, in newton
// metres, world space.
[Node("Physics/Forces/Add Torque", Capability.Physics, Description = "Twists the rigid body the slot is part of for this step, in newton metres, world space.",
    Keywords = "spin, twist, rotate physics")]
public sealed class AddTorque : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float3 torque)
    {
        if (ScriptPhysics.Body(ctx, slot) is { } body)
            body.AddTorque(ScriptWrites.Finite(torque, "torque"));
    }
}
