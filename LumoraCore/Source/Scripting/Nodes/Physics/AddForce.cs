// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Pushes the rigid body the slot is part of, its own or the nearest above it, for this step, in newtons,
// world space.
[Node("Physics/Forces/Add Force", Capability.Physics, Description = "Pushes the rigid body the slot is part of for this step, in newtons, world space.",
    Keywords = "push, thrust, physics force, propel")]
public sealed class AddForce : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float3 force)
    {
        if (ScriptPhysics.Body(ctx, slot) is { } body)
            body.AddForce(ScriptWrites.Finite(force, "force"));
    }
}
