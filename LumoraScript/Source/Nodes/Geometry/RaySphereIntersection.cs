// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Where a ray from origin along direction first reaches a sphere's surface, and how far along the ray:
// on the way in from outside, or on the way out when it starts inside. The direction's length does not
// matter. A negative radius counts as its size. No hit, or a zero direction, reads zeros.
[Node("Math/Geometry 3D/Ray Sphere Intersection", Description = "Where a ray from origin along direction first reaches a sphere's surface, and how far along the ray: on the way in from outside, or on the way out when it starts inside.")]
public sealed class RaySphereIntersection : ValueNode
{
    // The outputs sit before radius only so radius can have a default, the size of a new sphere mesh; the
    // port order is unchanged. -xlinka
    public static void Run(float3 origin, [Default(0, 0, 1)] float3 direction, float3 center, out float3 point, out float distance, out bool hit,
        float radius = 0.5f)
    {
        point = float3.Zero;
        distance = 0f;
        hit = false;
        if (!Cast(origin, direction, center, MathF.Abs(radius), out float t))
            return;
        point = origin + direction.Normalized * t;
        distance = t;
        hit = true;
    }

    internal static bool Cast(float3 origin, float3 direction, float3 center, float radius, out float t)
    {
        t = 0f;
        var d = direction.Normalized;
        if (d.LengthSquared == 0f)
            return false;
        var fromCenter = origin - center;
        float b = float3.Dot(fromCenter, d);
        float c = float3.Dot(fromCenter, fromCenter) - radius * radius;
        if (c > 0f && b > 0f)
            return false;
        float disc = b * b - c;
        if (!(disc >= 0f))
            return false;
        float root = MathF.Sqrt(disc);
        t = -b - root;
        if (t < 0f)
            t = -b + root;
        return t >= 0f;
    }
}
