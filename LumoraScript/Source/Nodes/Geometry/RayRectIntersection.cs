// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Where a ray from origin along direction first reaches the edge of a rect: on the way in from outside,
// or on the way out when it starts inside, so a ray from a rect's middle finds where its border lies in
// that direction. distance is how far along the ray. A rect of negative size spans the same ground as
// its flipped self. No hit, or a zero direction, reads zeros.
[Node("Math/Geometry 2D/Ray Rect Intersection", Description = "Finds where a 2D ray first reaches a rectangle's edge, entering from outside or leaving from inside.")]
public sealed class RayRectIntersection : ValueNode
{
    public static void Run(float2 origin, [Default(1, 0)] float2 direction, Rect rect, out float2 point, out float distance, out bool hit)
    {
        point = float2.Zero;
        distance = 0f;
        hit = false;
        var d = direction.Normalized;
        if (d.LengthSquared == 0f)
            return;

        float near = float.NegativeInfinity, far = float.PositiveInfinity;
        if (!Slab(origin.x, d.x, rect.x, rect.x + rect.width, ref near, ref far)
            || !Slab(origin.y, d.y, rect.y, rect.y + rect.height, ref near, ref far)
            || !(far >= 0f))
            return;

        float t = near >= 0f ? near : far;
        point = origin + d * t;
        distance = t;
        hit = true;
    }

    // Narrows the stretch of the ray between the two lines an axis bounds; false once nothing is left.
    private static bool Slab(float origin, float direction, float edgeA, float edgeB, ref float near, ref float far)
    {
        float min = MathF.Min(edgeA, edgeB), max = MathF.Max(edgeA, edgeB);
        if (direction == 0f)
            return origin >= min && origin <= max;
        float t0 = (min - origin) / direction, t1 = (max - origin) / direction;
        if (t0 > t1)
            (t0, t1) = (t1, t0);
        near = MathF.Max(near, t0);
        far = MathF.Min(far, t1);
        return near <= far;
    }
}
