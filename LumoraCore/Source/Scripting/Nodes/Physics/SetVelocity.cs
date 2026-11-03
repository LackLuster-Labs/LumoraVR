// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// How the rigid body the slot is part of, its own or the nearest above it, is moving, in metres and radians
// a second, world space.
[Node("Physics/Forces/Set Velocity", Capability.Physics, Description = "Sets how the rigid body the slot is part of is moving, in metres and radians a second, world space.",
    Keywords = "speed, physics velocity, launch, stop moving")]
public sealed class SetVelocity : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float3 linear, float3 angular)
    {
        var body = ScriptPhysics.Body(ctx, slot);
        body?.SetVelocities(ScriptWrites.Finite(linear, "linear"), ScriptWrites.Finite(angular, "angular"), bounded: false);
    }
}
