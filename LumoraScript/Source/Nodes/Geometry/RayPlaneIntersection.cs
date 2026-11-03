// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Where a ray from origin along direction meets the plane through planePoint facing planeNormal. hit
// is true when the plane is ahead. point and distance are where the ray's endless line meets the plane
// even when that is behind the origin, distance then below zero, so the node also serves as a line and
// plane test. A ray lying in the plane hits at its origin; one running beside it, a zero direction or a
// zero normal meets it nowhere and reads zeros.
[Node("Math/Geometry 3D/Ray Plane Intersection", Description = "Finds where a ray meets a plane. Hit is true only when the intersection is ahead of the ray's origin.")]
public sealed class RayPlaneIntersection : ValueNode
{
    public static void Run(float3 origin, [Default(0, -1, 0)] float3 direction, float3 planePoint, [Default(0, 1, 0)] float3 planeNormal,
        out float3 point, out float distance, out bool hit)
    {
        point = float3.Zero;
        distance = 0f;
        hit = false;
        var d = direction.Normalized;
        var n = planeNormal.Normalized;
        if (d.LengthSquared == 0f || n.LengthSquared == 0f)
            return;

        var toPlane = planePoint - origin;
        float height = float3.Dot(toPlane, n);
        float closing = float3.Dot(d, n);
        if (MathF.Abs(closing) < 1e-6f)
        {
            if (!(MathF.Abs(height) <= 1e-6f * MathF.Max(1f, toPlane.Length)))
                return;
            point = origin;
            hit = true;
            return;
        }

        float t = height / closing;
        if (!float.IsFinite(t))
            return;
        point = origin + d * t;
        distance = t;
        hit = t >= 0f;
    }
}
