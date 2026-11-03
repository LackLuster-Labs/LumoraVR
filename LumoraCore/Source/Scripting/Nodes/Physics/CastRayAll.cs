// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Every collider along a ray from origin, no further than range metres, nearest first: the
// slots struck, the points, the surface normals there and how far along each is, as lists that line up.
// Triggers only when asked. Users only finds people instead, by the bodies the searches find them by,
// and nothing else stops it. Ignore leaves out a slot and everything under it. With a root, origin,
// direction and range are in that slot's space and the hits still come back in world space. Each ray it
// casts counts against the script's rays for the frame: one per thing struck and one more to find the
// end, or one in all for people. Every flow step that reads it casts again, so a graph that walks the
// hits keeps the lists in stored values first.
[Node("Physics/Queries/Raycast All", Capability.Physics, Description = "Every collider along a ray from origin, no further than range metres, nearest first: the slots struck, the points, the surface normals there and how far along each is, as lists that line up.",
    Keywords = "all hits, pierce, every hit")]
public sealed class CastRayAll : ReadNode
{
    [return: Port("count")]
    public static int Run(ScriptContext ctx, float3 origin, [Default(0, 0, 1)] float3 direction, out ScriptList<Slot> hits, out ScriptList<float3> points,
        out ScriptList<float3> normals, out ScriptList<float> distances, float range = 10f, bool triggers = false, bool usersOnly = false,
        Slot? ignore = null, Slot? root = null)
    {
        hits = new ScriptList<Slot>(ctx.Collections);
        points = new ScriptList<float3>(ctx.Collections);
        normals = new ScriptList<float3>(ctx.Collections);
        distances = new ScriptList<float>(ctx.Collections);
        if (!ScriptRaySpace.ToWorld(ctx, root, ref origin, ref direction, ref range) || !ScriptPhysics.CanCast(origin, direction, range))
            return 0;
        ctx.CountCast();
        var along = direction.Normalized;
        var ignoring = ScriptPhysics.Ignoring(ctx, ignore);
        var found = new List<PhysicsRaycastHit>();
        if (usersOnly)
            ScriptPhysics.StrikePeople(ctx, origin, along, range, ignoring, found);
        else
            ScriptPhysics.StrikeAll(ctx, origin, along, range, triggers, ignoring, found);
        foreach (var hit in found)
        {
            hits.Add(hit.Slot!);
            points.Add(hit.Point);
            normals.Add(hit.Normal);
            distances.Add(hit.Distance);
        }
        return found.Count;
    }
}
