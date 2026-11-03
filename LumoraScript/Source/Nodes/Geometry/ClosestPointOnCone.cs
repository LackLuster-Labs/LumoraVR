// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The point of a solid cone's surface nearest to point, whether point is inside, and how far point is
// from the cone, 0 inside. The cone stands on its base with its tip up the rotation's +y and center half
// way between, the way the cone mesh builds one, so a cone mesh's slot position, rotation, height and
// radius describe the same cone. A point inside is nearest to the base or to the sloped side, whichever
// is closer. A negative height or radius counts as its size. Unwired, height is 1 and radius 0.5.
//
// Everything happens in the half plane through the axis and the point, where the cone is a triangle and
// its surface two segments: the base from the axis out to the rim and the side from the rim to the tip.
// The axis itself is inside the cone, never its surface. -xlinka
[Node("Math/Geometry 3D/Closest Point On Cone", Description = "The point of a solid cone's surface nearest to point, whether point is inside, and how far point is from the cone, 0 inside.")]
public sealed class ClosestPointOnCone : ValueNode
{
    public static void Run(float3 center, [Default(0, 0, 0, 1)] floatQ rotation, [Default(1)] float height, [Default(0.5)] float radius,
        float3 point, out float3 closest, out float distance, out bool inside)
    {
        var turn = Turns.Unit(rotation);
        float h = MathF.Abs(height), r = MathF.Abs(radius);
        var local = turn.Conjugate * (point - center);

        float up = local.y + h * 0.5f;
        var flat = new float2(local.x, local.z);
        float outward = flat.Length;
        var toward = outward > 0f ? flat / outward : new float2(1f, 0f);
        var p = new float2(outward, up);

        var onBase = new float2(Math.Clamp(outward, 0f, r), 0f);
        var onSide = Nearest(new float2(r, 0f), new float2(0f, h), p);
        var near = (p - onBase).LengthSquared <= (p - onSide).LengthSquared ? onBase : onSide;

        inside = h > 0f && r > 0f && up >= 0f && up <= h && outward <= r * (1f - up / h);
        closest = center + turn * new float3(toward.x * near.x, near.y - h * 0.5f, toward.y * near.x);
        distance = inside ? 0f : float3.Distance(point, closest);
    }

    private static float2 Nearest(float2 a, float2 b, float2 p)
    {
        var along = b - a;
        float lengthSquared = float2.Dot(along, along);
        if (!(lengthSquared > 0f))
            return a;
        float t = Math.Clamp(float2.Dot(p - a, along) / lengthSquared, 0f, 1f);
        return a + along * t;
    }
}
