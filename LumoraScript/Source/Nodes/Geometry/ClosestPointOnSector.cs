// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The point of a solid spherical sector's surface nearest to point, whether point is inside, and how far
// point is from the sector, 0 inside. The sector is the part of a ball about center within angle degrees
// of direction: a cone with a rounded cap, and at 180 or more the whole ball. A point inside is nearest
// to the cap or to the cone side, whichever is closer. A zero direction faces +z, and a negative radius
// counts as its size. Unwired, radius is 1 and angle 45.
[Node("Math/Geometry 3D/Closest Point On Spherical Sector", Description = "The point of a solid spherical sector's surface nearest to point, whether point is inside, and how far point is from the sector, 0 inside.")]
public sealed class ClosestPointOnSector : ValueNode
{
    public static void Run(float3 center, [Default(0, 0, 1)] float3 direction, [Default(1)] float radius, [Default(45)] float angle,
        float3 point, out float3 closest, out float distance, out bool inside)
    {
        var axis = direction.Normalized;
        if (axis.LengthSquared == 0f)
            axis = float3.Forward;
        float r = MathF.Abs(radius);
        float spread = (float.IsNaN(angle) ? 0f : Math.Clamp(angle, 0f, 180f)) * (MathF.PI / 180f);

        // The half plane through the axis and the point: x along the axis, y out from it.
        var offset = point - center;
        float ahead = float3.Dot(offset, axis);
        var sideways = offset - axis * ahead;
        float away = sideways.Length;
        var outward = away > 0f ? sideways / away : Turns.Square(axis);
        var p = new float2(ahead, away);
        float length = p.Length;
        float polar = MathF.Atan2(away, ahead);
        var rim = new float2(MathF.Cos(spread) * r, MathF.Sin(spread) * r);

        var near = polar <= spread ? (length > 0f ? p * (r / length) : new float2(r, 0f)) : rim;
        if (spread < MathF.PI)
        {
            var onSide = Nearest(rim, p);
            if ((p - onSide).LengthSquared < (p - near).LengthSquared)
                near = onSide;
        }

        inside = length <= r && polar <= spread;
        closest = center + axis * near.x + outward * near.y;
        distance = inside ? 0f : float3.Distance(point, closest);
    }

    // The nearest point to p of the segment from the middle out to the rim.
    private static float2 Nearest(float2 rim, float2 p)
    {
        float lengthSquared = float2.Dot(rim, rim);
        if (!(lengthSquared > 0f))
            return float2.Zero;
        return rim * Math.Clamp(float2.Dot(p, rim) / lengthSquared, 0f, 1f);
    }
}
