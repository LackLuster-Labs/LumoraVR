// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The first collider along a ray from origin, no further than range metres, triggers only when asked.
// Users only strikes people instead, by the bodies the searches find them by, and nothing else stops
// it; the slot it gives is the person's user root. Ignore leaves out a slot and everything under it.
// With a root, origin, direction and range are in that slot's space and the hit still comes back in
// world space. Hit Mesh Point turns a hit into the triangle and texture spot it struck.
[Node("Physics/Queries/Raycast", Capability.Physics, Description = "The first collider along a ray from origin, no further than range metres, triggers only when asked.",
    Keywords = "ray, hit test, cast, shoot, laser, line of sight")]
public sealed class CastRay : ReadNode
{
    [return: Port("hit")]
    public static bool Run(ScriptContext ctx, float3 origin, [Default(0, 0, 1)] float3 direction, out Slot? slot, out float3 point, out float3 normal,
        out float distance, float range = 10f, bool triggers = false, bool usersOnly = false, Slot? ignore = null, Slot? root = null)
    {
        if (!ScriptRaySpace.ToWorld(ctx, root, ref origin, ref direction, ref range) || !ScriptPhysics.CanCast(origin, direction, range))
            return ScriptPhysics.Report(ctx, false, default, out slot, out point, out normal, out distance);
        ctx.CountCast();
        var along = direction.Normalized;
        var ignoring = ScriptPhysics.Ignoring(ctx, ignore);
        PhysicsRaycastHit hit;
        bool struck = usersOnly
            ? ScriptPhysics.StrikePerson(ctx, origin, along, 0f, range, ignoring, out hit)
            : ScriptPhysics.StrikeFirst(ctx, origin, along, range, triggers, ignoring, out hit);
        return ScriptPhysics.Report(ctx, struck, hit, out slot, out point, out normal, out distance);
    }
}
