// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Where a ray from origin along direction first meets the segment from a to b, and how far along the ray
// that is. A ray running down the segment's own line meets it at its nearest point ahead, or at the
// origin when it starts on it, and a segment with no length is a point the ray can pass through. No hit,
// or a zero direction, reads zeros.
[Node("Math/Geometry 2D/Ray Segment Intersection", Description = "Where a ray from origin along direction first meets the segment from a to b, and how far along the ray that is.")]
public sealed class RaySegmentIntersection : ValueNode
{
    // How far past its ends a crossing still counts, as a share of the segment: float rounding at a
    // shared corner must not let a ray slip between two segments.
    private const float Slack = 1e-6f;

    public static void Run(float2 origin, [Default(1, 0)] float2 direction, float2 a, float2 b, out float2 point, out float distance, out bool hit)
    {
        point = float2.Zero;
        distance = 0f;
        hit = false;
        var d = direction.Normalized;
        if (d.LengthSquared == 0f)
            return;

        var along = b - a;
        var toA = a - origin;
        float span = along.Length;
        float across = Cross(d, along);
        float t;
        if (MathF.Abs(across) > 1e-6f * span)
        {
            t = Cross(toA, along) / across;
            float s = Cross(toA, d) / across;
            if (!(t >= 0f) || !(s >= -Slack) || !(s <= 1f + Slack))
                return;
        }
        else
        {
            float scale = MathF.Max(1f, MathF.Max(toA.Length, span));
            if (!(MathF.Abs(Cross(toA, d)) <= 1e-6f * scale))
                return;
            float ta = float2.Dot(toA, d), tb = float2.Dot(b - origin, d);
            if (!(MathF.Max(ta, tb) >= 0f))
                return;
            t = MathF.Max(0f, MathF.Min(ta, tb));
        }

        point = origin + d * t;
        distance = t;
        hit = true;
    }

    private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
}
