// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Kicks the rigid body the slot is part of, its own or the nearest above it, at once, in newton seconds,
// world space.
[Node("Physics/Forces/Add Impulse", Capability.Physics, Description = "Kicks the rigid body the slot is part of at once, in newton seconds, world space.",
    Keywords = "push, launch, kick, throw, knockback, jump")]
public sealed class AddImpulse : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, float3 impulse)
    {
        if (ScriptPhysics.Body(ctx, slot) is { } body)
            body.AddImpulse(ScriptWrites.Finite(impulse, "impulse"));
    }
}
