// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Sweeps a ball of radius metres from origin along direction, no further than range, and gives the
// first collider it would touch. Triggers only when asked. Users only touches people instead, by the
// bodies the searches find them by, and nothing else stops it; the slot it gives is the person's user
// root. Ignore leaves out a slot and everything under it: the ball sweeps on past it, at most eight
// sweeps, and each after the first counts as a ray.
[Node("Physics/Queries/Sphere Cast", Capability.Physics, Description = "Sweeps a ball of radius metres from origin along direction, no further than range, and gives the first collider it would touch.",
    Keywords = "sweep, thick ray, ball cast")]
public sealed class CastSphere : ReadNode
{
    [return: Port("hit")]
    public static bool Run(ScriptContext ctx, float3 origin, [Default(0, 0, 1)] float3 direction, out Slot? slot, out float3 point, out float3 normal,
        out float distance, float radius = 0.25f, float range = 10f, bool triggers = false, bool usersOnly = false, Slot? ignore = null)
    {
        if (!ScriptPhysics.CanCast(origin, direction, range) || !float.IsFinite(radius) || radius <= 0f)
            return ScriptPhysics.Report(ctx, false, default, out slot, out point, out normal, out distance);
        ctx.CountCast();
        var along = direction.Normalized;
        var ignoring = ScriptPhysics.Ignoring(ctx, ignore);
        PhysicsRaycastHit hit;
        bool struck = usersOnly
            ? ScriptPhysics.StrikePerson(ctx, origin, along, radius, range, ignoring, out hit)
            : ScriptPhysics.Sweep(ctx, origin, along, radius, range, triggers, ignoring, out hit);
        return ScriptPhysics.Report(ctx, struck, hit, out slot, out point, out normal, out distance);
    }
}
